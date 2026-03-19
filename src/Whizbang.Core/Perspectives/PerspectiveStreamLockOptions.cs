namespace Whizbang.Core.Perspectives;

/// <summary>
/// Configuration options for perspective stream locking.
/// Controls lock duration and keepalive interval for rewind, bootstrap, and purge operations.
/// </summary>
/// <docs>fundamentals/perspectives/stream-locking</docs>
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
