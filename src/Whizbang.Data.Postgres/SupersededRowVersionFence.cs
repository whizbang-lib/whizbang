// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Waits, after a committed write, until nothing on the server can keep the row versions that write
/// superseded visible to an index build.
/// </summary>
/// <remarks>
/// <para>
/// A commit is necessary and not sufficient for an index over a rewritten value. A plain
/// <c>CREATE INDEX</c> indexes every row version some open snapshot can still see, not only the live
/// one, and the version a rewrite superseded is one of those for as long as any snapshot taken
/// before the rewrite committed lives. That version still carries the old rendering, so an index
/// expression that casts the new one fails on it. At startup, other instances, long requests, the
/// server's own ANALYZE and the work of other databases on the same server are all running, so the
/// schema pass meets such a snapshot routinely, and meets it more often the busier the server is.
/// </para>
/// <para>
/// This does for that one write what <c>CREATE INDEX CONCURRENTLY</c> does for its own build: it
/// waits for every transaction older than a reference point to finish. The reference point is the
/// writer's own transaction id, so a session whose snapshot or transaction began after the write
/// committed is never waited for. The concurrent build itself is not used instead because the
/// indexes are built inside the initializer's transaction, where it cannot run, and because its own
/// wait would block on that transaction's locks.
/// </para>
/// <para>
/// What is waited for mirrors what the server consults when it decides whether a superseded row
/// version is still needed. A snapshot held by a session of this database, or of none, counts
/// directly. So does any transaction that has written and is still running, in any database of the
/// server: transaction ids are server-wide, so while one older than the write is open, every new
/// snapshot, the index build's own included, reaches back past the write, and the build treats the
/// superseded rows as still visible. That is why a busy shared server fails this far more often
/// than a quiet one, although the database being migrated has nothing else connected. Prepared
/// transactions count the same way, and so do replication slots of this database. A plain
/// <c>VACUUM</c> in progress is left out, because the server leaves it out too, and waiting for a
/// long one would stall a startup for nothing.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaCommandBoundaryTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/CanonicalTemporalRewritePhaseTests.cs</tests>
internal static class SupersededRowVersionFence {
  /// <summary>
  /// The current transaction's id, or null when it has written nothing and so superseded nothing.
  /// </summary>
  private const string CAPTURE_SQL = "SELECT pg_current_xact_id_if_assigned()::text";

  /// <summary>
  /// Every session, prepared transaction and slot whose snapshot or transaction is not newer than
  /// the writer, one description each: a snapshot only in this database, a running writer in any.
  /// </summary>
  /// <remarks>
  /// <c>age</c> measures distance back from the newest transaction, so "at least as old as the
  /// writer" is a comparison of ages, which stays correct across transaction id wraparound where
  /// comparing the ids themselves would not. A snapshot whose horizon is the writer itself was taken
  /// while the writer was still running, so it cannot see the rewrite and is waited for too.
  /// </remarks>
  private const string HOLDERS_SQL = """
    WITH fence AS (SELECT age($1::xid8::xid) AS writer_age)
    SELECT format('pid %s (%s, database "%s", application "%s", %s, transaction started %s)',
                  a.pid, coalesce(a.backend_type, '?'), coalesce(a.datname, ''),
                  coalesce(a.application_name, ''), coalesce(a.state, '?'),
                  coalesce(a.xact_start::text, '?'))
    FROM pg_stat_activity a, fence f
    WHERE a.pid <> pg_backend_pid()
      AND ((a.backend_xid IS NOT NULL AND age(a.backend_xid) >= f.writer_age)
        OR ((a.datid IS NULL OR a.datname = current_database())
            AND a.pid NOT IN (SELECT v.pid FROM pg_stat_progress_vacuum v)
            AND a.backend_xmin IS NOT NULL AND age(a.backend_xmin) >= f.writer_age))
    UNION ALL
    SELECT format('prepared transaction "%s" (database "%s")', p.gid, p.database)
    FROM pg_prepared_xacts p, fence f
    WHERE age(p.transaction) >= f.writer_age
    UNION ALL
    SELECT format('replication slot "%s"', s.slot_name)
    FROM pg_replication_slots s, fence f
    WHERE (s.database IS NULL OR s.database = current_database())
      AND s.xmin IS NOT NULL AND age(s.xmin) >= f.writer_age
    """;

  /// <summary>
  /// The id of the transaction open on <paramref name="connection"/>, or null when it has written
  /// nothing.
  /// </summary>
  /// <param name="connection">The connection the write ran on.</param>
  /// <param name="transaction">The transaction the write ran in, not yet committed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The transaction id as text, or <see langword="null"/>.</returns>
  /// <remarks>
  /// Read before the commit, because only then is it the writer's own. Null is the common case on
  /// every start after the first, and it means there is nothing to wait for.
  /// </remarks>
  internal static async Task<string?> CaptureWriterAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(CAPTURE_SQL, connection, transaction);
    return await command.ExecuteScalarAsync(cancellationToken) as string;
  }

  /// <summary>
  /// Waits until nothing older than <paramref name="writerTransactionId"/> is left, or the budget is
  /// spent.
  /// </summary>
  /// <param name="connection">An open connection with no transaction of its own.</param>
  /// <param name="writerTransactionId">The committed writer, from <see cref="CaptureWriterAsync"/>.</param>
  /// <param name="budget">How long to wait at most.</param>
  /// <param name="timeProvider">The clock the budget is measured on.</param>
  /// <param name="log">Where the wait is reported.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// Empty once nothing older is left; otherwise what was still holding on when the budget ran out,
  /// one description each, for the caller to report.
  /// </returns>
  /// <remarks>
  /// No second check is needed once it clears: a snapshot taken after the writer committed
  /// can only reach back past it through a transaction that was already running, and that
  /// transaction is one of the things waited for here.
  /// </remarks>
  internal static async Task<IReadOnlyList<string>> WaitAsync(
      NpgsqlConnection connection,
      string writerTransactionId,
      TimeSpan budget,
      TimeProvider timeProvider,
      ILogger log,
      CancellationToken cancellationToken) {
    var started = timeProvider.GetTimestamp();
    var delay = SchemaMigrationDeferral.PollFloor;
    var reported = false;

    while (true) {
      var holders = await _holdersAsync(connection, writerTransactionId, cancellationToken);
      if (holders.Count == 0) {
        return holders;
      }

      var remaining = budget - timeProvider.GetElapsedTime(started);
      if (remaining <= TimeSpan.Zero) {
        return holders;
      }

      if (!reported) {
        // Once, not once per poll: the holders rarely change while a long transaction runs.
        SupersededRowVersionFenceLog.Waiting(log, holders.Count, holders);
        reported = true;
      }

      await Task.Delay(delay < remaining ? delay : remaining, timeProvider, cancellationToken);
      delay = SchemaMigrationDeferral.NextPollDelay(delay);
    }
  }

  private static async Task<IReadOnlyList<string>> _holdersAsync(
      NpgsqlConnection connection, string writerTransactionId, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(HOLDERS_SQL, connection);
    command.Parameters.AddWithValue(writerTransactionId);

    var holders = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      holders.Add(reader.GetString(0));
    }

    return holders;
  }
}

/// <summary>Source-generated logging for the superseded-row-version wait.</summary>
internal static partial class SupersededRowVersionFenceLog {
  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Waiting for {Count} session(s) holding a snapshot older than a committed rewrite, "
              + "which can still see the rows it replaced; an index over the rewritten value cannot "
              + "be built until they finish: {Holders}")]
  public static partial void Waiting(ILogger logger, int count, IReadOnlyList<string> holders);
}
