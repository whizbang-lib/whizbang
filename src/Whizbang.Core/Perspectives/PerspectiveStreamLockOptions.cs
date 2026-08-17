namespace Whizbang.Core.Perspectives;

/// <summary>
/// Configuration options for perspective stream locking.
/// Controls lock duration and keepalive interval for rewind, bootstrap, and purge operations.
/// </summary>
/// <docs>fundamentals/perspectives/stream-locking</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveSnapshotAndRewindTests.cs:PerspectiveStreamLockOptions_Defaults_HaveExpectedValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveSnapshotAndRewindTests.cs:PerspectiveStreamLockOptions_CustomValues_ArePreservedAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperPerspectiveStreamLockerTests.cs:TryAcquireLockAsync_ExpiredLock_AcquiresSuccessfullyAsync</tests>
public class PerspectiveStreamLockOptions {
  /// <summary>
  /// How long a lock is valid before expiring. Must be longer than KeepAliveInterval.
  /// Default: 30 seconds.
  /// </summary>
  public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How often the keepalive task renews the lock. Must be less than LockTimeout / 2.
  /// Default: 10 seconds.
  /// </summary>
  public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);
}
