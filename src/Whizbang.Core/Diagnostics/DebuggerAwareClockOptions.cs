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
/// <docs>features/debugger-aware-clock</docs>
/// <tests>Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs</tests>
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
}
