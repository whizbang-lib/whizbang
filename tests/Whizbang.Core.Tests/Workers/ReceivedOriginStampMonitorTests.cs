using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The receive path warns when stream integrity is on but received events arrive without the producer's
/// origin (#1029). The failure it watches for is silent by construction: an unstamped row is stored as
/// locally originated, and integrity simply never sees that traffic.
/// </summary>
/// <docs>resilience/stream-integrity#origin-stamp</docs>
public class ReceivedOriginStampMonitorTests {
  private static readonly Guid _producer = Guid.Parse("5d0c7a3e-91b2-4f68-a4d1-3c2b1a0f9e8d");

  private static InboxMessage _row(Guid source, bool isEvent = true, string messageType = "MyApp.Events.OrderShipped, MyApp") {
    var id = (Guid)TrackedGuid.New();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "OrderShippedHandler",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonDocument.Parse("{}").RootElement, []),
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core",
      MessageType = messageType,
      StreamId = id,
      IsEvent = isEvent,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
      SourceServiceId = source,
    };
  }

  private static (ReceivedOriginStampMonitor Monitor, FakeLogger<ReceivedOriginStampMonitor> Logger, FakeTimeProvider Time) _create(
      StreamIntegrityOptions? options = null) {
    var logger = new FakeLogger<ReceivedOriginStampMonitor>();
    var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
    return (new ReceivedOriginStampMonitor(Options.Create(options ?? new StreamIntegrityOptions()), logger, time), logger, time);
  }

  [Test]
  public async Task UnstampedEvents_WarnOnceTheWindowCloses_WithTheCountsAsync() {
    var (monitor, logger, time) = _create();

    monitor.Record(_row(Guid.Empty));
    monitor.Record(_row(_producer));
    await Assert.That(logger.Collector.Count).IsEqualTo(0)
      .Because("the window is still open");

    time.Advance(ReceivedOriginStampMonitor.Window);
    monitor.Record(_row(Guid.Empty));

    var record = logger.Collector.LatestRecord;
    await Assert.That(record.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(record.Message).Contains("2 of 3 events received in the last 5 minutes carried no origin service id");
  }

  [Test]
  public async Task StampedEvents_NeverWarnAsync() {
    var (monitor, logger, time) = _create();

    monitor.Record(_row(_producer));
    time.Advance(ReceivedOriginStampMonitor.Window);
    monitor.Record(_row(_producer));

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task EachWindow_StartsFromZeroAsync() {
    var (monitor, logger, time) = _create();

    monitor.Record(_row(Guid.Empty));
    time.Advance(ReceivedOriginStampMonitor.Window);
    monitor.Record(_row(Guid.Empty));   // closes the first window: 2 unstamped
    monitor.Record(_row(_producer));
    time.Advance(ReceivedOriginStampMonitor.Window);
    monitor.Record(_row(_producer));    // closes the second: nothing unstamped

    await Assert.That(logger.Collector.Count).IsEqualTo(1)
      .Because("a window whose receives were all stamped is quiet, whatever the previous window held");
  }

  [Test]
  public async Task CommandsAndControlPlane_AreNotCountedAsync() {
    var (monitor, logger, time) = _create();
    var controlPlane = TypeNameFormatter.Format(typeof(IntegrityCheckpoint));
    ControlPlaneTypeRegistry.Register(typeof(IntegrityCheckpoint));

    monitor.Record(_row(Guid.Empty, isEvent: false));
    monitor.Record(_row(Guid.Empty, messageType: controlPlane));
    time.Advance(ReceivedOriginStampMonitor.Window);
    monitor.Record(_row(Guid.Empty, messageType: controlPlane));

    await Assert.That(logger.Collector.Count).IsEqualTo(0)
      .Because("commands never reach the event store and control-plane traffic never carries an origin");
  }

  [Test]
  public async Task IntegrityOff_RecordsNothingAsync() {
    var (monitor, logger, time) = _create(new StreamIntegrityOptions { GapDetectionEnabled = false, AuditEnabled = false });

    monitor.Record(_row(Guid.Empty));
    time.Advance(ReceivedOriginStampMonitor.Window);
    monitor.Record(_row(Guid.Empty));

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task AuditAlone_IsEnoughToWatchAsync() {
    var (monitor, logger, time) = _create(new StreamIntegrityOptions { GapDetectionEnabled = false });

    monitor.Record(_row(Guid.Empty));
    time.Advance(ReceivedOriginStampMonitor.Window);
    monitor.Record(_row(Guid.Empty));

    await Assert.That(logger.Collector.Count).IsEqualTo(1);
  }

  /// <summary>A clock whose next read runs a hook first: the seam that puts a second receive inside the first one's window close.</summary>
  private sealed class InterleavingClock : TimeProvider {
    public long Now { get; set; }
    public Action? BeforeNextRead { get; set; }

    public override long GetTimestamp() {
      var hook = BeforeNextRead;
      BeforeNextRead = null;
      hook?.Invoke();
      return Now;
    }
  }

  [Test]
  public async Task TwoReceives_ClosingTheSameWindow_WarnOnceWithBothCountedAsync() {
    // The first receive reads the window start, then reads the clock to see whether the window closed. The
    // hook runs a second receive at exactly that point: it sees the same start, closes the window and warns.
    // The first receive then loses the swap of the window start and must neither warn again nor reset the
    // counts the second one just reported.
    var logger = new FakeLogger<ReceivedOriginStampMonitor>();
    var clock = new InterleavingClock();
    var monitor = new ReceivedOriginStampMonitor(Options.Create(new StreamIntegrityOptions()), logger, clock);
    clock.Now += (long)(ReceivedOriginStampMonitor.Window.TotalSeconds * clock.TimestampFrequency);
    clock.BeforeNextRead = () => monitor.Record(_row(Guid.Empty));

    monitor.Record(_row(Guid.Empty));

    await Assert.That(logger.Collector.Count).IsEqualTo(1)
      .Because("only the receive that wins the window close reports it");
    await Assert.That(logger.Collector.LatestRecord.Message).Contains("2 of 2 events");
  }

  [Test]
  public async Task TheRowBuilder_RecordsEveryReceivedRowAsync() {
    var (monitor, logger, time) = _create();
    var envelope = new MessageEnvelope<JsonElement>(MessageId.New(), JsonDocument.Parse("{}").RootElement, []);
    var received = new ReceivedInboxMessageBuilder.ReceivedEnvelope(envelope, envelope,
      "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.OrderShipped, MyApp]], Whizbang.Core",
      "MyApp.Events.OrderShipped, MyApp", IsEvent: true);

    ReceivedInboxMessageBuilder.Build(received, 150, "test", null, null, monitor);
    time.Advance(ReceivedOriginStampMonitor.Window);
    ReceivedInboxMessageBuilder.Build(received, 150, "test", null, null, monitor);

    await Assert.That(logger.Collector.LatestRecord.Message).Contains("2 of 2 events")
      .Because("both consumer workers build their rows here, so recording here covers every receive path");
  }

  [Test]
  public async Task Defaults_ResolveFromTheContainer_WithoutALoggerOrClockAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(new StreamIntegrityOptions()));
    services.AddSingleton<ReceivedOriginStampMonitor>();
    await using var provider = services.BuildServiceProvider();

    var monitor = provider.GetRequiredService<ReceivedOriginStampMonitor>();
    monitor.Record(_row(Guid.Empty));

    await Assert.That(monitor).IsNotNull();
  }
}
