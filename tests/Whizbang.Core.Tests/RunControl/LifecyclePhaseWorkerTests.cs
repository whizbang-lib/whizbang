using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="LifecyclePhaseWorker"/>: advances to Migrating at startup, then to Ready once the
/// schema-ready gate opens — the driver that moves run-control from the gate. Completion-signal based.
/// </summary>
public class LifecyclePhaseWorkerTests {

  private sealed class RecordingLifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; private set; } = LifecyclePhase.Starting;
    public TaskCompletionSource Migrating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phase = phase;
      if (phase == LifecyclePhase.Migrating) {
        Migrating.TrySetResult();
      }
      if (phase == LifecyclePhase.Ready) {
        Ready.TrySetResult();
      }
      return default;
    }
  }

  [Test]
  public async Task AdvancesMigratingAtStart_ThenReadyWhenGateOpensAsync() {
    var lifecycle = new RecordingLifecycle();
    var gate = new SchemaReadyGate();
    var worker = new LifecyclePhaseWorker(lifecycle, gate);

    await worker.StartAsync(CancellationToken.None);
    await lifecycle.Migrating.Task; // advanced to Migrating at startup
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Migrating);
    await Assert.That(lifecycle.Ready.Task.IsCompleted).IsFalse(); // gate still closed

    gate.MarkReady();
    await lifecycle.Ready.Task; // advanced to Ready once the gate opens
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Ready);

    await worker.StopAsync(CancellationToken.None);
  }
}
