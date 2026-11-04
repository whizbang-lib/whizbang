// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// The state of the instance's <see cref="WhizbangRunPermit"/> (#2045): open (<see cref="Running"/>), closed but
/// resumable (<see cref="Paused"/>, after a pause or a stand-down), or closed for good (<see cref="Stopped"/>). Only the
/// run-control path (<see cref="IWhizbangRunController"/>) changes it; the lifecycle phase is what that path reports.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangRunPermitTests.cs:Paused_BlocksUntilResumedAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangRunPermitTests.cs:Stopped_CancelsAwaitersAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangRunPermitTests.cs:Stopped_NeverReopensAsync</tests>
public enum RunState {
  /// <summary>Permit open — awaiters proceed.</summary>
  Running,
  /// <summary>Permit closed but resumable — awaiters block until re-opened.</summary>
  Paused,
  /// <summary>Permit closed for good: awaiters are canceled, held work is finished, nothing new is taken.</summary>
  Stopped
}
