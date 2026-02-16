namespace Whizbang.Data.Postgres;

/// <summary>
/// Configuration options for PostgreSQL connections.
/// </summary>
/// <docs>components/data/postgres</docs>
public class PostgresOptions {
  #region Connection Retry Options

  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying indefinitely but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// Default: 5
  /// </summary>
  /// <docs>components/data/postgres#connection-retry</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// Default: 1 second
  /// </summary>
  /// <docs>components/data/postgres#connection-retry</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// Default: 120 seconds
  /// </summary>
  /// <docs>components/data/postgres#connection-retry</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at MaxRetryDelay).
  /// Default: 2.0
  /// </summary>
  /// <docs>components/data/postgres#connection-retry</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// If true, retry indefinitely until connection succeeds or cancellation is requested.
  /// If false, throw after InitialRetryAttempts.
  /// Default: true (critical infrastructure - always retry)
  /// </summary>
  /// <docs>components/data/postgres#connection-retry</docs>
  public bool RetryIndefinitely { get; set; } = true;

  #endregion
}
