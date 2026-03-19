namespace Whizbang.Core.Perspectives;

/// <summary>
/// Manages stream-level locks for perspective rewind, bootstrap, and purge operations.
/// Prevents concurrent event application while destructive or rebuilding operations are in progress.
/// New events queue up in wh_perspective_events and are processed after the lock is released.
/// </summary>
/// <docs>perspectives/stream-locking</docs>
public interface IPerspectiveStreamLocker {
  /// <summary>
  /// Attempts to acquire a stream lock for the given perspective.
  /// Returns true if the lock was acquired, false if another active instance holds the lock.
  /// Acquires succeed if: unlocked, expired, or already held by the same instance (idempotent).
  /// </summary>
  /// <param name="streamId">Stream to lock</param>
  /// <param name="perspectiveName">Perspective name to lock</param>
  /// <param name="instanceId">Instance requesting the lock</param>
  /// <param name="reason">Lock reason for observability (e.g., "rewind", "bootstrap", "purge")</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>True if lock was acquired, false if held by another active instance</returns>
  Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default);

  /// <summary>
  /// Extends the lock expiry for a stream/perspective held by the given instance.
  /// No-op if the lock is not held by this instance.
  /// </summary>
  /// <param name="streamId">Stream whose lock to renew</param>
  /// <param name="perspectiveName">Perspective whose lock to renew</param>
  /// <param name="instanceId">Instance that holds the lock</param>
  /// <param name="ct">Cancellation token</param>
  Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default);

  /// <summary>
  /// Releases the stream lock for the given perspective.
  /// Only releases if the lock is held by the specified instance.
  /// </summary>
  /// <param name="streamId">Stream to unlock</param>
  /// <param name="perspectiveName">Perspective to unlock</param>
  /// <param name="instanceId">Instance releasing the lock</param>
  /// <param name="ct">Cancellation token</param>
  Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default);
}
