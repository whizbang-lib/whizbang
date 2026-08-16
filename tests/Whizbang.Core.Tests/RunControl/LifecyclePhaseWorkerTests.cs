using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="LifecyclePhaseWorker"/>: advances Connecting → Migrating at startup, then
/// walks the CQRS ladder — AcceptingCommands when the schema gate opens (the write side has its
/// event store and outbox; commands become safe BEFORE queries), Running when the read-model
/// barrier releases (perspectives repaired; the full data plane serves). Completion-signal based.
/// </summary>
public class LifecyclePhaseWorkerTests {

  private sealed class RecordingLifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; private set; } = LifecyclePhase.Starting;
    public List<LifecyclePhase> Seen { get; } = [];
    public TaskCompletionSource Migrating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AcceptingCommands { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
      Phase = phase;
      Seen.Add(phase);
      if (phase == LifecyclePhase.Migrating) {
        Migrating.TrySetResult();
      }
      if (phase == LifecyclePhase.AcceptingCommands) {
        AcceptingCommands.TrySetResult();
      }
      if (phase == LifecyclePhase.Running) {
        Running.TrySetResult();
      }
      return default;
    }
    public ValueTask FaultAsync(CancellationToken cancellationToken) {
      Phase = LifecyclePhase.Faulted;
      Seen.Add(LifecyclePhase.Faulted);
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
    await Assert.That(lifecycle.Seen).Contains(LifecyclePhase.AcceptingCommands)
      .Because("without a read-model gate the two moments coincide, but the ladder is still walked");
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Running);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task WithAReadModelGate_HoldsAtAcceptingCommands_UntilTheReadSideReleasesAsync() {
    var lifecycle = new RecordingLifecycle();
    var schemaGate = new SchemaReadyGate();
    var readGate = new ReadModelsReadyGate();
    var worker = new LifecyclePhaseWorker(lifecycle, schemaGate, readGate);

    await worker.StartAsync(CancellationToken.None);
    schemaGate.MarkReady();
    await lifecycle.AcceptingCommands.Task;
    await Task.Delay(150);
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.AcceptingCommands)
      .Because("CQRS made observable: the schema gate released the WRITE side, but the "
             + "perspective repair is still running — commands are safe, queries are not yet");
    await Assert.That(lifecycle.Running.Task.IsCompleted).IsFalse();

    readGate.MarkReady();
    await lifecycle.Running.Task;
    await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.Running)
      .Because("Running means the FULL data plane serves — reads and commands");

    await worker.StopAsync(CancellationToken.None);
  }
}
