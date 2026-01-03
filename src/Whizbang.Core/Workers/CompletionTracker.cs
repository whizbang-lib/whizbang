using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// Thread-safe tracking of completions awaiting acknowledgement.
/// Uses ConcurrentDictionary for efficient removal operations.
/// Implements exponential backoff retry for stale items.
/// </summary>
/// <typeparam name="T">Type of completion being tracked</typeparam>
public sealed class CompletionTracker<T> where T : notnull {
  private readonly ConcurrentDictionary<Guid, TrackedCompletion<T>> _items = new();
  private readonly TimeSpan _baseTimeout;
  private readonly double _backoffMultiplier;
  private readonly TimeSpan _maxTimeout;

  /// <summary>
  /// Create a new completion tracker with configurable retry behavior.
  /// </summary>
  /// <param name="baseTimeout">Initial retry timeout (default: 5 minutes)</param>
  /// <param name="backoffMultiplier">Exponential backoff multiplier (default: 2.0)</param>
  /// <param name="maxTimeout">Maximum retry timeout (default: 60 minutes)</param>
  public CompletionTracker(
    TimeSpan? baseTimeout = null,
    double backoffMultiplier = 2.0,
    TimeSpan? maxTimeout = null
  ) {
    _baseTimeout = baseTimeout ?? TimeSpan.FromMinutes(5);
    _backoffMultiplier = backoffMultiplier;
    _maxTimeout = maxTimeout ?? TimeSpan.FromMinutes(60);
  }

  /// <summary>
  /// Add a new completion to track.
  /// Status is initialized to Pending.
  /// </summary>
  public void Add(T completion) {
    var tracked = new TrackedCompletion<T> { Completion = completion };
    _items.TryAdd(tracked.TrackingId, tracked);
  }

  /// <summary>
  /// Get all pending completions (status = Pending, not yet sent).
  /// Returns items ordered by SentAt timestamp.
  /// </summary>
  public TrackedCompletion<T>[] GetPending() {
    return _items.Values
      .Where(tc => tc.Status == CompletionStatus.Pending)
      .OrderBy(tc => tc.SentAt)
      .ToArray();
  }

  /// <summary>
  /// Mark items as sent to ProcessWorkBatchAsync.
  /// Updates status to Sent and records sent timestamp.
  /// </summary>
  /// <param name="items">Items to mark as sent</param>
  /// <param name="sentAt">Timestamp when items were sent</param>
  public void MarkAsSent(TrackedCompletion<T>[] items, DateTimeOffset sentAt) {
    foreach (var item in items) {
      item.Status = CompletionStatus.Sent;
      item.SentAt = sentAt;
    }
  }

  /// <summary>
  /// Mark oldest N 'Sent' items as Acknowledged.
  /// This is called after receiving acknowledgement count from ProcessWorkBatchAsync.
  /// </summary>
  /// <param name="count">Number of items to acknowledge</param>
  public void MarkAsAcknowledged(int count) {
    var sentItems = _items.Values
      .Where(tc => tc.Status == CompletionStatus.Sent)
      .OrderBy(tc => tc.SentAt)
      .Take(count)
      .ToArray();

    foreach (var item in sentItems) {
      item.Status = CompletionStatus.Acknowledged;
    }
  }

  /// <summary>
  /// Remove all acknowledged items from tracking.
  /// Uses ConcurrentDictionary.TryRemove for efficient O(1) removal.
  /// </summary>
  public void ClearAcknowledged() {
    var toRemove = _items.Values
      .Where(tc => tc.Status == CompletionStatus.Acknowledged)
      .Select(tc => tc.TrackingId)
      .ToList();

    foreach (var id in toRemove) {
      _items.TryRemove(id, out _);
    }
  }

  /// <summary>
  /// Reset items that have been 'Sent' for too long back to 'Pending'.
  /// Uses exponential backoff: timeout = baseTimeout * (backoffMultiplier ^ retryCount).
  /// Timeout is capped at maxTimeout to prevent infinite growth.
  /// </summary>
  /// <param name="now">Current timestamp for staleness calculation</param>
  public void ResetStale(DateTimeOffset now) {
    foreach (var item in _items.Values.Where(tc => tc.Status == CompletionStatus.Sent)) {
      var timeout = CalculateTimeout(item.RetryCount);
      if (now - item.SentAt > timeout) {
        item.Status = CompletionStatus.Pending;
        item.RetryCount++;
      }
    }
  }

  /// <summary>
  /// Calculate retry timeout using exponential backoff.
  /// Formula: baseTimeout * (backoffMultiplier ^ retryCount), capped at maxTimeout.
  /// Example with defaults: 5min → 10min → 20min → 40min → 60min (max)
  /// </summary>
  private TimeSpan CalculateTimeout(int retryCount) {
    var timeout = TimeSpan.FromMinutes(
      _baseTimeout.TotalMinutes * Math.Pow(_backoffMultiplier, retryCount)
    );
    return timeout > _maxTimeout ? _maxTimeout : timeout;
  }

  /// <summary>
  /// Get count of items in Pending status.
  /// </summary>
  public int PendingCount => _items.Values.Count(tc => tc.Status == CompletionStatus.Pending);

  /// <summary>
  /// Get count of items in Sent status (awaiting acknowledgement).
  /// </summary>
  public int SentCount => _items.Values.Count(tc => tc.Status == CompletionStatus.Sent);

  /// <summary>
  /// Get count of items in Acknowledged status (ready to clear).
  /// </summary>
  public int AcknowledgedCount => _items.Values.Count(tc => tc.Status == CompletionStatus.Acknowledged);
}
