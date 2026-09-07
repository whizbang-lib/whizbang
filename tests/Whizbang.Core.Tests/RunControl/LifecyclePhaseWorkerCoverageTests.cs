using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers the two shutdown-during-wait branches of <see cref="LifecyclePhaseWorker.ExecuteAsync"/>:
/// a cancellation observed on the schema-ready gate, and one observed on the read-models-ready
/// gate. Both must return cleanly (fail-closed — the phase stays where it was) instead of letting
/// the cancellation propagate uncaught or falling through to advance a phase whose prerequisite
/// gate never actually opened.
/// </summary>
public class LifecyclePhaseWorkerCoverageTests {

  /// <summary>
  /// Records every lifecycle phase the worker advances through, and signals the two phases these
  /// tests need to wait on.
  /// </summary>
  /// <remarks>
  /// The phase signals are the point. The host starts ExecuteAsync on the thread pool, so
  /// StartAsync returning proves only that the worker was scheduled — not that it advanced any
  /// phase yet. Asserting on <see cref="Seen"/> straight after StartAsync passes on an idle
  /// machine and races on a loaded one; each test here waits on the signal for the phase it needs
  /// instead. <see cref="Seen"/> is written from the worker's thread and read from the test's, so
  /// it is guarded rather than handed out raw.
  /// </remarks>
  private sealed class RecordingLifecycle : IWhizbangLifecycleState {
    private readonly Lock _lock = new();
    private readonly List<LifecyclePhase> _seen = [];
    private LifecyclePhase _phase = LifecyclePhase.Starting;

    public LifecyclePhase Phase {
      get { lock (_lock) { return _phase; } }
    }

    public IReadOnlyList<LifecyclePhase> Seen {
      get { lock (_lock) { return [.. _seen]; } }
    }

    public TaskCompletionSource Migrating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AcceptingCommands { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      lock (_lock) {
        _phase = phase;
        _seen.Add(phase);
      }
      if (phase == LifecyclePhase.Migrating) {
        Migrating.TrySetResult();
      }
      if (phase == LifecyclePhase.AcceptingCommands) {
        AcceptingCommands.TrySetResult();
      }
      return default;
    }

    public ValueTask FaultAsync(CancellationToken cancellationToken) {
      lock (_lock) {
        _phase = LifecyclePhase.Faulted;
        _seen.Add(LifecyclePhase.Faulted);
      }
      return default;
    }
  }

  /// <summary>
  /// If this early return regressed — the exception left uncaught, or the loop fell through to
  /// advance AcceptingCommands anyway — a host shutting down while migrations were still pending
  /// would either crash the worker on an unhandled cancellation, or advance the lifecycle machine
  /// past a schema that was never confirmed ready.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task SchemaGateWaitCanceled_ReturnsWithoutAdvancingPastMigratingAsync(CancellationToken testToken) {
    var lifecycle = new RecordingLifecycle();
    var gate = new SchemaReadyGate(); // never marked ready — the wait blocks until canceled
    var worker = new LifecyclePhaseWorker(lifecycle, gate);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    // Wait for the phase itself, not for StartAsync: the host runs ExecuteAsync on the thread
    // pool, so StartAsync returning says nothing about how far the worker got. Reaching Migrating
    // is also the precondition this test needs — it puts the worker on the schema gate that the
    // cancellation below is meant to interrupt.
    await lifecycle.Migrating.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    await cts.CancelAsync();

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("the catch must return, not leave ExecuteAsync parked on the canceled wait");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a shutdown observed on the schema-ready wait is an ordinary stop, not a worker fault");
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Migrating)
      .Because("fail-closed: the schema was never confirmed ready, so nothing past Migrating may run");
    await Assert.That(lifecycle.Seen).DoesNotContain(LifecyclePhase.AcceptingCommands);

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// If this early return regressed, a host shutting down between the write-side barrier
  /// (AcceptingCommands) and the read-side barrier would either crash on the unhandled
  /// cancellation, or advance to Running before the perspective repair that Running promises had
  /// actually completed — serving reads against read models nobody confirmed were consistent.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ReadModelGateWaitCanceled_ReturnsWithoutAdvancingToRunningAsync(CancellationToken testToken) {
    var lifecycle = new RecordingLifecycle();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    var readGate = new ReadModelsReadyGate(); // never marked ready — the wait blocks until canceled
    var worker = new LifecyclePhaseWorker(lifecycle, schemaGate, readGate);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    // Same reasoning as above: wait for the write-side barrier to actually fire. Without this the
    // cancellation below can land before the worker ever reaches the read-model gate, and the test
    // would then "pass" having never exercised the wait it names.
    await lifecycle.AcceptingCommands.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    await cts.CancelAsync();

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("the catch must return, not leave ExecuteAsync parked on the canceled wait");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a shutdown observed on the read-model wait is an ordinary stop, not a worker fault");
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.AcceptingCommands)
      .Because("fail-closed: the read models were never confirmed consistent, so Running may not fire");
    await Assert.That(lifecycle.Seen).DoesNotContain(LifecyclePhase.Running);

    await worker.StopAsync(CancellationToken.None);
  }
}
