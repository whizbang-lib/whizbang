// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable IDE0060, RCS1163 // Unused parameters: the fake coordinator implements interface members the tests never exercise

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branches of <see cref="ClaimWorker"/> no other test takes: the options guard for a missing
/// wrapper or value, the legacy listener wake for a category that is not work (and for work
/// categories once the signal bus owns them), a transient failure from a provider that reports no
/// SQLSTATE, a perspective drain channel that cannot count its backlog, and a claimed inbox stream
/// the claim carried no fold for when batch hooks reorder the drain.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimWorker.cs</code-under-test>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ClaimWorkerBranchCoverageTests {

  // ============================================================
  // Constructor: options guard
  // ============================================================

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => _construct(null!))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => _construct(new NullValueOptions()))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  // ============================================================
  // Legacy listener wake (_onSignal)
  // ============================================================

  /// <summary>
  /// A dead-letter signal is not new claimable work. If it were treated as a work doorbell, the
  /// following discovery would be credited to a doorbell that never rang for it, hiding a real
  /// NOTIFY outage from the liveness accounting.
  /// </summary>
  [Test]
  public async Task ListenerSignal_DeadLetterReadyWithoutBus_IsNotAWorkDoorbellAsync() {
    var listener = new ControllableListener();
    var coord = new ScriptedCoordinator(_edgeScript());
    coord.DuringFirstClaim = () => listener.Raise(WorkSignalCategory.DeadLetterReady);
    var liveness = new SignalBusLivenessState();
    var judged = _judgement(liveness);
    using var harness = _startWorker(coord, listener, NullSignalBus.Instance, liveness, pollingIntervalMs: 50, pollingMaxMs: 100);

    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(30));
    await judged.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(liveness.ConsecutiveMissedDoorbells).IsEqualTo(1)
      .Because("a dead-letter signal must not mark the edge as doorbell-preceded");
  }

  /// <summary>
  /// With the signal bus configured, outbox/inbox/perspective wakes arrive as typed bus signals.
  /// The same category arriving on the legacy listener must not ALSO count as a doorbell, or one
  /// notification would be counted twice and a dead bus route would look alive.
  /// </summary>
  [Test]
  public async Task ListenerSignal_OutboxWithBusConfigured_IsLeftToTheBusAsync() {
    var listener = new ControllableListener();
    var coord = new ScriptedCoordinator(_edgeScript());
    coord.DuringFirstClaim = () => listener.Raise(WorkSignalCategory.Outbox);
    var liveness = new SignalBusLivenessState();
    var judged = _judgement(liveness);
    using var harness = _startWorker(coord, listener, new ConfiguredSignalBus(), liveness, pollingIntervalMs: 50, pollingMaxMs: 100);

    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(30));
    await judged.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(liveness.ConsecutiveMissedDoorbells).IsEqualTo(1)
      .Because("with the bus configured the legacy listener's outbox category must not ring the doorbell");
  }

  /// <summary>Positive control for the two tests above: without a bus, the listener's outbox
  /// category IS the doorbell, so the discovery is doorbell-preceded and nothing is missed.</summary>
  [Test]
  public async Task ListenerSignal_OutboxWithoutBus_RingsTheDoorbellAsync() {
    var listener = new ControllableListener();
    var coord = new ScriptedCoordinator(_edgeScript());
    coord.DuringFirstClaim = () => listener.Raise(WorkSignalCategory.Outbox);
    var liveness = new SignalBusLivenessState();
    var judged = _judgement(liveness);
    // Polling parked far out: the second claim can only come from the doorbell.
    using var harness = _startWorker(coord, listener, NullSignalBus.Instance, liveness, pollingIntervalMs: 60_000, pollingMaxMs: 60_000);

    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(30));
    await judged.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(liveness.ConsecutiveMissedDoorbells).IsEqualTo(0);
  }

  // ============================================================
  // Transient failure with no SQLSTATE
  // ============================================================

  [Test]
  public async Task ClaimTick_TransientFailureWithoutSqlState_IsReportedAsNoneAsync() {
    var coord = new ScriptedCoordinator(_ => _emptyBatch()) {
      FirstClaimException = FakeDbException.WithSqlState(null, isTransient: true, message: "provider says transient"),
    };
    var logger = new Microsoft.Extensions.Logging.Testing.FakeLogger<ClaimWorker>();
    using var harness = _startWorker(coord, new ControllableListener(), NullSignalBus.Instance, busLiveness: null,
      pollingIntervalMs: 10, pollingMaxMs: 50, logger: logger);

    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(10));

    var reported = logger.Collector.GetSnapshot()
      .Where(e => e.Id.Id == ClaimWorker.TRANSIENT_FAILURE_EVENT_ID).ToList();
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Message).Contains(TransientDatabaseFailure.PROVIDER_TRANSIENT, StringComparison.Ordinal);
    await Assert.That(reported[0].Message).Contains("SQLSTATE none", StringComparison.Ordinal)
      .Because("a provider that reports no SQLSTATE is logged as 'none', never an empty or null placeholder");
  }

  // ============================================================
  // Perspective drain backlog cap with a channel that cannot count
  // ============================================================

  /// <summary>
  /// A channel that cannot count must read as "not above the cap": asking it for a count throws,
  /// which would fail every claim tick before the store is ever asked.
  /// </summary>
  [Test]
  public async Task Claim_PerspectiveDrainChannelThatCannotCount_LeavesPerspectiveAcquisitionAloneAsync() {
    var coord = new ScriptedCoordinator(_ => _emptyBatch());
    using var harness = _startWorker(coord, new ControllableListener(), NullSignalBus.Instance, busLiveness: null,
      pollingIntervalMs: 20, pollingMaxMs: 60,
      perspectiveDrainChannel: new NonCountingPerspectiveDrainChannel(),
      configure: o => o.MaxPerspectiveDrainBacklog = 1);

    await coord.WaitForCallsAsync(1, TimeSpan.FromSeconds(10));

    await Assert.That(coord.Requests[0].MaxPerspectiveStreams).IsNull()
      .Because("an uncountable backlog cannot be above the cap, so the store's own bound applies");
  }

  // ============================================================
  // Batch-hook ordering: a claimed stream with no fold
  // ============================================================

  /// <summary>
  /// A claimed inbox stream the claim carried no fold for is ordered as STANDARD. A folded stream
  /// the hook puts one step behind STANDARD must therefore follow it.
  /// </summary>
  [Test]
  public async Task Distribute_UnfoldedStream_IsOrderedAsStandard_AheadOfALowerPriorityFoldAsync() {
    var written = await _dispatchOrderAsync(hookNumberForFolded: WorkPriority.STANDARD + 1);

    await Assert.That(written[1]).IsEqualTo(written.Folded)
      .Because("the unfolded stream defaults to STANDARD, which sorts ahead of STANDARD + 1");
    await Assert.That(written[0]).IsEqualTo(written.Unfolded);
  }

  /// <summary>The other side of the boundary: a fold the hook puts one step ahead of STANDARD
  /// keeps its place in front of the unfolded stream.</summary>
  [Test]
  public async Task Distribute_UnfoldedStream_IsOrderedAsStandard_BehindAHigherPriorityFoldAsync() {
    var written = await _dispatchOrderAsync(hookNumberForFolded: WorkPriority.STANDARD - 1);

    await Assert.That(written[0]).IsEqualTo(written.Folded);
    await Assert.That(written[1]).IsEqualTo(written.Unfolded);
  }

  private sealed class DispatchOrder(Guid folded, Guid unfolded, Guid[] order) {
    public Guid Folded => folded;
    public Guid Unfolded => unfolded;
    public Guid this[int index] => order[index];
  }

  private static async Task<DispatchOrder> _dispatchOrderAsync(int hookNumberForFolded) {
    var folded = (Guid)TrackedGuid.New();
    var unfolded = (Guid)TrackedGuid.New();
    // The claim lists the folded stream FIRST, so only the adjusted numbers can put it second.
    var batch = new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      InboxStreamIds = [folded, unfolded],
      InboxStreams = [new InboxStreamFold(folded, WorkPriority.STANDARD, DateTimeOffset.UtcNow.AddMinutes(-1), 1)],
    };
    var coord = new ScriptedCoordinator(_ => batch);
    var drain = new RecordingInboxDrain();
    var chain = new PriorityHookChain([], [], [new FixedNumberHook(hookNumberForFolded)]);
    using (_startWorker(coord, new ControllableListener(), NullSignalBus.Instance, busLiveness: null,
             pollingIntervalMs: 50, pollingMaxMs: 200, inboxDrainChannel: drain, priorityHooks: chain)) {
      await drain.FirstTwoWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    Guid[] order;
    lock (drain.Written) { order = [.. drain.Written.Take(2)]; }
    return new DispatchOrder(folded, unfolded, order);
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static Func<int, WorkBatch> _edgeScript() {
    var fresh = (Guid)TrackedGuid.New();
    return call => new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      OutboxStreamIds = call == 2 ? [fresh] : [],
    };
  }

  private static WorkBatch _emptyBatch() => new() { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };

  private static TaskCompletionSource _judgement(SignalBusLivenessState liveness) {
    var judged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    liveness.DoorbellEvaluated += () => judged.TrySetResult();
    return judged;
  }

  private static ClaimWorker _construct(IOptions<ClaimWorkerOptions> options) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    var sp = services.BuildServiceProvider();
    return new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new StubInstance(),
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: options,
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new PerspectiveChannelWriter(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance);
  }

  private static WorkerHarness _startWorker(
      ScriptedCoordinator coord,
      ControllableListener listener,
      ISignalBus signalBus,
      SignalBusLivenessState? busLiveness,
      int pollingIntervalMs,
      int pollingMaxMs,
      Microsoft.Extensions.Logging.ILogger<ClaimWorker>? logger = null,
      IPerspectiveDrainChannel? perspectiveDrainChannel = null,
      IInboxDrainChannel? inboxDrainChannel = null,
      PriorityHookChain? priorityHooks = null,
      Action<ClaimWorkerOptions>? configure = null) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var options = new ClaimWorkerOptions {
      PollingIntervalMilliseconds = pollingIntervalMs,
      PollingMaxIntervalMilliseconds = pollingMaxMs,
      NotifyHealthyPollingIntervalMilliseconds = null,
    };
    configure?.Invoke(options);
    var worker = new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new StubInstance(),
      notificationListener: listener,
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: Options.Create(options),
      logger: logger ?? NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new PerspectiveChannelWriter(),
      perspectiveDrainChannel: perspectiveDrainChannel ?? new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: inboxDrainChannel ?? new InboxDrainChannel(),
      signalingGate: new AvailableGate(),
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: signalBus,
      busLiveness: busLiveness,
      priorityHooks: priorityHooks);
    var cts = new CancellationTokenSource();
    worker.StartAsync(cts.Token).GetAwaiter().GetResult();
    return new WorkerHarness(worker, cts);
  }

  private sealed class WorkerHarness(ClaimWorker worker, CancellationTokenSource cts) : IDisposable {
    public void Dispose() {
      cts.Cancel();
      try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
      worker.Dispose();
      cts.Dispose();
    }
  }

  private sealed class NullValueOptions : IOptions<ClaimWorkerOptions> {
    public ClaimWorkerOptions Value => null!;
  }

  private sealed class StubInstance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class AvailableGate : INotifySignalingGate {
    public bool IsAvailable => true;
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged { add { /* the fake never raises this event */ } remove { /* the fake never raises this event */ } }
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  /// <summary>A listener whose signals a test can raise.</summary>
  private sealed class ControllableListener : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;

    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;

    public event Action<WorkSignalCategory>? OnSignal {
      add { _onSignal += value; }
      remove { _onSignal -= value; }
    }

    public event Action<bool>? OnHealthChanged {
      add { /* the fake never raises this event */ }
      remove { /* the fake never raises this event */ }
    }

    public void Raise(WorkSignalCategory category) => _onSignal?.Invoke(category);
  }

  /// <summary>A configured signal bus (the interface default reports configured) that never publishes.</summary>
  private sealed class ConfiguredSignalBus : ISignalBus {
    private sealed class Subscription : ISignalSubscription {
      public void Dispose() { /* nothing to release */ }
    }

    public ValueTask PublishAsync<TSignal>(
        TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;

    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
      where TSignal : ISignal => new Subscription();
  }

  /// <summary>A reader that, like many custom channels, cannot report how many items it holds.</summary>
  private sealed class NonCountingReader : ChannelReader<Guid> {
    public override bool TryRead(out Guid item) {
      item = Guid.Empty;
      return false;
    }

    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(false);
  }

  private sealed class NonCountingPerspectiveDrainChannel : IPerspectiveDrainChannel {
    public ChannelReader<Guid> Reader { get; } = new NonCountingReader();
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public bool TryWrite(Guid streamId) => true;
  }

  private sealed class RecordingInboxDrain : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public List<Guid> Written { get; } = [];
    public TaskCompletionSource FirstTwoWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) {
      lock (Written) {
        Written.Add(streamId);
        if (Written.Count >= 2) { FirstTwoWritten.TrySetResult(); }
      }
      return _channel.Writer.WriteAsync(streamId, cancellationToken);
    }
    public bool TryWrite(Guid streamId) {
      lock (Written) { Written.Add(streamId); }
      return _channel.Writer.TryWrite(streamId);
    }
  }

  /// <summary>Gives every stream it is shown the same number.</summary>
  private sealed class FixedNumberHook(int number) : IPriorityBatchHook {
    public int Order => 100;
    public int Adjust(PriorityBatchEntry stream, IReadOnlyList<PriorityBatchEntry> batch) => number;
  }

  /// <summary>
  /// A store that answers each claim from a script keyed by call number (1-based) and records
  /// every request. The first claim may throw, and may run a callback before it returns.
  /// </summary>
  private sealed class ScriptedCoordinator(Func<int, WorkBatch> script) : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];
    private readonly List<ClaimWorkRequest> _requests = [];

    public Action? DuringFirstClaim { get; set; }
    public Exception? FirstClaimException { get; init; }

    public IReadOnlyList<ClaimWorkRequest> Requests {
      get { lock (_lock) { return [.. _requests]; } }
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      int call;
      WorkBatch batch;
      lock (_lock) {
        _requests.Add(request);
        call = _requests.Count;
        batch = script(call);
        if (_watchers.TryGetValue(call, out var tcs)) { tcs.TrySetResult(); }
      }
      // Outside the lock: the callback re-enters the worker.
      if (call == 1) {
        DuringFirstClaim?.Invoke();
        if (FirstClaimException is not null) {
          return Task.FromException<WorkBatch>(FirstClaimException);
        }
      }
      return Task.FromResult(batch);
    }

    public Task WaitForCallsAsync(int n, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_lock) {
        if (_requests.Count >= n) { return Task.CompletedTask; }
        if (!_watchers.TryGetValue(n, out tcs!)) {
          tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _watchers[n] = tcs;
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
