// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Configuration options for <see cref="IDebuggerAwareClock"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// var options = new DebuggerAwareClockOptions {
///   Mode = DebuggerDetectionMode.CpuTimeSampling,
///   SamplingInterval = TimeSpan.FromMilliseconds(50),
///   FrozenThreshold = 5.0
/// };
/// </code>
/// </remarks>
/// <docs>extending/features/debugger-aware-clock</docs>
/// <tests>tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs:DebuggerAwareClockOptions_CanSetFrozenThresholdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs:DebuggerAwareClock_FrozenThreshold_CanBeConfiguredAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs:DebuggerAwareClock_IsPaused_InAutoMode_WhenNoDebuggerAttachedAsync</tests>
public sealed class DebuggerAwareClockOptions {
  /// <summary>
  /// Gets or sets the detection mode for identifying paused states.
  /// </summary>
  /// <value>Default: <see cref="DebuggerDetectionMode.Auto"/>.</value>
  public DebuggerDetectionMode Mode { get; set; } = DebuggerDetectionMode.Auto;

  /// <summary>
  /// Gets or sets the CPU sampling interval for <see cref="DebuggerDetectionMode.CpuTimeSampling"/> mode.
  /// </summary>
  /// <value>Default: 100 milliseconds.</value>
  public TimeSpan SamplingInterval { get; set; } = TimeSpan.FromMilliseconds(100);

  /// <summary>
  /// Gets or sets the threshold ratio (wall time / CPU time) to consider execution "frozen".
  /// </summary>
  /// <remarks>
  /// A value of 10.0 means if wall time is more than 10x CPU time, the process is considered frozen.
  /// </remarks>
  /// <value>Default: 10.0.</value>
  public double FrozenThreshold { get; set; } = 10.0;

  /// <summary>
  /// Source of accumulated CPU time for freeze detection. Null (the default) reads the current
  /// process's <c>TotalProcessorTime</c>.
  /// </summary>
  /// <remarks>
  /// The seam exists for determinism: freeze detection compares CPU delta to wall delta, and a test
  /// that relies on the REAL process actually idling is hostage to whatever else the machine is
  /// doing — on a loaded CI runner the process keeps accruing CPU and "frozen" never fires, which
  /// presents as a 30-second timeout unrelated to any code change. A test injects a constant (or
  /// scripted) source instead and the transition becomes a fact, not a race.
  /// </remarks>
  public Func<TimeSpan>? CpuTimeSource { get; set; }

  /// <summary>
  /// Optional source for whether a debugger is attached; <see langword="null"/> (the default) reads
  /// <see cref="System.Diagnostics.Debugger.IsAttached"/>.
  /// </summary>
  /// <remarks>
  /// The same seam as <see cref="CpuTimeSource"/>, for the same reason: the attached-debugger modes can
  /// only be driven deterministically when the answer is the test's to give, not the test runner's.
  /// </remarks>
  public Func<bool>? DebuggerAttachedSource { get; set; }

  /// <summary>
  /// Optional source for the wall clock the sampler measures each interval on; <see langword="null"/>
  /// (the default) reads <see cref="DateTimeOffset.UtcNow"/>.
  /// </summary>
  /// <remarks>
  /// Freeze detection needs a wall interval of at least 200 ms, so with the real clock a test of a
  /// sampling decision has to wait for real time to pass; a source makes the interval the test's to set.
  /// </remarks>
  public Func<DateTimeOffset>? WallClockSource { get; set; }
}
