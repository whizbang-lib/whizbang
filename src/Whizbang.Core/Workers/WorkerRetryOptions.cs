namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for worker completion retry behavior with exponential backoff.
/// Controls how long completions remain in 'Sent' status before being retried as 'Pending'.
/// </summary>
/// <remarks>
/// <para>
/// When ProcessWorkBatchAsync fails or doesn't acknowledge completions, items remain in 'Sent' status.
/// After the retry timeout, they're moved back to 'Pending' with exponential backoff.
/// </para>
/// <para>
/// IMPORTANT: Fast retries are critical because messages in the same stream MUST process in order (by UUIDv7).
/// A single failing message blocks ALL later messages in that stream until it completes.
/// </para>
/// <para>
/// Default exponential backoff progression:
/// - 1st retry: 1 second
/// - 2nd retry: 2 seconds (1 * 2^1)
/// - 3rd retry: 4 seconds (1 * 2^2)
/// - 4th retry: 8 seconds (1 * 2^3)
/// - 5th retry: 16 seconds (1 * 2^4)
/// - 6th retry: 32 seconds (1 * 2^5)
/// - 7th+ retry: 60 seconds (max timeout)
/// </para>
/// </remarks>
public class WorkerRetryOptions {
  /// <summary>
  /// Base retry timeout in seconds. Default: 1 second.
  /// First retry occurs after this duration.
  /// </summary>
  public int RetryTimeoutSeconds { get; set; } = 1;

  /// <summary>
  /// Enable exponential backoff for retries. Default: true.
  /// When enabled, timeout increases: 1s → 2s → 4s → 8s → 16s → 32s → 60s (max).
  /// When disabled, uses fixed RetryTimeoutSeconds for all retries.
  /// </summary>
  public bool EnableExponentialBackoff { get; set; } = true;

  /// <summary>
  /// Exponential backoff multiplier. Default: 2.0.
  /// Timeout calculation: baseTimeout * (multiplier ^ retryCount).
  /// Only used when EnableExponentialBackoff is true.
  /// </summary>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// Maximum retry timeout in seconds. Default: 60 seconds (1 minute).
  /// Prevents exponential backoff from growing indefinitely.
  /// IMPORTANT: Keep this low - failing messages block entire streams!
  /// </summary>
  public int MaxBackoffSeconds { get; set; } = 60;
}
