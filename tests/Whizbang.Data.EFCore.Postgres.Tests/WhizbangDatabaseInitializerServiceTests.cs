using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for <see cref="WhizbangDatabaseInitializerService"/>: the best-effort partition-recompute
/// cancellation contract, and the blocking (default) vs. opt-in non-blocking initialization behavior
/// including the migration timeout — all fail-closed on the schema-ready gate.
/// </summary>
[Category("Shard3")]
public class WhizbangDatabaseInitializerServiceTests {

  // ---------- best-effort partition recompute (cancellation contract) ----------

  [Test]
  public async Task TryRecompute_QueryCancellation_NonShutdownToken_IsSwallowedAsync() {
    // A plain OCE with a live (uncanceled) token is a query cancellation — must NOT escape.
    var service = _create(coordinator: new _ThrowingCoordinator(new OperationCanceledException()));
    await service.TryRecomputePartitionsAsync(CancellationToken.None);
  }

  [Test]
  public async Task TryRecompute_HostShutdown_CanceledToken_PropagatesAsync() {
    var service = _create(coordinator: new _ThrowingCoordinator(new OperationCanceledException()));
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await Assert.That(async () => await service.TryRecomputePartitionsAsync(cts.Token))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task TryRecompute_NonCancellationFailure_IsSwallowedAsync() {
    var service = _create(coordinator: new _ThrowingCoordinator(new InvalidOperationException("boom")));
    await service.TryRecomputePartitionsAsync(CancellationToken.None);
  }

  // ---------- blocking (default) initialization ----------

  [Test]
  public async Task Blocking_StartAsync_WaitsForInit_ThenMarksReadyAsync() {
    var gate = new SchemaReadyGate();
    var runner = new _HeldRunner();
    var service = _create(gate: gate, runner: runner, nonBlocking: false);

    var startTask = service.StartAsync(CancellationToken.None);
    // Default is blocking: StartAsync does not complete until initialization does.
    await Assert.That(startTask.IsCompleted).IsFalse();
    await Assert.That(gate.IsReady).IsFalse();

    runner.Complete();
    await startTask;
    await Assert.That(gate.IsReady).IsTrue();
  }

  // ---------- non-blocking initialization ----------

  [Test]
  public async Task NonBlocking_StartAsync_ReturnsBeforeInit_ThenMarksReadyWhenDoneAsync() {
    var gate = new SchemaReadyGate();
    var runner = new _HeldRunner();
    var service = _create(gate: gate, runner: runner, nonBlocking: true);

    // Non-blocking: StartAsync returns immediately so the host can bind + answer liveness.
    await service.StartAsync(CancellationToken.None);
    await Assert.That(gate.IsReady).IsFalse();   // migration still running → gate closed

    runner.Complete();
    await service.BackgroundInitTask!;           // deterministically await background completion
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task NonBlocking_InitFailure_RetriesUntilSuccessAsync() {
    // Self-heal: a failed background init must NOT close the gate forever — a transient
    // environment problem (connection exhaustion, a broken pool) recovers, and a pod that never
    // re-attempts is a NotReady zombie only a human can fix. Fail-closed WHILE retrying.
    var gate = new SchemaReadyGate();
    var attempts = 0;
    var runner = new _FakeRunner(_ => ++attempts <= 2
      ? throw new InvalidOperationException("transient boom")
      : Task.CompletedTask);
    var service = _create(gate: gate, runner: runner, nonBlocking: true, initRetryDelay: TimeSpan.Zero);

    await service.StartAsync(CancellationToken.None);
    await service.BackgroundInitTask!;           // completes when init finally SUCCEEDS

    await Assert.That(attempts).IsEqualTo(3)
      .Because("the background loop re-attempts after each failure instead of giving up.");
    await Assert.That(gate.IsReady).IsTrue()
      .Because("the gate opens the moment an attempt succeeds — the pod self-heals.");
  }

  [Test]
  public async Task NonBlocking_InitFailure_GateStaysClosedWhileRetrying_ShutdownStopsTheLoopAsync() {
    var gate = new SchemaReadyGate();
    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var attempts = 0;
    var runner = new _FakeRunner(_ => {
      attempts++;
      failed.TrySetResult();
      throw new InvalidOperationException("still broken");
    });
    var service = _create(gate: gate, runner: runner, nonBlocking: true,
      initRetryDelay: TimeSpan.FromMinutes(5));

    await service.StartAsync(CancellationToken.None);
    await failed.Task;                            // first attempt failed → loop is in its retry delay
    await Assert.That(gate.IsReady).IsFalse()
      .Because("fail-closed while retrying: nothing touches an unmigrated schema.");

    await service.StopAsync(CancellationToken.None);
    await service.BackgroundInitTask!;            // shutdown cancels the retry delay and ends the loop
    await Assert.That(gate.IsReady).IsFalse();
    await Assert.That(attempts).IsEqualTo(1);
  }

  [Test]
  public async Task NonBlocking_InitFailure_DoesNotFaultLifecycle_WhileRetryingAsync() {
    // Retrying must NOT fault the lifecycle: FaultAsync drives Faulted -> Halted with no recovery
    // API, so faulting on a transient failure would permanently halt a pipeline whose next attempt
    // succeeds. The closed gate (readiness) is the honest health signal while init re-attempts.
    var gate = new SchemaReadyGate();
    var attempts = 0;
    var runner = new _FakeRunner(_ => ++attempts == 1
      ? throw new InvalidOperationException("transient boom")
      : Task.CompletedTask);
    var lifecycle = new _FakeLifecycle();
    var service = _create(gate: gate, runner: runner, nonBlocking: true, lifecycle: lifecycle,
      initRetryDelay: TimeSpan.Zero);

    await service.StartAsync(CancellationToken.None);
    await service.BackgroundInitTask!;

    await Assert.That(lifecycle.FaultCount).IsEqualTo(0);
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task NonBlocking_InitSuccess_DoesNotFaultLifecycleAsync() {
    // The happy path never faults the lifecycle.
    var gate = new SchemaReadyGate();
    var runner = new _HeldRunner();
    var lifecycle = new _FakeLifecycle();
    var service = _create(gate: gate, runner: runner, nonBlocking: true, lifecycle: lifecycle);

    await service.StartAsync(CancellationToken.None);
    runner.Complete();
    await service.BackgroundInitTask!;

    await Assert.That(lifecycle.FaultCount).IsEqualTo(0);
    await Assert.That(gate.IsReady).IsTrue();
  }

  [Test]
  public async Task NonBlocking_MigrationTimeout_RetriesAndRecoversAsync() {
    var gate = new SchemaReadyGate();
    var fakeTime = new FakeTimeProvider();
    var attempts = 0;
    var runner = new _SignalingRunner(ct => ++attempts == 1
      ? Task.Delay(Timeout.Infinite, ct)          // first attempt hangs → trips the timeout
      : Task.CompletedTask);                      // retry succeeds
    var service = _create(gate: gate, runner: runner, nonBlocking: true,
      migrationTimeout: TimeSpan.FromMinutes(5), timeProvider: fakeTime,
      initRetryDelay: TimeSpan.Zero);

    await service.StartAsync(CancellationToken.None);
    await runner.Entered.Task;                    // migration started → timeout timer is armed
    await Assert.That(gate.IsReady).IsFalse();

    fakeTime.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); // trip the timeout deterministically
    await service.BackgroundInitTask!;            // timeout → immediate retry → success
    await Assert.That(attempts).IsEqualTo(2);
    await Assert.That(gate.IsReady).IsTrue()
      .Because("a migration that blew its ceiling is retried, not abandoned — the pod self-heals.");
  }

  [Test]
  public async Task NonBlocking_WithinTimeout_MarksReadyAsync() {
    var gate = new SchemaReadyGate();
    var fakeTime = new FakeTimeProvider();
    var runner = new _HeldRunner();
    var service = _create(gate: gate, runner: runner, nonBlocking: true,
      migrationTimeout: TimeSpan.FromMinutes(5), timeProvider: fakeTime);

    await service.StartAsync(CancellationToken.None);
    runner.Complete();                            // migration finishes well within the ceiling (no time advance)
    await service.BackgroundInitTask!;
    await Assert.That(gate.IsReady).IsTrue();
  }

  // ---------- helpers ----------

  private static WhizbangDatabaseInitializerService _create(
      IWorkCoordinator? coordinator = null,
      ISchemaReadyGate? gate = null,
      ISchemaInitializationRunner? runner = null,
      bool nonBlocking = false,
      TimeSpan? migrationTimeout = null,
      TimeProvider? timeProvider = null,
      IWhizbangLifecycleState? lifecycle = null,
      TimeSpan? initRetryDelay = null) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddSingleton(coordinator);
    }
    if (lifecycle is not null) {
      services.AddSingleton(lifecycle);
    }
    var provider = services.BuildServiceProvider();
    return new WhizbangDatabaseInitializerService(
      provider,
      runner ?? new _FakeRunner(_ => Task.CompletedTask),
      gate ?? new SchemaReadyGate(),
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new SchemaInitializationOptions {
        NonBlockingSchemaInit = nonBlocking,
        MigrationTimeout = migrationTimeout,
        InitRetryDelay = initRetryDelay ?? TimeSpan.FromSeconds(30),
      }),
      timeProvider ?? TimeProvider.System,
      NullLogger<WhizbangDatabaseInitializerService>.Instance);
  }

  /// <summary>Runner whose migration blocks until <see cref="Complete"/> is called.</summary>
  private sealed class _HeldRunner : ISchemaInitializationRunner {
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Complete() => _tcs.TrySetResult();
    public Task RunAsync(CancellationToken cancellationToken) => _tcs.Task.WaitAsync(cancellationToken);
  }

  /// <summary>Runner that runs a supplied behavior.</summary>
  private sealed class _FakeRunner(Func<CancellationToken, Task> behavior) : ISchemaInitializationRunner {
    public Task RunAsync(CancellationToken cancellationToken) => behavior(cancellationToken);
  }

  /// <summary>Runner that signals when its migration has started (so a timeout timer is armed).</summary>
  private sealed class _SignalingRunner(Func<CancellationToken, Task> behavior) : ISchemaInitializationRunner {
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task RunAsync(CancellationToken cancellationToken) {
      Entered.TrySetResult();
      return behavior(cancellationToken);
    }
  }

  /// <summary>Lifecycle that records how many times <see cref="FaultAsync"/> was invoked.</summary>
  private sealed class _FakeLifecycle : IWhizbangLifecycleState {
    public int FaultCount { get; private set; }
    public LifecyclePhase Phase => LifecyclePhase.Migrating;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => default;
    public ValueTask FaultAsync(CancellationToken cancellationToken) {
      FaultCount++;
      return default;
    }
  }

  /// <summary>Coordinator whose partition recompute throws a supplied exception; defaults cover the rest.</summary>
  private sealed class _ThrowingCoordinator(Exception toThrow) : IWorkCoordinator {
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(
        int partitionCount, CancellationToken cancellationToken = default)
      => throw toThrow;

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
