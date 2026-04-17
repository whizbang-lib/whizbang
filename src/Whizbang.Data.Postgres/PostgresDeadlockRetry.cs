using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Retries database operations that fail due to PostgreSQL deadlock (40P01).
/// Deadlocks are transient — the losing transaction is rolled back by PostgreSQL,
/// and retrying immediately (with a short jittered delay) succeeds in nearly all cases.
/// </summary>
/// <docs>operations/infrastructure/deadlock-retry</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresDeadlockRetryTests.cs</tests>
public static partial class PostgresDeadlockRetry {
  private const string DEADLOCK_SQL_STATE = "40P01";

  [ThreadStatic]
  private static Random? _tRandom;

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "PostgreSQL deadlock detected (40P01), retrying attempt {Attempt}/{MaxAttempts} after {DelayMs}ms")]
  private static partial void LogDeadlockRetry(ILogger logger, int attempt, int maxAttempts, int delayMs);

  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "PostgreSQL deadlock detected (40P01), exhausted {MaxAttempts} retry attempts")]
  private static partial void LogDeadlockExhausted(ILogger logger, Exception exception, int maxAttempts);

  /// <summary>
  /// Executes an async action with deadlock retry.
  /// </summary>
  /// <param name="action">The database operation to execute.</param>
  /// <param name="maxAttempts">Maximum number of attempts (default: 3).</param>
  /// <param name="logger">Optional logger for retry warnings.</param>
  /// <param name="cancellationToken">Cancellation token honored between retries.</param>
  public static async Task ExecuteAsync(
    Func<Task> action,
    int maxAttempts = 3,
    ILogger? logger = null,
    CancellationToken cancellationToken = default) {
    for (var attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        await action();
        return;
      } catch (PostgresException ex) when (ex.SqlState == DEADLOCK_SQL_STATE && attempt < maxAttempts) {
        var delayMs = _computeJitteredDelay(attempt);
        if (logger is not null) {
          LogDeadlockRetry(logger, attempt, maxAttempts, delayMs);
        }
        await Task.Delay(delayMs, cancellationToken);
      } catch (PostgresException ex) when (ex.SqlState == DEADLOCK_SQL_STATE && attempt == maxAttempts) {
        if (logger is not null) {
          LogDeadlockExhausted(logger, ex, maxAttempts);
        }
        throw;
      }
    }
  }

  /// <summary>
  /// Executes an async function with deadlock retry and returns the result.
  /// </summary>
  public static async Task<T> ExecuteAsync<T>(
    Func<Task<T>> action,
    int maxAttempts = 3,
    ILogger? logger = null,
    CancellationToken cancellationToken = default) {
    for (var attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        return await action();
      } catch (PostgresException ex) when (ex.SqlState == DEADLOCK_SQL_STATE && attempt < maxAttempts) {
        var delayMs = _computeJitteredDelay(attempt);
        if (logger is not null) {
          LogDeadlockRetry(logger, attempt, maxAttempts, delayMs);
        }
        await Task.Delay(delayMs, cancellationToken);
      } catch (PostgresException ex) when (ex.SqlState == DEADLOCK_SQL_STATE && attempt == maxAttempts) {
        if (logger is not null) {
          LogDeadlockExhausted(logger, ex, maxAttempts);
        }
        throw;
      }
    }
    throw new InvalidOperationException("Unreachable");
  }

  /// <summary>
  /// Computes a jittered delay: base * 2^(attempt-1) with ±25% jitter.
  /// Attempt 1 → ~50ms, Attempt 2 → ~100ms, Attempt 3 → ~200ms.
  /// </summary>
  private static int _computeJitteredDelay(int attempt) {
    _tRandom ??= new Random();
    var baseMs = 50 * (1 << (attempt - 1)); // 50, 100, 200, ...
    var jitter = (int)(baseMs * 0.25);
    return baseMs + _tRandom.Next(-jitter, jitter + 1);
  }
}
