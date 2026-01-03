namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for worker completion retry behavior with exponential backoff.
/// Controls how long completions remain in 'Sent' status before being retried as 'Pending'.
/// </summary>
/// <remarks>
/// When ProcessWorkBatchAsync fails or doesn't acknowledge completions, items remain in 'Sent' status.
/// After the retry timeout, they're moved back to 'Pending' with exponential backoff:
/// - 1st retry: 5 minutes
/// - 2nd retry: 10 minutes (5 * 2^1)
/// - 3rd retry: 20 minutes (5 * 2^2)
/// - 4th retry: 40 minutes (5 * 2^3)
/// - 5th+ retry: 60 minutes (max timeout)
/// </remarks>
public class WorkerRetryOptions {
  /// <summary>
  /// Base retry timeout in minutes. Default: 5 minutes.
  /// First retry occurs after this duration.
  /// </summary>
  public int RetryTimeoutMinutes { get; set; } = 5;

  /// <summary>
  /// Enable exponential backoff for retries. Default: true.
  /// When enabled, timeout increases: 5min → 10min → 20min → 40min → 60min (max).
  /// When disabled, uses fixed RetryTimeoutMinutes for all retries.
  /// </summary>
  public bool EnableExponentialBackoff { get; set; } = true;

  /// <summary>
  /// Exponential backoff multiplier. Default: 2.0.
  /// Timeout calculation: baseTimeout * (multiplier ^ retryCount).
  /// Only used when EnableExponentialBackoff is true.
  /// </summary>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// Maximum retry timeout in minutes. Default: 60 minutes.
  /// Prevents exponential backoff from growing indefinitely.
  /// </summary>
  public int MaxBackoffMinutes { get; set; } = 60;
}
