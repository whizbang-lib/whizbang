namespace Whizbang.Core.Diagnostics;

/// <summary>
/// A stopwatch that tracks "active" time (time when execution is running, not paused at breakpoints).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key Properties:</strong>
/// </para>
/// <list type="bullet">
/// <item><see cref="ActiveElapsed"/> - Time spent actually executing (excludes frozen periods)</item>
/// <item><see cref="WallElapsed"/> - Total wall clock time since start</item>
/// <item><see cref="FrozenTime"/> - Time spent paused/frozen</item>
/// </list>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// using var clock = new DebuggerAwareClock();
/// var stopwatch = clock.StartNew();
///
/// // Do work...
///
/// if (stopwatch.HasTimedOut(TimeSpan.FromSeconds(5))) {
///   // Only triggers based on active execution time
/// }
/// </code>
/// </remarks>
/// <docs>features/debugger-aware-clock</docs>
/// <tests>Whizbang.Core.Tests/Diagnostics/DebuggerAwareClockTests.cs</tests>
public interface IActiveStopwatch {
  /// <summary>
  /// Gets the elapsed time excluding frozen/paused periods.
  /// </summary>
  /// <remarks>
  /// This value only advances when the process is actively executing.
  /// It will not include time spent at breakpoints or externally paused.
  /// </remarks>
  TimeSpan ActiveElapsed { get; }

  /// <summary>
  /// Gets the total wall clock elapsed time since start.
  /// </summary>
  /// <remarks>
  /// This value always advances, regardless of pause state.
  /// </remarks>
  TimeSpan WallElapsed { get; }

  /// <summary>
  /// Gets the total time spent in a frozen/paused state.
  /// </summary>
  /// <remarks>
  /// Calculated as <c>WallElapsed - ActiveElapsed</c>.
  /// </remarks>
  TimeSpan FrozenTime { get; }

  /// <summary>
  /// Checks if the active elapsed time exceeds the specified timeout.
  /// </summary>
  /// <param name="timeout">The timeout duration.</param>
  /// <returns><c>true</c> if active elapsed time >= timeout; otherwise, <c>false</c>.</returns>
  /// <remarks>
  /// Uses <see cref="ActiveElapsed"/> for comparison, so time spent at breakpoints
  /// does not count towards the timeout.
  /// </remarks>
  bool HasTimedOut(TimeSpan timeout);

  /// <summary>
  /// Halts the stopwatch, freezing all elapsed time values.
  /// </summary>
  void Halt();
}
