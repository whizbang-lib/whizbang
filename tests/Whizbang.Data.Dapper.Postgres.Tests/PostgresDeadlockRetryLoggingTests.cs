using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// What <see cref="PostgresDeadlockRetry"/> reports while it retries. The existing
/// <c>PostgresDeadlockRetryTests</c> cover the retry behavior itself but never pass a logger, so
/// the diagnostics were never exercised — and a retry nobody can see is the whole problem these
/// log lines exist to solve.
/// </summary>
public class PostgresDeadlockRetryLoggingTests {

  private static PostgresException _deadlock() =>
    new("deadlock detected", "ERROR", "ERROR", "40P01");

  private static PostgresException _serializationFailure() =>
    new("could not serialize access due to concurrent update", "ERROR", "ERROR", "40001");

  [Test]
  public async Task RetriedDeadlock_ReportsTheSqlStateAttemptAndDelayAsync() {
    // A deadlock retry is invisible by design: the operation succeeds, so nothing surfaces to the
    // caller. This warning is the only trace that the database is thrashing. It has to carry the
    // SQL state, because 40P01 (deadlock) and 40001 (serialization failure) are both retried here
    // and they call for different responses -- one points at lock ordering, the other at
    // contention under a stricter isolation level. "Retried something" tells an operator nothing.
    var logger = new CapturingLogger();
    var attempts = 0;

    await PostgresDeadlockRetry.ExecuteAsync(
      () => {
        attempts++;
        return attempts == 1 ? throw _deadlock() : Task.CompletedTask;
      },
      maxAttempts: 3,
      logger: logger);

    await Assert.That(attempts).IsEqualTo(2)
      .Because("the operation must actually be retried, not merely logged about");
    var retryLine = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning);
    await Assert.That(retryLine.Message).IsNotNull()
      .Because("a silent retry leaves a thrashing database indistinguishable from a healthy one");
    await Assert.That(retryLine.Message!).Contains("40P01")
      .Because("without the SQL state an operator cannot tell a lock-ordering deadlock from a serialization failure");
    await Assert.That(retryLine.Message!).Contains("1/3")
      .Because("the attempt number is what says whether this was a blip or nearly the last chance");
  }

  [Test]
  public async Task ExhaustedRetries_ReportsAtErrorAndCarriesTheExceptionAsync() {
    // The end of the road: every attempt deadlocked and the caller is about to receive the
    // failure. This one must be Error rather than Warning, because it is the line an alert fires
    // on -- a retry that eventually succeeded is noise, a retry that ran out is a lost operation.
    // It must also carry the exception, or whoever reads the alert has a message and no stack.
    var logger = new CapturingLogger();
    var attempts = 0;

    await Assert.That(async () => await PostgresDeadlockRetry.ExecuteAsync(
        () => { attempts++; throw _deadlock(); },
        maxAttempts: 2,
        logger: logger))
      .Throws<PostgresException>()
      .Because("the original PostgresException must reach the caller intact -- upstream code "
             + "branches on SqlState, and a wrapped exception hides it");

    await Assert.That(attempts).IsEqualTo(2)
      .Because("every configured attempt must be spent before giving up");
    var exhausted = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Error);
    await Assert.That(exhausted.Message).IsNotNull()
      .Because("exhaustion is the alertable event; logging it at Warning would bury it among ordinary retries");
    await Assert.That(exhausted.Exception).IsNotNull()
      .Because("an alert with no exception attached gives whoever answers it nothing to work from");
    await Assert.That(exhausted.Message!).Contains("40P01");
  }

  [Test]
  public async Task RetriedSerializationFailure_WithAResult_ReportsAndStillReturnsTheValueAsync() {
    // The generic overload is a separate copy of the retry loop, and its logging was never
    // exercised at all. A read that retries must still return the value it eventually read --
    // a retry path that logs correctly but drops the result is the worst of both.
    var logger = new CapturingLogger();
    var attempts = 0;

    var result = await PostgresDeadlockRetry.ExecuteAsync(
      () => {
        attempts++;
        return attempts == 1 ? throw _serializationFailure() : Task.FromResult(42);
      },
      maxAttempts: 3,
      logger: logger);

    await Assert.That(result).IsEqualTo(42)
      .Because("the retried call's value is the point of this overload; logging it away would be silent data loss");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning
        && e.Message!.Contains("40001", StringComparison.Ordinal))).IsTrue()
      .Because("a serialization failure retried on the read path must be reported with its own SQL state");
  }

  [Test]
  public async Task ExhaustedRetries_WithAResult_ReportsAtErrorAndRethrowsAsync() {
    // The generic overload's exhaustion path had no coverage whatsoever -- not the log, not the
    // rethrow. A query that gives up after N deadlocks must surface the original exception rather
    // than, say, returning default(T), which would silently hand the caller an empty read.
    var logger = new CapturingLogger();
    var attempts = 0;

    await Assert.That(async () => await PostgresDeadlockRetry.ExecuteAsync<int>(
        () => { attempts++; throw _serializationFailure(); },
        maxAttempts: 2,
        logger: logger))
      .Throws<PostgresException>()
      .Because("returning a default value instead of throwing would hand the caller an empty read "
             + "that looks exactly like a legitimately empty one");

    await Assert.That(attempts).IsEqualTo(2);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsTrue()
      .Because("the read path's exhaustion is as alertable as the write path's");
  }

  [Test]
  public async Task RetryWithoutALogger_StillRetriesAsync() {
    // The logger is optional and most callers omit it. The guard around each log call must not be
    // the thing that decides whether a retry happens.
    var attempts = 0;

    await PostgresDeadlockRetry.ExecuteAsync(
      () => {
        attempts++;
        return attempts == 1 ? throw _deadlock() : Task.CompletedTask;
      },
      maxAttempts: 3);

    await Assert.That(attempts).IsEqualTo(2)
      .Because("retry behavior must be identical whether or not anyone is listening");
  }

  /// <summary>Captures level, formatted message and exception so the diagnostics can be asserted.</summary>
  private sealed class CapturingLogger : ILogger {
    private readonly List<(LogLevel Level, string? Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string? Message, Exception? Exception)> Entries {
      get { lock (_entries) { return [.. _entries]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add((logLevel, formatter(state, exception), exception)); }
    }
  }
}
