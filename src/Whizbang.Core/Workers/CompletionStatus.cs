namespace Whizbang.Core.Workers;

/// <summary>
/// Tracks completion status for acknowledgement-before-clear pattern.
/// Items transition: Pending → Sent → Acknowledged → (cleared)
/// </summary>
public enum CompletionStatus {
  /// <summary>In memory, not yet sent to ProcessWorkBatchAsync</summary>
  Pending = 0,

  /// <summary>Sent to ProcessWorkBatchAsync, awaiting acknowledgement</summary>
  Sent = 1,

  /// <summary>Confirmed by ProcessWorkBatchAsync, ready to clear</summary>
  Acknowledged = 2
}
