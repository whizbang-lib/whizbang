namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Configurable detection modes for debugger-aware timing based on developer preference.
/// </summary>
/// <remarks>
/// <para>
/// Different modes trade off between accuracy and performance. The system can detect
/// when execution is paused (e.g., at a breakpoint) to prevent false timeouts.
/// </para>
/// </remarks>
/// <docs>extending/features/debugger-aware-clock</docs>
/// <tests>Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs</tests>
public enum DebuggerDetectionMode {
  /// <summary>
  /// Always use wall clock time (no breakpoint detection).
  /// Fastest option but timeouts will occur during debugging.
  /// </summary>
  Disabled,

  /// <summary>
  /// Only detect pauses when <see cref="System.Diagnostics.Debugger.IsAttached"/> is true.
  /// Fast path in production, detection when debugging.
  /// </summary>
  DebuggerAttached,

  /// <summary>
  /// Use CPU time sampling to detect frozen periods.
  /// Works without debugger attached (useful for external pauses).
  /// </summary>
  CpuTimeSampling,

  /// <summary>
  /// Wait for VS Code extension or other external tool to signal pauses.
  /// Most accurate when extension is active.
  /// </summary>
  ExternalHook,

  /// <summary>
  /// Auto-select the best available method based on environment.
  /// Default setting that adapts to the current context.
  /// </summary>
  Auto
}
