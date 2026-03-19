namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// The outcome of a perspective synchronization wait operation.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
public enum SyncOutcome {
  /// <summary>
  /// All matching events were processed successfully.
  /// </summary>
  Synced,

  /// <summary>
  /// The timeout was reached before all events were processed.
  /// </summary>
  TimedOut,

  /// <summary>
  /// No events matched the filter (nothing to wait for).
  /// </summary>
  NoPendingEvents
}
