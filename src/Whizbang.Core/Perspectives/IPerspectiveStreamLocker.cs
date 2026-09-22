namespace Whizbang.Core.Perspectives;

/// <summary>
/// Manages stream-level locks for perspective rewind, bootstrap, and purge operations.
/// Prevents concurrent event application while destructive or rebuilding operations are in progress.
/// New events queue up in wh_perspective_events and are processed after the lock is released.
/// </summary>
/// <docs>fundamentals/perspectives/stream-locking</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveSnapshotAndRewindTests.cs:IPerspectiveStreamLocker_HasExpectedMethodsAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ServiceCollectionExtensions_FullOverloadRegistrationTests.cs:AddWhizbangPostgres_EntriesOverload_ResolvesRealImplementationTypesAsync</tests>
public interface IPerspectiveStreamLocker {
  /// <summary>True when a real implementation is registered. The framework's null default returns false so
  /// a consumer takes the same skip path an unregistered subsystem produced, without a null check.</summary>
  bool IsConfigured => true;

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

/// <summary>
/// The framework's null default for <see cref="IPerspectiveStreamLocker"/>: reports
/// <see cref="IPerspectiveStreamLocker.IsConfigured"/> false, so rewinds proceed unlocked as they did when no locker
/// was registered. Callers check the flag first and this implementation throws if they do not.
/// </summary>
public sealed class NullPerspectiveStreamLocker : IPerspectiveStreamLocker, INullDefault {
  private const string NOT_REGISTERED = "No perspective stream locker is registered; a storage driver supplies one. Check IsConfigured before calling.";

  private NullPerspectiveStreamLocker() { }

  /// <summary>The shared instance.</summary>
  public static NullPerspectiveStreamLocker Instance { get; } = new();

  /// <inheritdoc />
  public bool IsConfigured => false;

  /// <inheritdoc />
  public Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) =>
    throw new InvalidOperationException(NOT_REGISTERED);

  /// <inheritdoc />
  public Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) =>
    throw new InvalidOperationException(NOT_REGISTERED);

  /// <inheritdoc />
  public Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) =>
    throw new InvalidOperationException(NOT_REGISTERED);
}
