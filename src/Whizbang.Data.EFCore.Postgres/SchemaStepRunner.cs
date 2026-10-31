// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>How one step of the schema pass ended.</summary>
/// <docs>operations/infrastructure/migrations</docs>
public enum SchemaStepOutcome {
  /// <summary>The step ran and committed.</summary>
  Applied,

  /// <summary>The step's ledger said, under the lock, that it was already done, so nothing ran.</summary>
  AlreadyCurrent,

  /// <summary>Another instance brought the whole schema up to date while this one waited, so nothing ran.</summary>
  CompletedElsewhere,
}

/// <summary>
/// Runs one step of the schema pass in a transaction of its own: the schema lock is taken at its start, the step's
/// ledger is re-checked under it, and the step's work and its ledger row commit together or not at all.
/// </summary>
/// <remarks>
/// <para>
/// The pass used to be one transaction, so a failure in any step rolled back every step before it, ledger rows
/// included, and each retry started from nothing while holding every lock it took until the final commit. Now a
/// failure costs only the step that failed. Its failure record is written after the rollback, on the same connection
/// with no transaction open, so the record is not lost with the work.
/// </para>
/// <para>
/// The lock is <c>pg_try_advisory_xact_lock</c> on the schema key, the same key and the same transaction scope as
/// before, so it is released by the step's commit or rollback on whatever backend the step ran on and cannot leak
/// through a pooler. Losing it means another instance is inside a step: this one gives its backend up and waits
/// through <see cref="SchemaMigrationDeferral"/>, exactly as a pass that lost the old single lock did. The wait ends
/// one of two ways. The schema is current: the other instance finished the whole pass, so this one stops, and every
/// later step it is handed does nothing. The lock is free with work still outstanding: the holder failed, died, or is
/// simply between two of its steps; this instance takes the step after a short backoff, and the re-check under the
/// lock is what keeps it from applying anything the other instance already committed. Two instances can therefore
/// take turns through one pass, and each step is still applied exactly once.
/// </para>
/// <para>
/// Once a pass has begun (<see cref="BeginPass"/>) every step logs one fixed form, counted against the whole pass:
/// <c>Migration X of Y - Started</c>, then <c>Succeeded</c>, <c>Skipped</c> or <c>Failed</c>, and the pass ends with
/// <c>Migration complete</c> or <c>Migration stopped at</c>.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaStepRunnerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationStepTransactionTests.cs</tests>
public sealed class SchemaStepRunner {
  private readonly DbContext _dbContext;
  private readonly long _lockId;
  private readonly string _schema;
  private readonly Func<CancellationToken, Task<bool>> _isSchemaCurrentAsync;
  private readonly IServiceProvider? _services;
  private readonly ILogger _logger;
  private readonly TimeProvider _timeProvider;

  private int _total;
  private int _position;
  private int _applied;
  private int _skipped;
  private long _passStarted;

  /// <summary>Creates a runner for one schema pass.</summary>
  /// <param name="dbContext">The context being initialized; its connection must be open.</param>
  /// <param name="lockId">The schema initialization lock key.</param>
  /// <param name="schema">The schema being migrated, unquoted, for reporting.</param>
  /// <param name="isSchemaCurrentAsync">
  /// Whether nothing is left to apply to the schema, asked without a lock while another instance holds it.
  /// </param>
  /// <param name="services">Where the <see cref="ISchemaInitializationObserver"/>s are registered; may be null.</param>
  /// <param name="logger">Where the step lines go; may be null.</param>
  /// <param name="timeProvider">The clock waits and durations are measured on; the system clock when null.</param>
  public SchemaStepRunner(
      DbContext dbContext,
      long lockId,
      string schema,
      Func<CancellationToken, Task<bool>> isSchemaCurrentAsync,
      IServiceProvider? services,
      ILogger? logger,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(schema);
    ArgumentNullException.ThrowIfNull(isSchemaCurrentAsync);
    _dbContext = dbContext;
    _lockId = lockId;
    _schema = schema;
    _isSchemaCurrentAsync = isSchemaCurrentAsync;
    _services = services;
    _logger = logger ?? NullLogger.Instance;
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  /// <summary>
  /// How long the initializer will wait before retrying after a failure, or null when it will not retry; a failed
  /// step's line says so. Never retried when not set.
  /// </summary>
  public Func<Exception, TimeSpan?>? RetryDelayFor { get; init; }

  /// <summary>How many deadlocks one step is retried in place for before it fails like any other step.</summary>
  internal int MaxDeadlockRetries { get; init; } = MigrationDeadlockRetry.MAX_RETRIES;

  /// <summary>
  /// Whether another instance brought the schema up to date while this one waited for the lock. Once true, every
  /// step this runner is handed does nothing.
  /// </summary>
  public bool CompletedElsewhere { get; private set; }

  /// <summary>
  /// Starts counting the pass's steps: from here each step logs its line as one of <paramref name="totalSteps"/>.
  /// </summary>
  /// <param name="totalSteps">Every step of the pass, including those it will skip.</param>
  public void BeginPass(int totalSteps) {
    _total = totalSteps;
    _position = 0;
    _applied = 0;
    _skipped = 0;
    _passStarted = _timeProvider.GetTimestamp();
  }

  /// <summary>Counts a step the plan already found current, so it never takes the lock.</summary>
  /// <param name="step">The step's name.</param>
  public void Skip(string step) {
    _position++;
    _skipped++;
    SchemaInitializationLog.StepSkipped(_logger, _position, _total, step);
  }

  /// <summary>Logs the summary of a pass in which every step succeeded.</summary>
  public void CompletePass() {
    var elapsed = _timeProvider.GetElapsedTime(_passStarted);
    SchemaInitializationLog.PassComplete(_logger, _total, _total, _applied, _skipped, new StepDuration(elapsed));
  }

  /// <summary>A failure as the ledger and the step lines give it: the SQLSTATE, or the exception's type, and its message.</summary>
  /// <param name="exception">The failure.</param>
  /// <returns>For example <c>40P01 deadlock detected</c>.</returns>
  public static string FailureText(Exception exception) {
    ArgumentNullException.ThrowIfNull(exception);
    for (var inner = exception; inner is not null; inner = inner.InnerException) {
      if (inner is PostgresException postgres) {
        return $"{postgres.SqlState} {postgres.MessageText}";
      }
    }
    return $"{exception.GetType().Name} {exception.Message}";
  }

  /// <summary>Runs one step in a transaction of its own, under the schema lock.</summary>
  /// <param name="step">The step's name: a migration's file name, a perspective's ledger key, or the step's own.</param>
  /// <param name="isCurrentAsync">Whether the step is already done, asked under the lock in the step's transaction.</param>
  /// <param name="applyAsync">The step's work, including its ledger row, in the step's transaction.</param>
  /// <param name="recordFailureAsync">
  /// Writes the step's failure once its transaction has rolled back, with no transaction open; null records nothing.
  /// </param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How the step ended.</returns>
  public async Task<SchemaStepOutcome> RunAsync(
      string step,
      Func<CancellationToken, Task<bool>> isCurrentAsync,
      Func<CancellationToken, Task> applyAsync,
      Func<Exception, CancellationToken, Task>? recordFailureAsync,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(step);
    ArgumentNullException.ThrowIfNull(isCurrentAsync);
    ArgumentNullException.ThrowIfNull(applyAsync);

    if (CompletedElsewhere) {
      return SchemaStepOutcome.CompletedElsewhere;
    }

    var counted = _total > 0;
    if (counted) {
      _position++;
    }

    var work = new StepWork(step, counted, isCurrentAsync, applyAsync, recordFailureAsync);
    var connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
    var attempt = 0;
    var deadlocks = 0;
    while (true) {
      attempt++;
      cancellationToken.ThrowIfCancellationRequested();
      var (outcome, deadlock) = await _attemptAsync(connection, work, deadlocks, cancellationToken).ConfigureAwait(false);
      if (deadlock is not null) {
        // #1636: the server chose this step's transaction as a deadlock's victim and rolled it back. The step runs
        // again, after a short back-off, rather than failing the attempt.
        deadlocks++;
        await _waitOutDeadlockAsync(step, counted, deadlock, deadlocks, cancellationToken).ConfigureAwait(false);
        continue;
      }
      if (outcome is { } done) {
        await SchemaInitializationObservers.StepCommittedAsync(_services, _schema, step, cancellationToken).ConfigureAwait(false);
        return done;
      }

      // Another instance is inside a step. Wait for its result rather than queue behind it.
      await SchemaInitializationObservers.LockContendedAsync(_services, _schema, cancellationToken).ConfigureAwait(false);
      var deferral = await SchemaMigrationDeferral.DeferAsync(
        _isSchemaCurrentAsync,
        ct => AdvisoryLockProbe.IsHeldElsewhereAsync(connection, _lockId, ct),
        _timeProvider, _schema, _logger, cancellationToken).ConfigureAwait(false);
      if (deferral == SchemaDeferralOutcome.AnotherInstanceMigrated) {
        CompletedElsewhere = true;
        SchemaInitializationLog.SchemaInitializedByOtherInstance(_logger, _schema);
        return SchemaStepOutcome.CompletedElsewhere;
      }

      // Free with work outstanding: the holder failed, died, or is between two of its steps. Backing off first keeps a
      // fleet that all saw the same release from converging on the same instant.
      var delay = LockRetryDelay(attempt, Random.Shared);
      SchemaInitializationLog.AdvisoryLockRetry(_logger, _lockId, attempt, (int)delay.TotalMilliseconds);
      await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// The wait before contending again after attempt <paramref name="attempt"/>: 100 ms doubling to a 20 s cap, plus up
  /// to half again of jitter.
  /// </summary>
  internal static TimeSpan LockRetryDelay(int attempt, Random random) {
    var delay = Math.Min(100 * (1 << Math.Min(attempt - 1, 20)), 20_000);
#pragma warning disable CA5394, S2245 // Jitter spreads a fleet's retries; it protects nothing.
    return TimeSpan.FromMilliseconds(delay + random.Next(0, delay / 2));
#pragma warning restore CA5394, S2245
  }

  /// <summary>
  /// The wait before retrying a step after its <paramref name="deadlock"/>th deadlock: 100 ms doubling to a 2 s cap,
  /// plus up to half again of jitter.
  /// </summary>
  internal static TimeSpan DeadlockRetryDelay(int deadlock, Random random) => MigrationDeadlockRetry.Delay(deadlock, random);

  /// <summary>Says a step lost a deadlock and will run again, at Warning, then waits out the back-off.</summary>
  private async Task _waitOutDeadlockAsync(
      string step, bool counted, PostgresException deadlock, int deadlocks, CancellationToken cancellationToken) {
    var delay = DeadlockRetryDelay(deadlocks, Random.Shared);
    if (counted) {
      SchemaInitializationLog.StepDeadlocked(_logger, _position, _total, step, deadlock.SqlState, deadlock.MessageText,
        new StepDuration(delay), deadlocks, MaxDeadlockRetries);
    } else {
      SchemaInitializationLog.UncountedStepDeadlocked(_logger, step, deadlock.SqlState, deadlock.MessageText,
        new StepDuration(delay), deadlocks, MaxDeadlockRetries);
    }
    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// One attempt: its outcome, null when the lock was another instance's so nothing ran; or the deadlock it lost and
  /// will be retried for, with nothing reported or recorded.
  /// </summary>
  private async Task<(SchemaStepOutcome? Outcome, PostgresException? Deadlock)> _attemptAsync(
      NpgsqlConnection connection, StepWork work, int deadlocks, CancellationToken cancellationToken) {
    var (step, counted, isCurrentAsync, applyAsync, recordFailureAsync) = work;
    // A raw transaction rather than BeginTransactionAsync on the context: the retrying execution strategy refuses a
    // user-initiated transaction, and the initializer has its own retry.
    var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    SchemaStepOutcome? outcome = null;
    Exception? failure = null;
    var started = _timeProvider.GetTimestamp();
    try {
      await _dbContext.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
      if (await _tryLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false)) {
        if (counted) {
          SchemaInitializationLog.StepStarted(_logger, _position, _total, step);
        }
        outcome = SchemaStepOutcome.AlreadyCurrent;
        if (!await isCurrentAsync(cancellationToken).ConfigureAwait(false)) {
          await applyAsync(cancellationToken).ConfigureAwait(false);
          outcome = SchemaStepOutcome.Applied;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      }
    } catch (Exception ex) {
      // Captured rather than rethrown here, so the rollback, the record and the rethrow happen in that order outside
      // the handler.
      failure = ex;
    }

    // A lost lock rolls back too: nothing ran, and the pooled backend is given up while this instance waits.
    await _endAsync(transaction, step, rollback: outcome is null || failure is not null).ConfigureAwait(false);

    // Only a step that ran under the lock failed; one that never got that far has nothing to record or report.
    if (failure is not null && outcome is not null) {
      if (deadlocks < MaxDeadlockRetries && MigrationDeadlockRetry.AsDeadlock(failure) is { } deadlock) {
        return (null, deadlock);
      }
      _reportFailure(step, counted, failure);
      await _recordFailureAsync(step, recordFailureAsync, failure).ConfigureAwait(false);
    }
    _rethrowIfFailed(failure);

    if (counted && outcome is { } ended) {
      _tally(step, ended, started);
    }
    return (outcome, null);
  }

  /// <summary>Counts a counted step that ended under the lock as applied or skipped, and says so.</summary>
  private void _tally(string step, SchemaStepOutcome ended, long started) {
    if (ended == SchemaStepOutcome.Applied) {
      _applied++;
      var elapsed = _timeProvider.GetElapsedTime(started);
      SchemaInitializationLog.StepSucceeded(_logger, _position, _total, step, new StepDuration(elapsed));
    } else {
      _skipped++;
      SchemaInitializationLog.StepSkipped(_logger, _position, _total, step);
    }
  }

  /// <summary>
  /// One step's name, whether it is counted in the progress lines, and its callbacks, as an attempt runs them.
  /// </summary>
  private readonly record struct StepWork(
      string Step,
      bool Counted,
      Func<CancellationToken, Task<bool>> IsCurrentAsync,
      Func<CancellationToken, Task> ApplyAsync,
      Func<Exception, CancellationToken, Task>? RecordFailureAsync);

  /// <summary>Rethrows a captured failure with its original stack, once it has been rolled back and recorded.</summary>
  private static void _rethrowIfFailed(Exception? failure) {
    if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
  }

  private async Task<bool> _tryLockAsync(NpgsqlConnection connection, DbTransaction transaction, CancellationToken ct) {
    await using var command = connection.CreateCommand();
    command.Transaction = (NpgsqlTransaction)transaction;
    command.CommandText = "SELECT pg_try_advisory_xact_lock($1)";
    command.Parameters.Add(new NpgsqlParameter { Value = _lockId });
    return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
  }

  /// <summary>Detaches the step's transaction from the context and ends it.</summary>
  /// <remarks>
  /// A connection lost under the step ends its transaction with it, and asking that transaction for its connection
  /// then throws <see cref="ObjectDisposedException"/>. Asked outside the handler, that exception replaced the lost
  /// connection as the step's failure, and since it is not transient the initializer gave up on a start it would have
  /// retried (#1915). It is asked inside the handler, so it is logged as a rollback that could not run.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaStepRunnerTests.cs:AConnectionLostWhileTheStepRuns_RaisesTheLostConnection_NotTheEndedTransactionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaStepRunnerTests.cs:ARollbackThatCannotRun_IsLogged_AndTheStepsOwnFailurePropagatesAsync</tests>
  private async Task _endAsync(DbTransaction transaction, string step, bool rollback) {
    await _dbContext.Database.UseTransactionAsync(null, CancellationToken.None).ConfigureAwait(false);
    if (rollback) {
      try {
        // A transaction whose commit was attempted is over either way; rolling it back again would only report that.
        if (transaction.Connection is not null) {
          await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
      } catch (Exception ex) {
        // Over a dead connection the server already rolled it back; the step's own failure is what is raised.
        SchemaInitializationLog.StepRollbackFailed(_logger, ex, _schema, step);
      }
    }
    await transaction.DisposeAsync().ConfigureAwait(false);
  }

  private void _reportFailure(string step, bool counted, Exception failure) {
    if (!counted || failure is OperationCanceledException) {
      return;
    }
    var text = FailureText(failure);
    var space = text.IndexOf(' ', StringComparison.Ordinal);
    var (state, message) = (text[..space], text[(space + 1)..]);
    if (RetryDelayFor?.Invoke(failure) is { } delay) {
      var at = (_timeProvider.GetUtcNow() + delay).ToString("O", CultureInfo.InvariantCulture);
      SchemaInitializationLog.StepFailedWillRetry(_logger, failure, _position, _total, step, state, message, new StepDuration(delay), at);
    } else {
      SchemaInitializationLog.StepFailed(_logger, failure, _position, _total, step, state, message);
    }
    SchemaInitializationLog.PassStopped(_logger, _position, _total, step, text);
  }

  private async Task _recordFailureAsync(
      string step, Func<Exception, CancellationToken, Task>? recordFailureAsync, Exception failure) {
    // A canceled start is not a failed step.
    if (recordFailureAsync is null || failure is OperationCanceledException) {
      return;
    }
    try {
      await recordFailureAsync(failure, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) {
      SchemaInitializationLog.StepFailureNotRecorded(_logger, ex, _schema, step);
    }
  }

}
