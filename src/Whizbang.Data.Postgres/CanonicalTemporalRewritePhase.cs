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
/// that makes one instance do the schema work, so the lock has to be taken again here, at session
/// scope, over the connection that runs them.
/// </para>
/// <para>
/// Without it every replica rewrites concurrently. The statements are idempotent, so the result
/// would still be correct, but two full-table updates over the same rows in unpredictable order can
/// deadlock each other, and a fleet starting together would do the same scan many times over.
/// </para>
/// <para>
/// The same lock key as the DDL phase, so an instance rewriting and an instance creating tables
/// exclude each other too. Session scope rather than transaction scope because each rewrite commits
/// on its own; the lock is released explicitly, and by the backend if the connection dies.
/// </para>
/// <para>
/// An instance that cannot take the lock applies nothing and says so at debug level. That is the
/// expected outcome for every instance but one, not a failure: the holder commits the rewrite, and
/// the losers' own index builds then read converted rows.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/CanonicalTemporalRewritePhaseTests.cs</tests>
public static class CanonicalTemporalRewritePhase {
  /// <summary>
  /// Takes the schema lock and applies every rewrite under it.
  /// </summary>
  /// <param name="connectionFactory">Produces the connection the lock and the rewrites share.</param>
  /// <param name="lockId">The schema initialization lock key, the same one the DDL phase uses.</param>
  /// <param name="rewrites">The rewrites, named for reporting.</param>
  /// <param name="commandTimeoutSeconds">The timeout for one rewrite, which scans a whole table.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <see langword="true"/> when this instance held the lock and attempted the rewrites,
  /// <see langword="false"/> when another instance held it and this one did nothing.
  /// </returns>
  public static async Task<bool> ApplyAsync(
      Func<NpgsqlConnection> connectionFactory,
      long lockId,
      IEnumerable<(string Name, string Sql)> rewrites,
      int commandTimeoutSeconds,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(rewrites);

    // Non-null so the log calls below need no guard. NullLogger discards, at no cost.
    var log = logger ?? NullLogger.Instance;

    var pending = new List<(string Name, string Sql)>(rewrites);
    if (pending.Count == 0) {
      // Nothing to do, and nothing worth opening a connection or taking a lock for.
      return true;
    }

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken);

    if (!await _tryLockAsync(connection, lockId, cancellationToken)) {
      CanonicalTemporalRewriteLog.LockHeldElsewhere(log, lockId);
      return false;
    }

    try {
      foreach (var (name, sql) in pending) {
        try {
          await SchemaCommandBoundary.ApplyOnAsync(
            connection, sql, commandTimeoutSeconds, cancellationToken);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          // Reported rather than fatal. A rewrite that did not run leaves rows in the older format,
          // and the index built over them fails with its own reason, which is a better place to read
          // the problem than a startup that stopped before saying what it was doing.
          CanonicalTemporalRewriteLog.RewriteFailed(log, ex, name);
        }
      }
    } finally {
      await _unlockAsync(connection, lockId);
    }

    return true;
  }

  private static async Task<bool> _tryLockAsync(
      NpgsqlConnection connection, long lockId, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
    command.Parameters.AddWithValue(lockId);
    return await command.ExecuteScalarAsync(cancellationToken) is true;
  }

  private static async Task _unlockAsync(NpgsqlConnection connection, long lockId) {
    // Deliberately not cancellable: a cancelled unlock would leave the lock held for the life of
    // the connection and stall every other instance's rewrite.
    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
    command.Parameters.AddWithValue(lockId);
    await command.ExecuteScalarAsync(CancellationToken.None);
  }
}

/// <summary>Source-generated logging for the rewrite phase.</summary>
internal static partial class CanonicalTemporalRewriteLog {
  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Stored-format rewrites skipped: another instance holds schema lock {LockId}")]
  public static partial void LockHeldElsewhere(ILogger logger, long lockId);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Could not apply the stored-format rewrite for {Perspective}; an index over the "
              + "rewritten key will fail until it succeeds")]
  public static partial void RewriteFailed(ILogger logger, Exception exception, string perspective);
}
