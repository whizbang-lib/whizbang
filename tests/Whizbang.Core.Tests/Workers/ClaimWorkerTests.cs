using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

[NotInParallel(Order = 100)]
public class ClaimWorkerTests {

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FakeCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly System.Collections.Generic.Dictionary<int, TaskCompletionSource> _callWatchers = [];
    private int _orderCounter;
    public TaskCompletionSource FirstCallSignal { get; } = new();
    public int CallCount { get; private set; }
    public int HeartbeatCallCount { get; private set; }
    /// <summary>Order index of the first ClaimWorkAsync call, or 0 if never called.</summary>
    public int FirstClaimOrder { get; private set; }
    /// <summary>Order index of the first RecordHeartbeatAsync call, or 0 if never called.</summary>
    public int FirstHeartbeatOrder { get; private set; }
    public WorkBatch BatchToReturn { get; set; } = new() {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = []
    };

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      lock (_lock) {
        CallCount++;
        if (FirstClaimOrder == 0) { FirstClaimOrder = ++_orderCounter; }
        FirstCallSignal.TrySetResult();
        if (_callWatchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      return Task.FromResult(BatchToReturn);
    }

    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      lock (_lock) {
        HeartbeatCallCount++;
        if (FirstHeartbeatOrder == 0) { FirstHeartbeatOrder = ++_orderCounter; }
      }
      return Task.CompletedTask;
    }

    /// <summary>Resolves once at least <paramref name="n"/> ClaimWorkAsync calls have been observed.</summary>
    public Task WaitForCallsAsync(int n, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_lock) {
        if (CallCount >= n) { return Task.CompletedTask; }
        if (!_callWatchers.TryGetValue(n, out tcs!)) {
          tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _callWatchers[n] = tcs;
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("not used");
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  [Test]
  public async Task ExecuteAsync_RegistersInstanceViaHeartbeat_BeforeFirstClaimWorkAsync() {
    // Regression lock: claim_work calls calculate_instance_rank, which raises P0001
    // ("Instance not found in active instances") if the instance_id isn't in
    // wh_service_instances. HeartbeatWorker UPSERTs the row but ticks on its own
    // cadence — there's no ordering guarantee against ClaimWorker's first tick.
    // ClaimWorker must therefore perform an initial heartbeat itself before its
    // first claim cycle so the rank lookup succeeds. a consumer prod surfaced the raw
    // race as repeated "ClaimWorker tick failed; will back off and retry" warnings.
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(coord.HeartbeatCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(coord.FirstHeartbeatOrder).IsGreaterThan(0);
    await Assert.That(coord.FirstHeartbeatOrder).IsLessThan(coord.FirstClaimOrder);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_PollsAtLeastOnceAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task RequestImmediatePoll_BypassesWaitAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 10_000,            // very long
        PollingMaxIntervalMilliseconds = 60_000           // very long
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var startCount = coord.CallCount;

    // Immediate poll should fire another tick within hundreds of ms despite the 10s base.
    var beforeWake = coord.CallCount;
    worker.RequestImmediatePoll();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (coord.CallCount == beforeWake && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Delay(20);
    }

    await Assert.That(coord.CallCount).IsGreaterThan(startCount);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_DisabledOptions_NoClaimFiresAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();  // gate ready but Enabled=false should still suppress the claim loop
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions {
        Enabled = false,
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 200
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var raced = await Task.WhenAny(coord.FirstCallSignal.Task, Task.Delay(500, CancellationToken.None));
    await Assert.That(coord.CallCount).IsEqualTo(0);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_BlocksOnSchemaGate_UntilMarkedReadyAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();  // not marked ready
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // No claim while gate is closed.
    var racedBefore = await Task.WhenAny(coord.FirstCallSignal.Task, Task.Delay(300, CancellationToken.None));
    await Assert.That(coord.CallCount).IsEqualTo(0);

    // Open gate — claim fires.
    gate.MarkReady();
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // --- Part C step 4: stream_id drain channels ---

  private sealed class CapturingDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _ch = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public List<Guid> Written { get; } = [];
    public TaskCompletionSource SecondWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Threading.Channels.ChannelReader<Guid> Reader => _ch.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) {
      Written.Add(streamId);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult();
      }
      return _ch.Writer.WriteAsync(streamId, ct);
    }
    public bool TryWrite(Guid streamId) {
      Written.Add(streamId);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult();
      }
      return _ch.Writer.TryWrite(streamId);
    }
  }

  private sealed class FilteringDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _ch = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();
    public List<Guid> Written { get; } = [];
    public TaskCompletionSource WriteCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Threading.Channels.ChannelReader<Guid> Reader => _ch.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) {
      Written.Add(streamId);
      WriteCalled.TrySetResult();
      return _ch.Writer.WriteAsync(streamId, ct);
    }
    public bool TryWrite(Guid streamId) {
      Written.Add(streamId);
      WriteCalled.TrySetResult();
      return _ch.Writer.TryWrite(streamId);
    }
    public bool IsInFlight(Guid streamId) => _inFlight.ContainsKey(streamId);
    public void MarkDraining(Guid streamId) => _inFlight[streamId] = 1;
    public void MarkDrained(Guid streamId) => _inFlight.TryRemove(streamId, out _);
  }

  /// <summary>Captures perspective drain channel writes and exposes in-flight tracking.</summary>
  private sealed class FilteringPerspectiveDrainChannel : IPerspectiveDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _ch = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();
    public List<Guid> Written { get; } = [];
    public System.Threading.Channels.ChannelReader<Guid> Reader => _ch.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) {
      Written.Add(streamId);
      return _ch.Writer.WriteAsync(streamId, ct);
    }
    public bool TryWrite(Guid streamId) {
      Written.Add(streamId);
      return _ch.Writer.TryWrite(streamId);
    }
    public bool IsInFlight(Guid streamId) => _inFlight.ContainsKey(streamId);
    public void MarkDraining(Guid streamId) => _inFlight[streamId] = 1;
    public void MarkDrained(Guid streamId) => _inFlight.TryRemove(streamId, out _);
  }

  /// <summary>Captures inbox drain channel writes and exposes in-flight tracking.</summary>
  private sealed class FilteringInboxDrainChannel : IInboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _ch = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();
    public List<Guid> Written { get; } = [];
    public System.Threading.Channels.ChannelReader<Guid> Reader => _ch.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) {
      Written.Add(streamId);
      return _ch.Writer.WriteAsync(streamId, ct);
    }
    public bool TryWrite(Guid streamId) {
      Written.Add(streamId);
      return _ch.Writer.TryWrite(streamId);
    }
    public bool IsInFlight(Guid streamId) => _inFlight.ContainsKey(streamId);
    public void MarkDraining(Guid streamId) => _inFlight[streamId] = 1;
    public void MarkDrained(Guid streamId) => _inFlight.TryRemove(streamId, out _);
  }

  [Test]
  public async Task Distribute_PerspectiveDrainChannel_WritesStreamIds_EvenWhenIsInFlightTrueAsync() {
    // Regression lock: ClaimWorker MUST NOT skip writes based on IsInFlight. The previous
    // filter (Phase H step 6 slice 5) silently dropped writes when a drain task's MarkDraining
    // flag stuck — e.g., a hung handler that never returned from `_drainStreamInnerAsync`'s
    // try/finally. Once stuck, every subsequent claim_work emit was discarded for that stream,
    // forever, even though `claim_work` SQL kept reporting the row as eligible. Observed in
    // production on a consumer BFF (3,545 unprocessed inbox rows leased to a healthy instance with
    // zero drain progress; only restart unstuck them).
    //
    // The new contract: writes always happen. The drainer's idempotent fetch_*_batch (filters
    // processed_at IS NULL) + sequential channel reads make duplicate emissions harmless —
    // a second drain for the same stream just reads 0 rows from SQL.
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var coord = new FakeCoordinator {
      BatchToReturn = new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        PerspectiveStreamIds = [streamA, streamB]
      }
    };

    var perspectiveDrain = new FilteringPerspectiveDrainChannel();
    perspectiveDrain.MarkDraining(streamA);  // simulate stuck flag from a hung prior drain

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance,
      perspectiveDrainChannel: perspectiveDrain);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.WaitForCallsAsync(3, TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // streamA must appear in Written despite IsInFlight=true. Both stream_ids written every poll.
    await Assert.That(perspectiveDrain.Written).Contains(streamA)
      .Because("stuck IsInFlight flag must not block re-emission — drainer dedups via idempotent SQL");
    await Assert.That(perspectiveDrain.Written).Contains(streamB);
    await Assert.That(perspectiveDrain.Written.Count).IsGreaterThanOrEqualTo(coord.CallCount * 2)
      .Because("each poll emits both stream_ids; if the filter is back, count would be < callCount * 2");
  }

  [Test]
  public async Task Distribute_OutboxDrainChannel_WritesStreamIds_EvenWhenIsInFlightTrueAsync() {
    // Regression lock — see PerspectiveDrainChannel test for the rationale.
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var coord = new FakeCoordinator {
      BatchToReturn = new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        OutboxStreamIds = [streamA, streamB]
      }
    };

    var drain = new FilteringDrainChannel();
    drain.MarkDraining(streamA);  // stuck flag from a hypothetical hung prior drain

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance,
      outboxDrainChannel: drain);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.WaitForCallsAsync(3, TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(drain.Written).Contains(streamA)
      .Because("stuck IsInFlight flag must not block re-emission");
    await Assert.That(drain.Written).Contains(streamB);
    await Assert.That(drain.Written.Count).IsGreaterThanOrEqualTo(coord.CallCount * 2);
  }

  [Test]
  public async Task Distribute_InboxDrainChannel_WritesStreamIds_EvenWhenIsInFlightTrueAsync() {
    // Regression lock for the inbox path — completes the symmetry across all three drain
    // channels. a consumer production observed this exact failure mode on BFF: 3,545 inbox rows
    // leased to the healthy instance, claim_work emitting them on every poll, ClaimWorker
    // silently filtering them via IsInFlight, drain pipeline idle for hours.
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var coord = new FakeCoordinator {
      BatchToReturn = new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        InboxStreamIds = [streamA, streamB]
      }
    };

    var drain = new FilteringInboxDrainChannel();
    drain.MarkDraining(streamA);  // stuck flag from a hypothetical hung prior drain

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance,
      inboxDrainChannel: drain);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.WaitForCallsAsync(3, TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(drain.Written).Contains(streamA)
      .Because("stuck IsInFlight flag must not block re-emission — inbox drain symmetry with perspective + outbox");
    await Assert.That(drain.Written).Contains(streamB);
    await Assert.That(drain.Written.Count).IsGreaterThanOrEqualTo(coord.CallCount * 2);
  }

  [Test]
  public async Task Distribute_OutboxStreamIds_RoutedToOutboxDrainChannelAsync() {
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    // Coordinator populates OutboxStreamIds on the batch (real EFCoreWorkCoordinator dedups
    // these from claim_work output). ClaimWorker just forwards to the drain channel.
    var coord = new FakeCoordinator {
      BatchToReturn = new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        OutboxStreamIds = [streamA, streamB]
      }
    };

    var drain = new CapturingDrainChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance,
      outboxDrainChannel: drain);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await drain.SecondWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Two distinct stream_ids written even though OutboxWork had 3 items (one dupe).
    await Assert.That(drain.Written.Count).IsEqualTo(2);
    await Assert.That(drain.Written).Contains(streamA);
    await Assert.That(drain.Written).Contains(streamB);
  }

}
