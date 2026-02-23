namespace Whizbang.Core.Diagnostics;

/// <summary>
/// A clock service that tracks active vs frozen time, enabling debugger-aware timeouts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Problem Solved:</strong>
/// When debugging, hitting a breakpoint freezes execution but wall-clock time continues,
/// causing false timeouts across the system (perspective sync, transport layer, health checks, etc.).
/// </para>
/// <para>
/// <strong>Solution:</strong>
/// This service tracks "active" time that only advances when code is actually executing,
/// enabling timeouts that don't trigger while paused at breakpoints.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
///   Mode = DebuggerDetectionMode.Auto
/// });
///
/// var stopwatch = clock.StartNew();
///
/// // Work that might be interrupted by breakpoints...
///
/// if (stopwatch.HasTimedOut(TimeSpan.FromSeconds(5))) {
///   // Only triggers based on active execution time
/// }
/// </code>
/// <para>
/// <strong>Detection Modes:</strong>
/// </para>
/// <list type="bullet">
/// <item><see cref="DebuggerDetectionMode.Disabled"/> - Always use wall time (fastest)</item>
/// <item><see cref="DebuggerDetectionMode.DebuggerAttached"/> - Detect only when debugger attached</item>
/// <item><see cref="DebuggerDetectionMode.CpuTimeSampling"/> - Use CPU time sampling</item>
/// <item><see cref="DebuggerDetectionMode.ExternalHook"/> - Wait for VS Code extension signals</item>
/// <item><see cref="DebuggerDetectionMode.Auto"/> - Auto-select best method</item>
/// </list>
/// </remarks>
/// <docs>features/debugger-aware-clock</docs>
/// <tests>Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs</tests>
public interface IDebuggerAwareClock : IDisposable {
  /// <summary>
  /// Gets the current detection mode.
  /// </summary>
  DebuggerDetectionMode Mode { get; }

  /// <summary>
  /// Gets a value indicating whether execution is currently paused (at a breakpoint or externally).
  /// </summary>
  bool IsPaused { get; }

  /// <summary>
  /// Creates and starts a new stopwatch that tracks active execution time.
  /// </summary>
  /// <returns>A new <see cref="IActiveStopwatch"/> instance.</returns>
  IActiveStopwatch StartNew();

  /// <summary>
  /// Subscribes to pause state changes for external monitoring.
  /// </summary>
  /// <param name="handler">Action called when pause state changes. Parameter is <c>true</c> when paused.</param>
  /// <returns>A disposable subscription that can be used to unsubscribe.</returns>
  /// <remarks>
  /// Useful for VS Code extension integration or test synchronization.
  /// </remarks>
  IDisposable OnPauseStateChanged(Action<bool> handler);
}
