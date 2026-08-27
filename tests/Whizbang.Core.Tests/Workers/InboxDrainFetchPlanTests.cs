using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The drain must turn a global row budget into actual fetches, not just compute a plan it ignores.
/// </summary>
/// <remarks>
/// <para>
/// A fetch takes ONE cap for every stream in the call, so a per-stream allocation cannot be issued
/// directly. Quantizing each allocation down to a multiple of the floor solves it: streams sharing
/// a quantized cap travel in one fetch, which bounds the number of calls to ceiling/floor no matter
/// how many streams are active, and rounding DOWN keeps the total inside the budget rather than
/// drifting over it.
/// </para>
/// <para>
/// Without this the drain is back to one cap for everyone, which is the failure the allocator
/// exists to prevent: a uniform cap either starves deep streams or wastes budget on shallow ones,
/// and which of the two it does depends entirely on a number nobody can tune for both shapes.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDrainWorker.cs</code-under-test>
[Category("Workers")]
public class InboxDrainFetchPlanTests {

  private sealed class _Instance : Whizbang.Core.Observability.IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo() => new() {
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
    public ValueTask WriteAsync(Guid s, CancellationToken ct = default) => _c.Writer.WriteAsync(s, ct);
    public bool TryWrite(Guid s) => _c.Writer.TryWrite(s);
  }

  private sealed class _InboxWriter : IInboxChannelWriter {
    private readonly System.Threading.Channels.Channel<InboxWork> _c =
      System.Threading.Channels.Channel.CreateUnbounded<InboxWork>();
    public System.Threading.Channels.ChannelReader<InboxWork> Reader => _c.Reader;
    public ValueTask WriteAsync(InboxWork w, CancellationToken ct = default) => _c.Writer.WriteAsync(w, ct);
    public bool TryWrite(InboxWork w) => _c.Writer.TryWrite(w);
    public bool IsInFlight(Guid id) => false;
    public void RemoveInFlight(Guid id) { }
    public bool ShouldRenewLease(Guid id) => false;
    public void Complete() => _c.Writer.TryComplete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private static InboxDrainWorker _worker(InboxDrainWorkerOptions o) {
    var sp = new ServiceCollection().BuildServiceProvider();
    return new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), new _Instance(), new _DrainChannel(),
      new _InboxWriter(), new SchemaReadyGate(), Options.Create(o),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<InboxDrainWorker>.Instance);
  }

  private static readonly Guid[] _ids =
    [.. Enumerable.Range(1, 80).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}"))];

  private static InboxDrainWorkerOptions _opts(int floor = 100, int ceiling = 1000, int cycle = 0)
    => new() { MaxPerStream = floor, MaxPerStreamCeiling = ceiling, MaxRowsPerCycle = cycle };

  [Test]
  public async Task EveryStreamWithWorkAppearsInSomeFetchAsync() {
    var worker = _worker(_opts());
    var streams = _ids.Take(30).ToList();

    var plan = worker.PlanFetchesForTest(streams);

    var covered = plan.SelectMany(p => p.Streams).ToHashSet();
    await Assert.That(covered.Count).IsEqualTo(30)
      .Because("a stream dropped from the plan is a stream that never drains — silent starvation "
             + "that no aggregate metric distinguishes from healthy throughput");
  }

  [Test]
  public async Task TheNumberOfFetchesStaysBoundedRegardlessOfStreamCountAsync() {
    var worker = _worker(_opts(floor: 100, ceiling: 1000));

    var plan = worker.PlanFetchesForTest([.. _ids]);

    await Assert.That(plan.Count).IsLessThanOrEqualTo(10)
      .Because("quantizing to multiples of the floor caps the call count at ceiling/floor; issuing "
             + "one fetch per distinct allocation would turn eighty streams into eighty round-trips "
             + "and be far worse than the single-cap fetch it replaced");
  }

  [Test]
  public async Task PlannedRowsNeverExceedTheCycleBudgetAsync() {
    var worker = _worker(_opts(floor: 100, ceiling: 1000, cycle: 2_000));

    var plan = worker.PlanFetchesForTest([.. _ids.Take(40)]);
    var planned = plan.Sum(p => (long)p.Cap * p.Streams.Count);

    await Assert.That(planned).IsLessThanOrEqualTo(2_000)
      .Because("rounding allocations DOWN to the floor multiple is what keeps the total inside the "
             + "budget; rounding up would drift over it on every cycle");
  }

  [Test]
  public async Task ADeepStreamIsFetchedWithAWiderCapThanAShallowOneAsync() {
    var worker = _worker(_opts(floor: 100, ceiling: 1000));
    var deep = _ids[0];
    var shallow = _ids[1];

    // Teach the worker what it saw last cycle: one stream saturated, the other barely had rows.
    worker.RecordObservedDepthForTest(deep, 5_000);
    worker.RecordObservedDepthForTest(shallow, 3);

    var plan = worker.PlanFetchesForTest([deep, shallow]);
    var deepCap = plan.Single(p => p.Streams.Contains(deep)).Cap;
    var shallowCap = plan.Single(p => p.Streams.Contains(shallow)).Cap;

    await Assert.That(deepCap).IsGreaterThan(shallowCap)
      .Because("this is the whole point of allocating rather than capping: the stream holding "
             + "thousands drains in a few wide fetches while the three-row stream is not handed "
             + "budget it cannot use");
  }

  [Test]
  public async Task StreamsSharingAQuantizedCapTravelInOneFetchAsync() {
    var worker = _worker(_opts(floor: 100, ceiling: 1000));
    var streams = _ids.Take(12).ToList();
    foreach (var s in streams) {
      worker.RecordObservedDepthForTest(s, 4);
    }

    var plan = worker.PlanFetchesForTest(streams);

    await Assert.That(plan.Count).IsEqualTo(1)
      .Because("twelve identical shallow streams must not become twelve round-trips — amortizing "
             + "the per-call setup across streams is exactly what the batched fetch is for");
    await Assert.That(plan[0].Streams.Count).IsEqualTo(12);
  }

  [Test]
  public async Task AnUnknownStreamIsStillFetchedAtTheFloorAsync() {
    var worker = _worker(_opts(floor: 100, ceiling: 1000));

    var plan = worker.PlanFetchesForTest([_ids[5]]);

    await Assert.That(plan.Count).IsEqualTo(1);
    await Assert.That(plan[0].Cap).IsGreaterThanOrEqualTo(100)
      .Because("a stream nobody has measured yet must still be drained at a useful width, or a "
             + "newly-active stream would never produce the observation that sizes it");
  }

  [Test]
  public async Task NoStreamsMeansNoFetchesAsync() {
    var worker = _worker(_opts());
    await Assert.That(worker.PlanFetchesForTest([]).Count).IsEqualTo(0);
  }

  [Test]
  public async Task EveryPlannedCapIsAtLeastTheFloorAsync() {
    var worker = _worker(_opts(floor: 100, ceiling: 1000, cycle: 150));
    var streams = _ids.Take(40).ToList();

    var plan = worker.PlanFetchesForTest(streams);

    await Assert.That(plan.All(p => p.Cap >= 100)).IsTrue()
      .Because("a cap below the floor fetches a uselessly thin slice and guarantees another "
             + "round-trip; when the budget cannot seat everyone the answer is fewer streams this "
             + "cycle, never thinner slices for all of them");
  }
}
