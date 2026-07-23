using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="LifecyclePhaseWorker"/>: advances Connecting → Migrating at startup, then to
/// Running once the schema-ready gate opens — the driver that moves the lifecycle from the gate.
/// Completion-signal based.
/// </summary>
public class LifecyclePhaseWorkerTests {

  private sealed class RecordingLifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; private set; } = LifecyclePhase.Starting;
    public List<LifecyclePhase> Seen { get; } = [];
    public TaskCompletionSource Migrating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phase = phase;
      Seen.Add(phase);
      if (phase == LifecyclePhase.Migrating) {
        Migrating.TrySetResult();
      }
      if (phase == LifecyclePhase.Running) {
        Running.TrySetResult();
      }
      return default;
    }
  }

  [Test]
  public async Task AdvancesConnectingThenMigrating_ThenRunningWhenGateOpensAsync() {
    var lifecycle = new RecordingLifecycle();
    var gate = new SchemaReadyGate();
    var worker = new LifecyclePhaseWorker(lifecycle, gate);

    await worker.StartAsync(CancellationToken.None);
    await lifecycle.Migrating.Task; // advanced through Connecting to Migrating at startup
    await Assert.That(lifecycle.Seen).Contains(LifecyclePhase.Connecting);
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Migrating);
    await Assert.That(lifecycle.Running.Task.IsCompleted).IsFalse(); // gate still closed

    gate.MarkReady();
    await lifecycle.Running.Task; // advanced to Running once the gate opens
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Running);

    await worker.StopAsync(CancellationToken.None);
  }
}
