using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Applies the rewrites that convert a stored format, once per schema rather than once per instance.
/// </summary>
/// <remarks>
/// <para>
/// These run before the initializer's transaction opens, because an index over an extraction of a
/// rewritten key needs the rewrite committed and that transaction is where the indexes are built.
/// Moving them out of the transaction also moved them out of the transaction-scoped advisory lock
/// that makes one instance do the schema work, so the lock has to be taken again here, over the
/// connection that runs them.
/// </para>
/// <para>
/// Without it every replica rewrites concurrently. The statements are idempotent, so the result
/// would still be correct, but two full-table updates over the same rows in unpredictable order can
/// deadlock each other, and a fleet starting together would do the same scan many times over.
/// </para>
/// <para>
/// The same lock key as the DDL phase, so an instance rewriting and an instance creating tables
/// exclude each other too. Taken at transaction scope, in one transaction of this phase's own that
/// holds every rewrite: a connection pooler that hands each statement to a different backend cannot
/// separate the lock from the statements it guards, and nothing stays held if the instance dies.
/// Each rewrite runs under a savepoint inside that transaction, so one table's failure neither undoes
/// an earlier table nor stops a later one.
/// </para>
/// <para>
/// An instance that cannot take the lock waits for it. The holder is not necessarily another
/// rewriter: a sibling's bootstrap or DDL transaction holds the same key and converts nothing, and a
/// sibling staged to wait for the migrator never rewrites at all. Skipping on a lost attempt once
/// left every table of a schema unconverted, on a fleet that started together, with nothing above
/// debug level to say so. Losing an attempt means waiting, and running out of the wait budget is a
/// warning; the tables stay in their current form, which every reader tolerates, and the next start
/// tries again.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/CanonicalTemporalRewritePhaseTests.cs</tests>
public static class CanonicalTemporalRewritePhase {
  private const string SAVEPOINT = "wh_rewrite";

  /// <summary>
  /// Takes the schema lock, waiting up to the command timeout for it, and applies every rewrite
  /// under it.
  /// </summary>
  /// <param name="connectionFactory">Produces the connection the lock and the rewrites share.</param>
  /// <param name="lockId">The schema initialization lock key, the same one the DDL phase uses.</param>
  /// <param name="rewrites">The rewrites, named for reporting.</param>
  /// <param name="commandTimeoutSeconds">
  /// The timeout for one rewrite, which scans a whole table, and the budget for taking the lock.
  /// </param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <see langword="true"/> when this instance held the lock and attempted the rewrites,
  /// <see langword="false"/> when the lock stayed held elsewhere for the whole budget.
  /// </returns>
  public static Task<bool> ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      long lockId,
      IEnumerable<(string Name, string Sql)> rewrites,
      int commandTimeoutSeconds,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) =>
    ApplyAsync(
      connectionFactory, lockId, rewrites, commandTimeoutSeconds, TimeProvider.System, logger, cancellationToken);

  /// <summary>
  /// Takes the schema lock, waiting up to the command timeout for it on the given clock, and
  /// applies every rewrite under it.
  /// </summary>
  /// <param name="connectionFactory">Produces the connection the lock and the rewrites share.</param>
  /// <param name="lockId">The schema initialization lock key, the same one the DDL phase uses.</param>
  /// <param name="rewrites">The rewrites, named for reporting.</param>
  /// <param name="commandTimeoutSeconds">
  /// The timeout for one rewrite, which scans a whole table, and the budget for taking the lock.
  /// </param>
  /// <param name="timeProvider">The clock the wait is measured on.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <see langword="true"/> when this instance held the lock and attempted the rewrites,
  /// <see langword="false"/> when the lock stayed held elsewhere for the whole budget.
  /// </returns>
  public static async Task<bool> ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      long lockId,
      IEnumerable<(string Name, string Sql)> rewrites,
      int commandTimeoutSeconds,
      TimeProvider timeProvider,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(rewrites);
    ArgumentNullException.ThrowIfNull(timeProvider);

    // Non-null so the log calls below need no guard. NullLogger discards, at no cost.
    var log = logger ?? NullLogger.Instance;

    var pending = new List<(string Name, string Sql)>(rewrites);
    if (pending.Count == 0) {
      // Nothing to do, and nothing worth opening a connection or taking a lock for.
      return true;
    }

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken);

    var started = timeProvider.GetTimestamp();
    await using var transaction = await _acquireAsync(
      connection, lockId, TimeSpan.FromSeconds(commandTimeoutSeconds), timeProvider, log, cancellationToken);
    if (transaction is null) {
      return false;
    }

    // What a statement says about itself, such as how many rows it touched, comes back as a notice.
    // Relayed while this phase runs the statements, and only then.
    void relay(object sender, NpgsqlNoticeEventArgs e) =>
      CanonicalTemporalRewriteLog.Notice(log, e.Notice.MessageText);
    connection.Notice += relay;

    var applied = 0;
    try {
      foreach (var (name, sql) in pending) {
        await transaction.SaveAsync(SAVEPOINT, cancellationToken);
        try {
          await using var command = new NpgsqlCommand(sql, connection) {
            CommandTimeout = commandTimeoutSeconds,
          };
          await command.ExecuteNonQueryAsync(cancellationToken);
          await transaction.ReleaseAsync(SAVEPOINT, cancellationToken);
          applied++;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          // Reported rather than fatal. A rewrite that did not run leaves rows in the older format,
          // and the index built over them fails with its own reason, which is a better place to read
          // the problem than a startup that stopped before saying what it was doing. Rolled back to
          // the savepoint so the transaction stays usable for the tables that follow.
          await transaction.RollbackAsync(SAVEPOINT, cancellationToken);
          await transaction.ReleaseAsync(SAVEPOINT, cancellationToken);
          CanonicalTemporalRewriteLog.RewriteFailed(log, ex, name);
        }
      }

      await transaction.CommitAsync(cancellationToken);
    } finally {
      connection.Notice -= relay;
    }

    var elapsedMs = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;
    CanonicalTemporalRewriteLog.Applied(log, applied, pending.Count, lockId, elapsedMs);
    return true;
  }

  /// <summary>
  /// Opens the transaction that holds the lock, retrying with backoff until the lock is taken or
  /// the budget is spent.
  /// </summary>
  /// <returns>The transaction holding the lock, or <see langword="null"/> when the budget ran out.</returns>
  private static async Task<NpgsqlTransaction?> _acquireAsync(
      NpgsqlConnection connection,
      long lockId,
      TimeSpan lockWait,
      TimeProvider timeProvider,
      ILogger log,
      CancellationToken cancellationToken) {
    var started = timeProvider.GetTimestamp();
    var delay = SchemaMigrationDeferral.PollFloor;
    var reported = false;

    while (true) {
      var transaction = await _tryBeginLockedAsync(connection, lockId, cancellationToken);
      if (transaction is not null) {
        return transaction;
      }

      var elapsed = timeProvider.GetElapsedTime(started);
      var remaining = lockWait - elapsed;
      if (remaining <= TimeSpan.Zero) {
        CanonicalTemporalRewriteLog.LockNotAcquired(log, lockId, (long)elapsed.TotalSeconds);
        return null;
      }

      if (!reported) {
        // Once, not once per attempt: a long DDL pass on a sibling would otherwise fill the log
        // with the fact that this instance is still waiting.
        CanonicalTemporalRewriteLog.WaitingForLock(log, lockId);
        reported = true;
      }

      await Task.Delay(delay < remaining ? delay : remaining, timeProvider, cancellationToken);
      delay = SchemaMigrationDeferral.NextPollDelay(delay);
    }
  }

  /// <summary>
  /// Begins a transaction and tries for the lock inside it.
  /// </summary>
  /// <returns>
  /// The transaction, now holding the lock, or <see langword="null"/> after ending the transaction
  /// because another session holds it.
  /// </returns>
  private static async Task<NpgsqlTransaction?> _tryBeginLockedAsync(
      NpgsqlConnection connection, long lockId, CancellationToken cancellationToken) {
    var transaction = await connection.BeginTransactionAsync(cancellationToken);
    var held = false;
    try {
      await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection);
      command.Parameters.AddWithValue(lockId);
      held = await command.ExecuteScalarAsync(cancellationToken) is true;
      return held ? transaction : null;
    } finally {
      if (!held) {
        // Ends the empty transaction so the connection is clean for the next attempt. A transaction
        // disposed without a commit is rolled back.
        await transaction.DisposeAsync();
      }
    }
  }
}

/// <summary>Source-generated logging for the rewrite phase.</summary>
internal static partial class CanonicalTemporalRewriteLog {
  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Stored-format rewrite is waiting for schema lock {LockId}, held by another instance")]
  public static partial void WaitingForLock(ILogger logger, long lockId);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Stored-format rewrite could not take schema lock {LockId} within {WaitedSeconds}s; "
              + "the tables stay in their current form and the next start tries again")]
  public static partial void LockNotAcquired(ILogger logger, long lockId, long waitedSeconds);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Stored-format rewrite: {Message}")]
  public static partial void Notice(ILogger logger, string message);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Stored-format rewrite applied {Applied} of {Total} table statement(s) under schema "
              + "lock {LockId} in {ElapsedMs} ms")]
  public static partial void Applied(ILogger logger, int applied, int total, long lockId, long elapsedMs);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Could not apply the stored-format rewrite for {Perspective}; an index over the "
              + "rewritten key will fail until it succeeds")]
  public static partial void RewriteFailed(ILogger logger, Exception exception, string perspective);
}
