namespace Whizbang.Core.Resilience;

/// <summary>
/// Configuration options for <see cref="StreamRateLimiter"/>.
/// Controls per-stream event rate limits, cooldown duration, and memory cleanup.
/// </summary>
/// <docs>resilience/stream-rate-limiter</docs>
public class StreamRateLimiterOptions {
  /// <summary>
  /// Maximum events allowed per stream within the window before throttling.
  /// Default: 50
  /// </summary>
  public int MaxEventsPerWindow { get; set; } = 50;

  /// <summary>
  /// Duration of the sliding window for counting events.
  /// Default: 1 minute
  /// </summary>
  public TimeSpan WindowDuration { get; set; } = TimeSpan.FromMinutes(1);

  /// <summary>
  /// How long to pause a throttled stream before allowing it to resume.
  /// Set to TimeSpan.Zero for immediate resume (no cooldown).
  /// Default: 30 seconds
  /// </summary>
  public TimeSpan CooldownDuration { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long before a stream's tracking entry is considered stale and eligible for cleanup.
  /// Default: 5 minutes
  /// </summary>
  public TimeSpan StaleEntryTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
