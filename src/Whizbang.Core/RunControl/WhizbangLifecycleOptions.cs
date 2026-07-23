namespace Whizbang.Core.RunControl;

/// <summary>
/// Tunables for the coordinated lifecycle state machine. Every managed resource must acknowledge each
/// transition within <see cref="TransitionAckTimeout"/>, or the coordinator raises an error that faults
/// the system; on a fault, <see cref="FaultRecordWindow"/> is the bounded window resources get to
/// record/report before the machine reaches <see cref="LifecyclePhase.Halted"/>.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public sealed class WhizbangLifecycleOptions {
  /// <summary>Per-resource acknowledgement budget on each coordinated transition. Default 30s.</summary>
  public TimeSpan TransitionAckTimeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>How long the system stays in <see cref="LifecyclePhase.Faulted"/> (record/report) before <see cref="LifecyclePhase.Halted"/>. Default 5s.</summary>
  public TimeSpan FaultRecordWindow { get; set; } = TimeSpan.FromSeconds(5);
}
