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
public class WhizbangDatabaseInitializerServiceTests {

  // ---------- best-effort partition recompute (cancellation contract) ----------

  [Test]
  public async Task TryRecompute_QueryCancellation_NonShutdownToken_IsSwallowedAsync() {
    // A plain OCE with a live (uncancelled) token is a query cancellation — must NOT escape.
    var service = _create(coordinator: new _ThrowingCoordinator(new OperationCanceledException()));
    await service.TryRecomputePartitionsAsync(CancellationToken.None);
  }

  [Test]
  public async Task TryRecompute_HostShutdown_CancelledToken_PropagatesAsync() {
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
  public async Task NonBlocking_InitFailure_GateStaysClosedAsync() {
    var gate = new SchemaReadyGate();
    var runner = new _FakeRunner(_ => throw new InvalidOperationException("migration boom"));
    var service = _create(gate: gate, runner: runner, nonBlocking: true);

    await service.StartAsync(CancellationToken.None);
    await service.BackgroundInitTask!;           // background catches + logs, then completes
    await Assert.That(gate.IsReady).IsFalse();   // fail-closed: gate never opened
  }

  [Test]
  public async Task NonBlocking_InitFailure_DrivesLifecycleFaultAsync() {
    // A failed init must fault the managed-resource lifecycle (Faulted -> record window -> Halted) so
    // health reports the failure instead of looping "still initializing" forever.
    var gate = new SchemaReadyGate();
    var runner = new _FakeRunner(_ => throw new InvalidOperationException("migration boom"));
    var lifecycle = new _FakeLifecycle();
    var service = _create(gate: gate, runner: runner, nonBlocking: true, lifecycle: lifecycle);

    await service.StartAsync(CancellationToken.None);
    await service.BackgroundInitTask!;

    await Assert.That(lifecycle.FaultCount).IsEqualTo(1);
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
  public async Task NonBlocking_MigrationTimeout_GateStaysClosedAsync() {
    var gate = new SchemaReadyGate();
    var fakeTime = new FakeTimeProvider();
    var runner = new _SignalingRunner(ct => Task.Delay(Timeout.Infinite, ct)); // never completes on its own
    var service = _create(gate: gate, runner: runner, nonBlocking: true,
      migrationTimeout: TimeSpan.FromMinutes(5), timeProvider: fakeTime);

    await service.StartAsync(CancellationToken.None);
    await runner.Entered.Task;                    // migration started → timeout timer is armed
    await Assert.That(gate.IsReady).IsFalse();

    fakeTime.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); // trip the timeout deterministically
    await service.BackgroundInitTask!;            // background observes the timeout, fail-closed
    await Assert.That(gate.IsReady).IsFalse();    // never opened
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
      IWhizbangLifecycleState? lifecycle = null) {
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
