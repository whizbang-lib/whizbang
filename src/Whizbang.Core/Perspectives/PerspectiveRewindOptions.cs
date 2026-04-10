namespace Whizbang.Core.Perspectives;

/// <summary>
/// Configuration options for perspective rewind detection, execution, and startup repair.
/// Controls whether rewinds are enabled, startup scan behavior, and concurrency limits.
/// </summary>
/// <docs>fundamentals/perspectives/rewind</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRewindOptionsTests.cs</tests>
public class PerspectiveRewindOptions {
  /// <summary>
  /// Master switch for rewind detection and execution.
  /// When disabled, out-of-order events are detected (Phase 4.6B) but not replayed.
  /// Default: true.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Whether to scan for and repair streams needing rewind on service startup.
  /// When enabled, queries wh_perspective_cursors for RewindRequired flag
  /// and processes them before or during normal polling.
  /// Default: true.
  /// </summary>
  public bool StartupScanEnabled { get; set; } = true;

  /// <summary>
  /// Controls whether startup rewinds block the worker from polling
  /// or run concurrently in the background.
  /// Default: Blocking (rewinds complete before serving reads).
  /// </summary>
  public RewindStartupMode StartupRewindMode { get; set; } = RewindStartupMode.Blocking;

  /// <summary>
  /// Maximum number of concurrent rewind operations.
  /// Limits parallel replay to prevent overwhelming the database during large catch-ups.
  /// Default: 3.
  /// </summary>
  public int MaxConcurrentRewinds { get; set; } = 3;
}

/// <summary>
/// Controls startup rewind behavior.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#startup-modes</docs>
public enum RewindStartupMode {
  /// <summary>
  /// Rewinds complete before the worker starts polling for new work.
  /// Guarantees projections are repaired before serving reads.
  /// </summary>
  Blocking = 0,

  /// <summary>
  /// Worker starts polling immediately. Rewinds run concurrently in the background.
  /// Faster startup but projections may be stale briefly.
  /// </summary>
  Background = 1
}
