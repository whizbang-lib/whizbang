using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Turning stored event rows back into typed envelopes, which is what the drain path does before
/// a perspective can see them.
/// </summary>
/// <remarks>
/// A row that fails to deserialize used to be skipped silently, and the result was a total read
/// failure presenting as "0 typed events" with nothing in the log — perspective completion
/// stalled for days behind it. So the contract has two halves that pull against each other: one
/// bad row must not block the batch, and it must never disappear quietly.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
[Category("Shard2")]
public class EFCoreEventStoreDeserializeTests : EFCoreTestBase {

  public record ProbeEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
  }

  private sealed class CapturingLogger : ILogger<EFCoreEventStore<WorkCoordinationDbContext>> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];
    public List<string> Messages { get { lock (_lock) { return [.. _messages]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }

  private static StreamEventData _row(string eventData, string? eventType = null) => new() {
    StreamId = (Guid)TrackedGuid.NewMedo(),
    EventId = (Guid)TrackedGuid.NewMedo(),
    EventType = eventType ?? typeof(ProbeEvent).AssemblyQualifiedName!,
    EventData = eventData,
    EventWorkId = (Guid)TrackedGuid.NewMedo(),
  };

  private static string _validPayload(string name = "probe")
    => JsonSerializer.Serialize(new ProbeEvent { Id = (Guid)TrackedGuid.NewMedo(), Name = name });

  [Test]
  public async Task NoRows_ProducesNoEnvelopesAsync() {
    // The common case on an idle drain — it must not do the type-lookup work for nothing.
    await using var ctx = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx);

    var result = store.DeserializeStreamEvents([], [typeof(ProbeEvent)]);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task AnUnknownEventType_IsSkippedWithoutFailingTheBatchAsync() {
    // A type this host does not have is ordinary in a multi-service store: another service owns
    // it. Skipping is correct — throwing would stop this host draining anything.
    await using var ctx = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx);

    var result = store.DeserializeStreamEvents(
      [_row(_validPayload(), eventType: "Some.Other.Service.Event, Other")],
      [typeof(ProbeEvent)]);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task AMalformedRow_DoesNotBlockTheGoodRowsBesideItAsync() {
    // The half that matters most: one corrupt row must not cost the whole batch, or a single
    // bad event stalls every perspective behind it.
    await using var ctx = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx);

    var result = store.DeserializeStreamEvents(
      [_row("{not json"), _row(_validPayload("good"))],
      [typeof(ProbeEvent)]);

    await Assert.That(result.Count).IsEqualTo(1)
      .Because("one corrupt row must not cost the batch — everything behind it would stall");
  }

  [Test]
  public async Task AMalformedRow_IsReportedAsync() {
    // The other half. A silent skip turned a total read failure into "0 typed events" with no
    // diagnostic once already; the log line is the only thing that distinguishes the two.
    await using var ctx = CreateDbContext();
    var logger = new CapturingLogger();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx, logger: logger);

    _ = store.DeserializeStreamEvents([_row("{not json")], [typeof(ProbeEvent)]);

    await Assert.That(logger.Messages.Any(m =>
      m.Contains("deserialize", StringComparison.OrdinalIgnoreCase))).IsTrue()
      .Because("a silent skip is indistinguishable from an empty stream, and that cost days once");
  }

  [Test]
  public async Task ManyMalformedRows_AreReportedInFullOnceThenCountedAsync() {
    // Logging every failure in a large corrupt batch would bury the log. The first is logged in
    // full with its exception; the rest are summarized.
    await using var ctx = CreateDbContext();
    var logger = new CapturingLogger();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx, logger: logger);

    var rows = Enumerable.Range(0, 10).Select(_ => _row("{not json")).ToList();
    var result = store.DeserializeStreamEvents(rows, [typeof(ProbeEvent)]);

    await Assert.That(result).IsEmpty();
    await Assert.That(logger.Messages).IsNotEmpty();
    await Assert.That(logger.Messages.Count).IsLessThan(rows.Count)
      .Because("logging every failure in a corrupt batch buries the one that explains it");
  }

  [Test]
  public async Task AValidRow_BecomesATypedEnvelopeAsync() {
    await using var ctx = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx);

    var result = store.DeserializeStreamEvents([_row(_validPayload("probe"))], [typeof(ProbeEvent)]);

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Payload).IsTypeOf<ProbeEvent>();
    await Assert.That(((ProbeEvent)result[0].Payload!).Name).IsEqualTo("probe");
  }

  [Test]
  public async Task ARowWithNoStoredMetadata_TakesItsMessageIdFromTheEventIdAsync() {
    // Rows written before metadata was stored still have to replay, and their identity has to
    // come from somewhere stable — the event id is the only thing guaranteed present.
    await using var ctx = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx);
    var row = _row(_validPayload());

    var result = store.DeserializeStreamEvents([row], [typeof(ProbeEvent)]);

    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].MessageId.Value).IsEqualTo(row.EventId);
  }

  [Test]
  public async Task ARowWhosePayloadDeserializesToNull_IsSkippedAsync() {
    // A stored literal "null" is not a usable event, and casting it to IEvent would throw
    // further down where the cause is no longer visible.
    await using var ctx = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx);

    var result = store.DeserializeStreamEvents([_row("null")], [typeof(ProbeEvent)]);

    await Assert.That(result).IsEmpty();
  }
}
