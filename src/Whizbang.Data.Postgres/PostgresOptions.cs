namespace Whizbang.Data.Postgres;

/// <summary>
/// Configuration options for PostgreSQL connections.
/// </summary>
/// <docs>data/postgres</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresConnectionRetryTests.cs:PostgresOptions_DefaultValues_AreCorrectAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ServiceCollectionExtensions_FullOverloadRegistrationTests.cs:AddWhizbangPostgres_EntriesConvenienceOverload_RegistersCoreServicesWithDefaultOptionsAsync</tests>
public class PostgresOptions {
  #region Connection Retry Options

  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying indefinitely but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// Default: 5
  /// </summary>
  /// <docs>data/postgres#connection-retry</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// Default: 1 second
  /// </summary>
  /// <docs>data/postgres#connection-retry</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// Default: 120 seconds
  /// </summary>
  /// <docs>data/postgres#connection-retry</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at MaxRetryDelay).
  /// Default: 2.0
  /// </summary>
  /// <docs>data/postgres#connection-retry</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// If true, retry indefinitely until connection succeeds or cancellation is requested.
  /// If false, throw after InitialRetryAttempts.
  /// Default: true (critical infrastructure - always retry)
  /// </summary>
  /// <docs>data/postgres#connection-retry</docs>
  public bool RetryIndefinitely { get; set; } = true;

  #endregion

  #region Command Timeout Options

  /// <summary>
  /// Command timeout in seconds for database operations like process_work_batch.
  /// Controls how long a single SQL command can run before being cancelled.
  /// Default: 5 seconds
  /// </summary>
  /// <docs>data/postgres#command-timeout</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ServiceCollectionExtensions_FullOverloadRegistrationTests.cs:AddWhizbangPostgres_EntriesConvenienceOverload_RegistersCoreServicesWithDefaultOptionsAsync</tests>
  public int CommandTimeoutSeconds { get; set; } = 5;

  #endregion

  #region Concurrency Cap

  /// <summary>
  /// Defense-in-depth cap on concurrent <c>IWorkCoordinator</c> calls per process.
  /// Prevents runaway pool draw if Npgsql config drifts. When the cap is hit, callers wait
  /// on a semaphore rather than erroring. Set to 0 to disable the cap entirely.
  /// Default: 50 (matches the recommended Npgsql Maximum Pool Size).
  /// </summary>
  /// <docs>fundamentals/work-coordinator/configuration-reference#max-in-flight-commands</docs>
  public int MaxInFlightCommands { get; set; } = 50;

  #endregion

  #region Collective Apply

  /// <summary>
  /// Rows mutated per batched collective-apply UPDATE (keyset <c>LIMIT</c>). Keeps each batch a short
  /// transaction so lock hold stays brief. Default 1000.
  /// </summary>
  /// <docs>fundamentals/messaging/collective-events</docs>
  public int CollectiveApplyBatchSize { get; set; } = 1000;

  /// <summary>
  /// Server-side <c>statement_timeout</c> (seconds) applied to each collective-apply batch transaction (via
  /// the transaction-local <c>set_config('statement_timeout', …, true)</c> — the only form that survives
  /// PgBouncer transaction pooling). Null leaves the server/role default; when set, a runaway batch is
  /// cancelled by Postgres itself, so a client timeout can never leave a zombie query.
  /// </summary>
  /// <docs>fundamentals/messaging/collective-events</docs>
  public int? CollectiveApplyStatementTimeoutSeconds { get; set; }

  #endregion
}
