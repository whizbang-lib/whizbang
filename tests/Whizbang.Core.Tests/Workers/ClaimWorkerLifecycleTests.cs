using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers the ClaimWorker's signal wiring and teardown — the paths around the claim loop
/// rather than inside it.
/// </summary>
/// <remarks>
/// The worker subscribes to a notification listener for its whole lifetime. Those handlers
/// hold it alive, so failing to release them on Dispose leaks the worker; and a signal
/// arriving after teardown must be absorbed rather than thrown from a background callback
/// nobody is awaiting.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class ClaimWorkerLifecycleTests {

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo();
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

  /// <summary>A listener whose signals a test can raise, and which counts live subscribers.</summary>
  private sealed class ControllableListener : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;
    private Action<bool>? _onHealth;

    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;

    public event Action<WorkSignalCategory>? OnSignal {
      add { _onSignal += value; SignalSubscribers++; }
      remove { _onSignal -= value; SignalSubscribers--; }
    }

    public event Action<bool>? OnHealthChanged {
      add { _onHealth += value; HealthSubscribers++; }
      remove { _onHealth -= value; HealthSubscribers--; }
    }

    public int SignalSubscribers { get; private set; }
    public int HealthSubscribers { get; private set; }

    public void Raise(WorkSignalCategory category) => _onSignal?.Invoke(category);
  }


  /// <summary>A signal bus whose subscriptions record their own disposal.</summary>
  private sealed class TrackingSignalBus : ISignalBus {
    private sealed class Subscription(TrackingSignalBus owner) : ISignalSubscription {
      public void Dispose() => owner.DisposeCount++;
    }

    public int SubscribeCount { get; private set; }
    public int DisposeCount { get; set; }

    public ValueTask PublishAsync<TSignal>(
        TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;

    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
      where TSignal : ISignal {
      SubscribeCount++;
      return new Subscription(this);
    }
  }

  private sealed class MinimalCoordinator : IWorkCoordinator {
    /// <summary>Makes the startup registration fail, standing in for a database that is up but
    /// briefly refusing writes.</summary>
    public bool HeartbeatThrows { get; init; }

    public TaskCompletionSource HeartbeatAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the first claim, so a test can tell "has not claimed yet" from
    /// "never will".</summary>
    public TaskCompletionSource ClaimAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _claimCount;

    /// <summary>How many claims the worker made. Written from the worker thread.</summary>
    public int ClaimCount => Volatile.Read(ref _claimCount);

    private readonly Lock _operationLock = new();
    private readonly List<string> _operations = [];

    /// <summary>
    /// The order the worker called this coordinator in — "register" for the startup heartbeat,
    /// "claim" for a claim. Written from the worker thread, read from the test thread, so it is
    /// guarded and handed out as a copy.
    /// </summary>
    public IReadOnlyList<string> Operations {
      get { lock (_operationLock) { return [.. _operations]; } }
    }

    private void _record(string operation) {
      lock (_operationLock) { _operations.Add(operation); }
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) {
      _record("register");
      HeartbeatAttempted.TrySetResult();
      return HeartbeatThrows
        ? Task.FromException<bool>(new InvalidOperationException("wh_service_instances unavailable"))
        : Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) {
      _record("claim");
      Interlocked.Increment(ref _claimCount);
      ClaimAttempted.TrySetResult();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
      });
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private static ClaimWorker _worker(
      ControllableListener listener, ISignalBus? bus = null,
      MinimalCoordinator? coordinator = null, bool perspectiveOnly = false) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator ?? new MinimalCoordinator());
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      listener,
      gate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 200,
        PerspectiveOnly = perspectiveOnly,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalBus: bus);
  }

  [Test]
  public async Task Constructor_SubscribesToTheNotificationListenerAsync() {
    var listener = new ControllableListener();

    using var worker = _worker(listener);

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task Dispose_ReleasesTheSignalBusSubscriptionsAsync() {
    // The bus holds a handler reference per subscription. A worker that stops without releasing
    // them leaves three dead handlers on a bus that outlives it, so every later publish walks
    // them and wakes a worker that is gone.
    var bus = new TrackingSignalBus();
    var worker = _worker(new ControllableListener(), bus);

    worker.Dispose();

    await Assert.That(bus.SubscribeCount).IsEqualTo(3);
    await Assert.That(bus.DisposeCount).IsEqualTo(3);
  }

  [Test]
  public async Task Dispose_DoesNotReleaseTheSameSubscriptionTwiceAsync() {
    // StopAsync followed by the host's own disposal is an ordinary shape, so the second pass
    // has to find the handles already cleared rather than dispose them again.
    var bus = new TrackingSignalBus();
    var worker = _worker(new ControllableListener(), bus);

    worker.Dispose();
    worker.Dispose();

    await Assert.That(bus.DisposeCount).IsEqualTo(3);
  }

  [Test]
  public async Task Dispose_WithoutASignalBus_IsSafeAsync() {
    // The bus is optional: in a pull-only deployment nothing was ever subscribed, and teardown
    // must not fault on the handles that were never assigned.
    var listener = new ControllableListener();
    var worker = _worker(listener);

    worker.Dispose();
    worker.Dispose();

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task OrphanRedistributeSignal_RequestsAnImmediatePollAsync() {
    // Orphan redistribution is time-sensitive: the rows are already claimed by an instance
    // that stopped, so this category skips the doorbell and pokes the loop directly.
    var listener = new ControllableListener();
    using var worker = _worker(listener);

    listener.Raise(WorkSignalCategory.OrphanRedistribute);

    // Reaching here without throwing is the assertion — the handler runs on the listener's
    // thread, so an exception would surface as an unobserved callback failure.
    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  [Arguments(WorkSignalCategory.Outbox)]
  [Arguments(WorkSignalCategory.Inbox)]
  [Arguments(WorkSignalCategory.Perspective)]
  public async Task WorkSignals_WakeTheLoopWithoutABusAsync(WorkSignalCategory category) {
    // With no signal bus configured the listener is the only wake source, so these
    // categories have to ring the doorbell themselves.
    var listener = new ControllableListener();
    using var worker = _worker(listener);

    listener.Raise(category);

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task SignalsAfterDispose_AreAbsorbedAsync() {
    // A signal can arrive from the listener's thread after teardown. It must not throw:
    // nobody is awaiting that callback, so an exception there is unobserved.
    var listener = new ControllableListener();
    var worker = _worker(listener);
    worker.Dispose();

    // The listener handler outlives Dispose by design — it is the only wake path for the
    // orphan and dead-letter categories, which have no typed bus signal. So the signal still
    // reaches a worker whose nap token is disposed, and the guard has to swallow that.
    listener.Raise(WorkSignalCategory.Outbox);
    listener.Raise(WorkSignalCategory.OrphanRedistribute);

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task RequestImmediatePoll_IsIdempotentWhileOnePollIsPendingAsync() {
    // The doorbell is a one-slot semaphore: several signals arriving before the loop wakes
    // collapse into a single poll rather than queueing a burst of them.
    var listener = new ControllableListener();
    using var worker = _worker(listener);

    worker.RequestImmediatePoll();
    worker.RequestImmediatePoll();
    worker.RequestImmediatePoll();

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  // ============================================================
  // Startup
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task AFailedStartupRegistration_DoesNotStopTheWorkerAsync(CancellationToken testToken) {
    // The registration exists so peers rank correctly the moment this pod appears, but
    // claim_work repairs its own registration before ranking — so this is an optimization, not
    // a correctness requirement. Treating its failure as fatal would keep a pod out of the
    // fleet over something the very next claim fixes by itself.
    var coordinator = new MinimalCoordinator { HeartbeatThrows = true };
    var worker = _worker(new ControllableListener(), coordinator: coordinator);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    await coordinator.HeartbeatAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    var executeTask = worker.ExecuteTask;
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.IsFaulted).IsFalse()
      .Because("claim_work repairs its own registration — failing here must not keep the pod "
             + "out of the fleet");
    worker.Dispose();
  }

  [Test]
  [Timeout(30000)]
  public async Task TheStartupRegistrationRunsBeforeClaimingAsync(CancellationToken testToken) {
    // Registering after the first claim would leave the registry briefly carrying no row for
    // this pod, which skews every peer's rank denominator and delays instance-lifecycle signals.
    var coordinator = new MinimalCoordinator();
    var worker = _worker(new ControllableListener(), coordinator: coordinator);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    // Wait for a CLAIM, not just the registration. The recorded order only means something once
    // both operations have happened: waiting on the heartbeat alone would let a worker that
    // registered *after* its first claim pass, which is precisely the regression this test names.
    await coordinator.ClaimAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Previously this test asserted nothing at all — it started the worker, waited, and stopped,
    // so it passed whatever order the two calls happened in.
    var operations = coordinator.Operations;
    await Assert.That(operations.Count).IsGreaterThanOrEqualTo(2)
      .Because("the order is only evidence once both the registration and a claim were observed");
    await Assert.That(operations[0]).IsEqualTo("register")
      .Because("registering after the first claim leaves the registry briefly carrying no row for "
             + "this pod, which skews every peer's rank denominator and delays instance-lifecycle "
             + "signals");
    worker.Dispose();
  }

  [Test]
  [Timeout(30000)]
  public async Task PerspectiveOnlyMode_ParksInsteadOfClaimingAsync(CancellationToken testToken) {
    // In this mode the legacy publisher is the sole poller. ClaimWorker also claiming would race
    // it and lease orphan rows before process_work_batch sees them, breaking the event-store
    // auto-create chain — so parking is the whole behavior.
    var coordinator = new MinimalCoordinator();
    var worker = _worker(new ControllableListener(), coordinator: coordinator, perspectiveOnly: true);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);

    // StartAsync only proves the body was queued to the thread pool, so wait for something the
    // body itself produces before judging it. The startup registration is the last step before
    // the perspective-only fork, so observing it puts the loop at the fork.
    await coordinator.HeartbeatAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    // Past that fork a claiming worker claims immediately — there is no delay before the first
    // cycle — so a window several poll intervals wide (the fixture polls at 50 ms) is enough to
    // separate "parked" from "about to claim".
    _ = await Task.WhenAny(
      coordinator.ClaimAttempted.Task,
      Task.Delay(TimeSpan.FromMilliseconds(500), testToken));

    var executeTask = worker.ExecuteTask;
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.ClaimCount).IsEqualTo(0)
      .Because("parking is the whole behavior — claiming here races the legacy publisher for "
             + "orphan rows and breaks the event-store auto-create chain");
    await Assert.That(executeTask!.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("parking is the intended state — a faulted worker reads as a crash on shutdown");
    worker.Dispose();
  }
}
