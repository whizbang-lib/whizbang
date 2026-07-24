using System;
using System.Diagnostics;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// A stopwatch that can detect debugger attachment to mark timing as approximate.
/// Uses <see cref="Stopwatch"/> internally for high-resolution timing.
/// </summary>
/// <remarks>
/// When <see cref="Debugger.IsAttached"/> is true, elapsed times may include
/// time spent paused at breakpoints. The <see cref="IsApproximate"/> property
/// indicates this condition.
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#diagnostics</docs>
internal sealed class DebugAwareStopwatch {
  private readonly Stopwatch _stopwatch = new();
  private bool _debuggerWasAttached;

  /// <summary>
  /// Gets the elapsed time. May include debugger pause time if <see cref="IsApproximate"/> is true.
  /// </summary>
  public TimeSpan Elapsed => _stopwatch.Elapsed;

  /// <summary>
  /// Gets whether the elapsed time is approximate (debugger was attached during measurement).
  /// </summary>
  public bool IsApproximate => _debuggerWasAttached;

  /// <summary>
  /// Gets whether the stopwatch is currently running.
  /// </summary>
  public bool IsRunning => _stopwatch.IsRunning;

  /// <summary>
  /// Starts or resumes measuring elapsed time.
  /// </summary>
  public void Start() {
    if (Debugger.IsAttached) {
      _debuggerWasAttached = true;
    }
    _stopwatch.Start();
  }

  /// <summary>
  /// Stops measuring elapsed time.
  /// </summary>
  public void Stop() {
    _stopwatch.Stop();
  }

  /// <summary>
  /// Stops and resets the stopwatch.
  /// </summary>
  public void Reset() {
    _stopwatch.Reset();
    _debuggerWasAttached = false;
  }

  /// <summary>
  /// Stops, resets, and starts the stopwatch.
  /// </summary>
  public void Restart() {
    _debuggerWasAttached = Debugger.IsAttached;
    _stopwatch.Restart();
  }

  /// <summary>
  /// Creates and starts a new instance.
  /// </summary>
  public static DebugAwareStopwatch StartNew() {
    var sw = new DebugAwareStopwatch();
    sw.Start();
    return sw;
  }
}
