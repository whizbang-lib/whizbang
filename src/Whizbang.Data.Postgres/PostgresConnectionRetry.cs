using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Handles PostgreSQL connection establishment with retry and exponential backoff.
/// Also supports waiting for schema to be fully initialized before returning success.
/// </summary>
/// <docs>data/postgres#connection-retry</docs>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresConnectionRetryTests.cs</tests>
public sealed partial class PostgresConnectionRetry {
  private readonly PostgresOptions _options;
  private readonly ILogger? _logger;

  /// <summary>
  /// Creates a new connection retry handler.
  /// </summary>
  /// <param name="options">PostgreSQL options containing retry configuration.</param>
  /// <param name="logger">Optional logger for retry attempts.</param>
  public PostgresConnectionRetry(PostgresOptions options, ILogger? logger = null) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
    _logger = logger;
  }

  [LoggerMessage(Level = LogLevel.Debug, Message = "Attempting PostgreSQL connection (attempt {Attempt})")]
  private static partial void LogConnectionAttempt(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL connection established after {Attempt} attempts")]
  private static partial void LogConnectionEstablished(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Error, Message = "Failed to connect to PostgreSQL after {MaxAttempts} initial attempts. Giving up.")]
  private static partial void LogConnectionFailed(ILogger logger, Exception exception, int maxAttempts);

  [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL connection attempt {Attempt} failed. Retrying in {DelayMs}ms...")]
  private static partial void LogRetrying(ILogger logger, Exception exception, int attempt, double delayMs);

  [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL connection still failing after {Attempt} attempts. Continuing to retry every {DelayMs}ms...")]
  private static partial void LogStillRetrying(ILogger logger, int attempt, double delayMs);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting for PostgreSQL schema to be ready (attempt {Attempt})")]
  private static partial void LogSchemaWaitAttempt(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL schema ready after {Attempt} attempts")]
  private static partial void LogSchemaReady(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL schema not ready after attempt {Attempt}. Retrying in {DelayMs}ms...")]
  private static partial void LogSchemaNotReady(ILogger logger, int attempt, double delayMs);

  /// <summary>
  /// Tests a PostgreSQL connection with retry and exponential backoff.
  /// If RetryIndefinitely is true (default), retries forever until success or cancellation.
  /// </summary>
  /// <param name="connectionString">The PostgreSQL connection string.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="NpgsqlException">Thrown when RetryIndefinitely is false and all initial attempts are exhausted.</exception>
  public async Task WaitForConnectionAsync(
      string connectionString,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrEmpty(connectionString);

    var currentDelay = _options.InitialRetryDelay;
    var attempt = 0;

    while (true) {
      attempt++;
      cancellationToken.ThrowIfCancellationRequested();

      try {
        _logRetryAttempt(attempt, isSchema: false);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (attempt > 1 && _logger is not null) {
          LogConnectionEstablished(_logger, attempt);
        }

        return;
      } catch (Exception ex) when (_isTransientException(ex)) {
        if (_shouldRethrowAfterRetry(ex, attempt, currentDelay)) {
          throw;
        }
        await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
        currentDelay = CalculateNextDelay(currentDelay);
      }
    }
  }

  /// <summary>
  /// Waits for the PostgreSQL schema to be fully initialized (tables and functions exist).
  /// Uses the same retry logic as connection retry.
  /// </summary>
  /// <param name="connectionString">The PostgreSQL connection string.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task WaitForSchemaReadyAsync(
      string connectionString,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrEmpty(connectionString);

    var currentDelay = _options.InitialRetryDelay;
    var attempt = 0;

    while (true) {
      attempt++;
      cancellationToken.ThrowIfCancellationRequested();

      try {
        _logRetryAttempt(attempt, isSchema: true);

        if (await _isSchemaReadyAsync(connectionString, cancellationToken).ConfigureAwait(false)) {
          if (attempt > 1 && _logger is not null) {
            LogSchemaReady(_logger, attempt);
          }
          return;
        }

        // Schema not ready yet
        if (_logger is not null) {
          LogSchemaNotReady(_logger, attempt, currentDelay.TotalMilliseconds);
        }

        await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
        currentDelay = CalculateNextDelay(currentDelay);
      } catch (Exception ex) when (_isTransientException(ex)) {
        if (_logger is not null) {
          LogRetrying(_logger, ex, attempt, currentDelay.TotalMilliseconds);
        }

        await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
        currentDelay = CalculateNextDelay(currentDelay);
      }
    }
  }

  /// <summary>
  /// Waits for both connection and schema to be ready.
  /// This is the recommended method for startup.
  /// </summary>
  /// <param name="connectionString">The PostgreSQL connection string.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task WaitForDatabaseReadyAsync(
      string connectionString,
      CancellationToken cancellationToken = default) {
    // First wait for connection
    await WaitForConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false);

    // Then wait for schema
    await WaitForSchemaReadyAsync(connectionString, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Handles retry logic for connection attempts: logs appropriately and returns true if the caller should rethrow.
  /// </summary>
  private bool _shouldRethrowAfterRetry(Exception ex, int attempt, TimeSpan currentDelay) {
    if (_isWithinInitialRetryPhase(attempt)) {
      _logRetryWarning(ex, attempt, currentDelay);
      return false;
    }

    if (!_options.RetryIndefinitely) {
      _logConnectionExhausted(ex);
      return true;
    }

    _logPeriodicRetryStatus(attempt, currentDelay);
    return false;
  }

  /// <summary>
  /// Returns true if the attempt is within the initial retry phase.
  /// </summary>
  private bool _isWithinInitialRetryPhase(int attempt) =>
    attempt <= _options.InitialRetryAttempts;

  /// <summary>
  /// Logs a warning for a transient connection failure during initial retry phase.
  /// </summary>
  private void _logRetryWarning(Exception ex, int attempt, TimeSpan currentDelay) {
    if (_logger is not null) {
      LogRetrying(_logger, ex, attempt, currentDelay.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Logs that all initial retry attempts have been exhausted.
  /// </summary>
  private void _logConnectionExhausted(Exception ex) {
    if (_logger is not null) {
      LogConnectionFailed(_logger, ex, _options.InitialRetryAttempts);
    }
  }

  /// <summary>
  /// Logs retry status periodically (every 10 attempts) during indefinite retry.
  /// </summary>
  private void _logPeriodicRetryStatus(int attempt, TimeSpan currentDelay) {
    if (_logger is not null && attempt % 10 == 0) {
      LogStillRetrying(_logger, attempt, currentDelay.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Logs the current retry attempt for connection or schema checks.
  /// </summary>
  private void _logRetryAttempt(int attempt, bool isSchema) {
    if (_logger is not null) {
      if (isSchema) {
        LogSchemaWaitAttempt(_logger, attempt);
      } else {
        LogConnectionAttempt(_logger, attempt);
      }
    }
  }

  /// <summary>
  /// Calculates the next retry delay using exponential backoff.
  /// </summary>
  /// <param name="currentDelay">The current delay.</param>
  /// <returns>The next delay, capped at MaxRetryDelay.</returns>
  internal TimeSpan CalculateNextDelay(TimeSpan currentDelay) {
    var nextDelay = TimeSpan.FromTicks((long)(currentDelay.Ticks * _options.BackoffMultiplier));

    // Cap at max delay
    if (nextDelay > _options.MaxRetryDelay) {
      return _options.MaxRetryDelay;
    }

    return nextDelay;
  }

  /// <summary>
  /// Checks if the required schema is ready (tables and functions exist).
  /// </summary>
  private static async Task<bool> _isSchemaReadyAsync(string connectionString, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

    // Check for required tables
    const string checkTablesSql = @"
      SELECT COUNT(*)
      FROM information_schema.tables
      WHERE table_schema = 'public'
        AND table_name IN ('wh_inbox', 'wh_outbox', 'wh_event_store')";

    await using var tableCommand = new NpgsqlCommand(checkTablesSql, connection);
    var tableCount = Convert.ToInt32(await tableCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    if (tableCount < 3) {
      return false;
    }

    // Check for required function (process_work_batch is critical)
    // Functions are installed in 'public' schema (via __SCHEMA__ placeholder replacement)
    const string checkFunctionsSql = @"
      SELECT COUNT(*)
      FROM information_schema.routines
      WHERE routine_schema = 'public'
        AND routine_name = 'process_work_batch'
        AND routine_type = 'FUNCTION'";

    await using var functionCommand = new NpgsqlCommand(checkFunctionsSql, connection);
    var functionCount = Convert.ToInt32(await functionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    return functionCount >= 1;
  }

  /// <summary>
  /// Determines if an exception is transient and should be retried.
  /// </summary>
  private static bool _isTransientException(Exception ex) =>
    _isTransientExceptionDirect(ex) ||
    (ex.InnerException is not null && _isTransientException(ex.InnerException));

  /// <summary>
  /// Checks if the exception itself (without inner exceptions) is a transient type.
  /// </summary>
  private static bool _isTransientExceptionDirect(Exception ex) => ex switch {
    NpgsqlException npgsqlEx => _isTransientNpgsqlException(npgsqlEx),
    System.Net.Sockets.SocketException => true,
    System.IO.IOException => true,
    _ => false,
  };

  /// <summary>
  /// Checks if an NpgsqlException represents a transient connection-related error.
  /// </summary>
  private static bool _isTransientNpgsqlException(NpgsqlException npgsqlEx) =>
    npgsqlEx.IsTransient ||
    npgsqlEx.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
    npgsqlEx.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
    npgsqlEx.Message.Contains("refused", StringComparison.OrdinalIgnoreCase);
}
