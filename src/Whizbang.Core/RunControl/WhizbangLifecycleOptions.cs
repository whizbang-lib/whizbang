// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// Tunables for the coordinated lifecycle state machine. Every managed resource must acknowledge each
/// transition within <see cref="TransitionAckTimeout"/>, or the coordinator raises an error that faults
/// the system; on a fault, <see cref="FaultRecordWindow"/> is the bounded window resources get to
/// record/report before the machine reaches <see cref="LifecyclePhase.Halted"/>.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangLifecycleCoordinatorTests.cs:Transition_Timeout_RaisesAckTimeoutAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangLifecycleStateTests.cs:FaultAsync_DrivesFaultedThenHaltsAfterRecordWindowAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControlDiTests.cs:AddWhizbangRunControl_RegistersCoordinatorAndStateAsync</tests>
public sealed class WhizbangLifecycleOptions {
  /// <summary>Per-resource acknowledgement budget on each coordinated transition. Default 30s.</summary>
  public TimeSpan TransitionAckTimeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>How long the system stays in <see cref="LifecyclePhase.Faulted"/> (record/report) before <see cref="LifecyclePhase.Halted"/>. Default 5s.</summary>
  public TimeSpan FaultRecordWindow { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// How long the run-control path (#2045) waits for the work in hand to finish before it releases what is still held and
  /// confirms the halt. Work still in hand after this keeps its leases, which lapse to a peer. Default 20s, which leaves a
  /// host's default 30-second shutdown budget time to stop its workers and deregister.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangRunControllerTests.cs:Halt_AHoldThatOutlastsTheBound_IsLeftAndTheHaltGoesOnAsync</tests>
  public TimeSpan HeldWorkTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
