using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The drain path must actually SIZE its fetch from the governor, not merely own one.
/// </summary>
/// <remarks>
/// A governor nothing reads is decoration: the unit tests pass, the value adapts, and every fetch
/// still goes out with the old constant. These drive the worker's own seams so the page size that
/// would be sent to the store is what is asserted.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDrainWorker.cs</code-under-test>
[Category("Workers")]
public class InboxDrainAdaptivePageWiringTests {

  private static InboxBatchRow _row(int attempts) => new() {
    MessageId = Guid.NewGuid(),
    StreamId = Guid.NewGuid(),
    HandlerName = "H",
    MessageType = "T",
    EventData = "{}",
    Metadata = "{}",
    Attempts = attempts,
  };

  private static IReadOnlyList<InboxBatchRow> _rows(int count, int attempts = 1)
    => [.. Enumerable.Range(0, count).Select(_ => _row(attempts))];

  private sealed class _Instance : Whizbang.Core.Observability.IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo()
      => new() {
        InstanceId = InstanceId,
        ServiceName = ServiceName,
        HostName = HostName,
        ProcessId = ProcessId,
      };
  }

  private sealed class _DrainChannel : IInboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _c =
      System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _c.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default)
      => _c.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _c.Writer.TryWrite(streamId);
  }

  private sealed class _InboxWriter : IInboxChannelWriter {
    private readonly System.Threading.Channels.Channel<InboxWork> _c =
      System.Threading.Channels.Channel.CreateUnbounded<InboxWork>();
    public System.Threading.Channels.ChannelReader<InboxWork> Reader => _c.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default)
      => _c.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _c.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _c.Writer.TryComplete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private static InboxDrainWorker _worker(InboxDrainWorkerOptions options) {
    var sp = new ServiceCollection().BuildServiceProvider();
    return new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _Instance(),
      new _DrainChannel(),
      new _InboxWriter(),
      new SchemaReadyGate(),
      Options.Create(options),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<InboxDrainWorker>.Instance);
  }

  [Test]
  public async Task TheFetchStartsAtTheConfiguredPageAsync() {
    var worker = _worker(new InboxDrainWorkerOptions { MaxPerStream = 100, MaxPerStreamCeiling = 1000 });

    await Assert.That(worker.EffectivePerStreamForTest()).IsEqualTo(100)
      .Because("the previously fixed value becomes the FLOOR, so a deployment that upgrades starts "
             + "exactly where it was and moves only on evidence");
  }

  [Test]
  public async Task ASaturatedFetchWidensTheNextOneAsync() {
    var worker = _worker(new InboxDrainWorkerOptions { MaxPerStream = 100, MaxPerStreamCeiling = 1000 });

    worker.ObservePageForTest(rowsReturned: 100, capRequested: 100, _rows(100));

    await Assert.That(worker.EffectivePerStreamForTest()).IsGreaterThan(100)
      .Because("a full page means the stream held at least that much — widening is what turns "
             + "dozens of serial round-trips on a deep stream into a handful");
  }

  [Test]
  public async Task ADeepStreamConvergesOnFarFewerRoundTripsAsync() {
    var worker = _worker(new InboxDrainWorkerOptions { MaxPerStream = 100, MaxPerStreamCeiling = 1000 });

    for (var i = 0; i < 20; i++) {
      var cap = worker.EffectivePerStreamForTest();
      worker.ObservePageForTest(cap, cap, _rows(cap));
    }

    await Assert.That(worker.EffectivePerStreamForTest()).IsEqualTo(1000)
      .Because("a stream holding thousands should end up drained in single-digit fetches instead "
             + "of forty-plus — that is the entire throughput difference for this workload shape");
  }

  [Test]
  public async Task AShallowStreamNeverWidensAsync() {
    var worker = _worker(new InboxDrainWorkerOptions { MaxPerStream = 100, MaxPerStreamCeiling = 1000 });

    for (var i = 0; i < 30; i++) {
      worker.ObservePageForTest(rowsReturned: 2, capRequested: 100, _rows(2));
    }

    await Assert.That(worker.EffectivePerStreamForTest()).IsEqualTo(100)
      .Because("one- and two-row streams are the shape the batched fetch was tuned for; widening "
             + "there buys nothing and only lengthens the lease");
  }

  [Test]
  public async Task ReClaimedRowsNarrowThePageAgainAsync() {
    var worker = _worker(new InboxDrainWorkerOptions { MaxPerStream = 100, MaxPerStreamCeiling = 1000 });
    for (var i = 0; i < 10; i++) {
      var cap = worker.EffectivePerStreamForTest();
      worker.ObservePageForTest(cap, cap, _rows(cap));
    }
    var wide = worker.EffectivePerStreamForTest();

    var churnCap = worker.EffectivePerStreamForTest();
    worker.ObservePageForTest(churnCap, churnCap, _rows(churnCap, attempts: 4));

    await Assert.That(worker.EffectivePerStreamForTest()).IsLessThan(wide)
      .Because("rows coming back with attempts above one mean the page could not be drained inside "
             + "its lease — the width was writing cheques the drain could not cash");
  }

  [Test]
  public async Task PinningTheOptionKeepsTheOldFixedBehaviorAsync() {
    var worker = _worker(new InboxDrainWorkerOptions {
      MaxPerStream = 100,
      MaxPerStreamCeiling = 1000,
      AdaptivePerStreamEnabled = false,
    });

    for (var i = 0; i < 20; i++) {
      worker.ObservePageForTest(rowsReturned: 100, capRequested: 100, _rows(100));
    }

    await Assert.That(worker.EffectivePerStreamForTest()).IsEqualTo(100)
      .Because("an operator must be able to pin the page exactly where it was; an adaptive control "
             + "with no off switch is one that cannot be ruled out during an incident");
  }
}
