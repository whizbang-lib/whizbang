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

  private sealed class RecordingLifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; private set; } = LifecyclePhase.Starting;
    public List<LifecyclePhase> Seen { get; } = [];
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phase = phase;
      Seen.Add(phase);
      return default;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) {
      Phase = LifecyclePhase.Faulted;
      Seen.Add(LifecyclePhase.Faulted);
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
    // RecordingLifecycle completes synchronously, so ExecuteAsync has already run Connecting and
    // Migrating (both real awaits complete instantly) by the time it suspends on the still-closed
    // schema gate — StartAsync only returns once ExecuteAsync reaches that first genuine await.
    await Assert.That(lifecycle.Seen).Contains(LifecyclePhase.Migrating);

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
    await Assert.That(lifecycle.Seen).Contains(LifecyclePhase.AcceptingCommands);

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
