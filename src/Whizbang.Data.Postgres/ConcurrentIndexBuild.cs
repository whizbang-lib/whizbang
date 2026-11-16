// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>A framework index built concurrently after the migrations, never inside a migration's transaction.</summary>
/// <param name="Table">The table, unqualified.</param>
/// <param name="Name">The index name.</param>
/// <param name="Columns">The key, parenthesized, as <c>CREATE INDEX</c> takes it.</param>
/// <param name="Predicate">The partial-index predicate, or null.</param>
/// <param name="Comment">The index comment, or null.</param>
/// <param name="Replaces">An index this one supersedes (#2345), dropped concurrently once this one is valid; null for none.
/// A migration cannot rebuild an index on a large table, and a valid index under an existing name is never rebuilt, so a
/// changed index is built under a new name and the old one goes only after it can serve.</param>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
public sealed record ConcurrentIndexDefinition(
  string Table, string Name, string Columns, string? Predicate, string? Comment = null, string? Replaces = null);

/// <summary>What one <see cref="ConcurrentIndexBuild.EnsureAsync"/> did.</summary>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
public enum ConcurrentIndexOutcome {
  /// <summary>The index was already there and valid.</summary>
  AlreadyValid,

  /// <summary>The index was built.</summary>
  Built,

  /// <summary>The table does not exist; nothing to index.</summary>
  TableAbsent,

  /// <summary>Another session holds the build; this one stepped aside.</summary>
  Busy,

  /// <summary>The build failed; no invalid index was left behind.</summary>
  Failed,
}

/// <summary>
/// Builds the framework indexes a migration must not build (#2092): an index on a large, write-heavy table, which a
/// <c>CREATE INDEX</c> inside the migration's transaction would hold every write to for the whole build. Each is built
/// with <c>CREATE INDEX CONCURRENTLY</c>, outside any transaction, so writes go on while it builds; reads that would use
/// it run without it until it is valid.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent and restart-safe: a valid index is left alone, so once built each run is one catalog read. It runs on the
/// maintainer's turn (the maintenance step), so one instance builds; and while any index build is in progress on the
/// table (<c>pg_stat_progress_create_index</c>) it steps aside, because an invalid index under the name may then be that
/// build's work in progress. The view lists every database's builds and a relation id is unique only within its
/// database (two databases created from one template give a table the same id), so only this database's builds count. A build that races another for the name fails on the name and leaves that build alone: the
/// cleanup after a failure drops only an invalid index no build is working on.
/// </para>
/// <para>
/// A concurrent build that is interrupted (a crash, a cancellation, a failure reading the table) leaves an INVALID index
/// under the name, which every later <c>CREATE INDEX IF NOT EXISTS</c> would take for the index while no read could use
/// it. With no build in progress, an invalid index is a leftover: it is dropped concurrently and the index built again,
/// and a build that fails drops what it left before reporting.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ConcurrentIndexBuildTests.cs</tests>
public static partial class ConcurrentIndexBuild {
  /// <summary>
  /// The framework indexes built this way: the paced backfill's read of one type in commit order (#2092), the
  /// paced repair drain's count of the answers it is waiting for (#2184), and the claim's held-lanes walk over the inbox,
  /// rebuilt to cover chain_emitted_at in place of 167's index (#2345). The first one's predicate also names
  /// <c>event_type IS NOT NULL</c>, which every row satisfies (the column is NOT NULL) but only a query filtering on the
  /// type implies, so the planner never offers it to a commit-sequence probe that names no type.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/PacedBackfillIndexTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAnswerPendingCountSqlTests.cs:Count_OnceTheIndexIsBuilt_IsAnsweredFromItAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkPlanShapeTests.HeldInbox.cs:ClaimWork_ReofferingHeldInbox_WalksTheCoveringIndex_IndexOnlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ConcurrentIndexBuildTests.Replaces.cs:TheHeldLanesIndex_IsRebuiltCovering_AndTheInboxStateKeepsItsIndexCountAsync</tests>
  public static IReadOnlyList<ConcurrentIndexDefinition> FrameworkIndexes { get; } = [
    new ConcurrentIndexDefinition(
      "wh_event_store",
      "idx_event_store_type_commit_sequence",
      "(event_type, commit_sequence)",
      "commit_sequence IS NOT NULL AND event_type IS NOT NULL",
      "One type's stamped events in commit order (#2092): a paced backfill window reads from a commit sequence onward under a limit, and a window answer reads the type's head. Built concurrently after the migrations."),
    new ConcurrentIndexDefinition(
      "wh_integrity_ledger",
      "idx_integrity_ledger_answer_pending",
      "(answer_pending_until)",
      "answer_pending_until IS NOT NULL",
      "The repair-ledger buckets a request was sent for (#2184): the paced drain counts those still awaiting their answer, up to its cap, on every tick it has tokens to spend. Built concurrently after the migrations."),
    new ConcurrentIndexDefinition(
      "wh_inbox_state",
      "idx_inbox_state_held_lanes_covering",
      "(instance_id, (CASE WHEN priority <= 99 THEN 0 WHEN priority <= 199 THEN 1 WHEN priority <= 399 THEN 2 ELSE 3 END), "
        + "is_event, ((attempts = 0)), stream_id, received_at, message_id) "
        + "INCLUDE (priority, attempts, partition_number, status, lease_expiry, chain_emitted_at)",
      "processed_at IS NULL",
      "Pending inbox rows by holder, priority bucket, kind, class, stream and arrival (#2345): claim_work's held-lanes walk reads every column it tests from here, chain_emitted_at included, so each probe is index-only and a held row it passes over costs no heap page. Replaces idx_inbox_state_held_lanes (167). Built concurrently after the migrations.",
      Replaces: "idx_inbox_state_held_lanes"),
  ];

  /// <summary>Ensures <paramref name="index"/> exists in <paramref name="schema"/> and is valid, building it concurrently.</summary>
  /// <param name="connection">An open connection, outside any transaction, that the caller owns.</param>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="index">The index.</param>
  /// <param name="commandTimeoutSeconds">The timeout for the build (0 waits as long as it takes); a concurrent build
  /// waits for every transaction older than it, then reads the table twice.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What it did.</returns>
  public static async Task<ConcurrentIndexOutcome> EnsureAsync(
      NpgsqlConnection connection, string schema, ConcurrentIndexDefinition index, int commandTimeoutSeconds,
      ILogger? logger = null, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentNullException.ThrowIfNull(index);
    var log = logger ?? NullLogger.Instance;
    var state = await _stateAsync(connection, schema, index, cancellationToken).ConfigureAwait(false);
    if (state == IndexState.Valid) {
      await _dropReplacedAsync(connection, schema, index, commandTimeoutSeconds, log, cancellationToken).ConfigureAwait(false);
      return ConcurrentIndexOutcome.AlreadyValid;
    }
    if (state == IndexState.TableAbsent) {
      return ConcurrentIndexOutcome.TableAbsent;
    }
    if (state == IndexState.Building) {
      LogBusy(log, index.Name, schema);
      return ConcurrentIndexOutcome.Busy;
    }
    var outcome = await _buildAsync(connection, schema, index, state, commandTimeoutSeconds, log, cancellationToken).ConfigureAwait(false);
    if (outcome == ConcurrentIndexOutcome.Built) {
      await _dropReplacedAsync(connection, schema, index, commandTimeoutSeconds, log, cancellationToken).ConfigureAwait(false);
    }
    return outcome;
  }

  /// <summary>
  /// #2345: once <paramref name="index"/> is valid, drops the index it replaces, concurrently, when that one is still
  /// there. Never before: until the new index can serve, the reads that used the old one still need it. A run after the
  /// drop finds nothing to drop, one catalog read.
  /// </summary>
  private static async Task _dropReplacedAsync(
      NpgsqlConnection connection, string schema, ConcurrentIndexDefinition index, int commandTimeoutSeconds, ILogger log,
      CancellationToken cancellationToken) {
    if (index.Replaces is not { } replaced) {
      return;
    }
    await using (var exists = new NpgsqlCommand("""
      SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = $1 AND c.relname = $2 AND c.relkind = 'i')
      """, connection)) {
      exists.Parameters.Add(new NpgsqlParameter { Value = schema });
      exists.Parameters.Add(new NpgsqlParameter { Value = replaced });
      if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!) {
        return;
      }
    }
    await _executeAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {PgIdentifier.Quote(schema)}.{PgIdentifier.Quote(replaced)}",
      commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    LogDroppedReplaced(log, replaced, schema, index.Name);
  }

  private static async Task<ConcurrentIndexOutcome> _buildAsync(
      NpgsqlConnection connection, string schema, ConcurrentIndexDefinition index, IndexState state,
      int commandTimeoutSeconds, ILogger log, CancellationToken cancellationToken) {
    var qualifiedIndex = $"{PgIdentifier.Quote(schema)}.{PgIdentifier.Quote(index.Name)}";
    try {
      if (state == IndexState.Invalid) {
        await _executeAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {qualifiedIndex}", commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        LogDroppedLeftover(log, index.Name, schema);
      }
      var where = index.Predicate is null ? string.Empty : $" WHERE {index.Predicate}";
      await _executeAsync(connection,
        $"CREATE INDEX CONCURRENTLY {PgIdentifier.Quote(index.Name)} ON {PgIdentifier.Quote(schema)}.{PgIdentifier.Quote(index.Table)} {index.Columns}{where}",
        commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
      if (index.Comment is not null) {
        await _executeAsync(connection,
          $"COMMENT ON INDEX {qualifiedIndex} IS '{index.Comment.Replace("'", "''", StringComparison.Ordinal)}'",
          commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
      }
      LogBuilt(log, index.Name, schema, index.Table);
      return ConcurrentIndexOutcome.Built;
    } catch (PostgresException ex) {
      // Only an invalid index no build is working on is this build's leftover; another session's build in progress is
      // left alone.
      LogFailed(log, ex, index.Name, schema, index.Table);
      if (await _stateAsync(connection, schema, index, CancellationToken.None).ConfigureAwait(false) == IndexState.Invalid) {
        await _executeAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS {qualifiedIndex}", commandTimeoutSeconds, CancellationToken.None).ConfigureAwait(false);
      }
      return ConcurrentIndexOutcome.Failed;
    }
  }

  private enum IndexState { Absent, Valid, Invalid, TableAbsent, Building }

  private static async Task<IndexState> _stateAsync(
      NpgsqlConnection connection, string schema, ConcurrentIndexDefinition index, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("""
      SELECT CASE
        WHEN to_regclass(format('%I.%I', $1, $2)) IS NULL THEN 'table-absent'
        WHEN EXISTS (SELECT 1 FROM pg_stat_progress_create_index p
                     WHERE p.datid = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database())
                       AND p.relid = to_regclass(format('%I.%I', $1, $2))) THEN 'building'
        ELSE COALESCE((SELECT CASE WHEN i.indisvalid THEN 'valid' ELSE 'invalid' END
                       FROM pg_class c
                       JOIN pg_namespace n ON n.oid = c.relnamespace
                       JOIN pg_index i ON i.indexrelid = c.oid
                       WHERE n.nspname = $1 AND c.relname = $3), 'absent')
      END
      """, connection);
    command.Parameters.Add(new NpgsqlParameter { Value = schema });
    command.Parameters.Add(new NpgsqlParameter { Value = index.Table });
    command.Parameters.Add(new NpgsqlParameter { Value = index.Name });
    var state = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    return state switch {
      "valid" => IndexState.Valid,
      "invalid" => IndexState.Invalid,
      "table-absent" => IndexState.TableAbsent,
      "building" => IndexState.Building,
      _ => IndexState.Absent,
    };
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive",
    Justification = "Identifiers are quoted by PgIdentifier from the framework's own definitions; the columns and predicate are framework constants.")]
  private static async Task _executeAsync(NpgsqlConnection connection, string sql, int commandTimeoutSeconds, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Index {Index} in {Schema} built concurrently on {Table}")]
  private static partial void LogBuilt(ILogger logger, string index, string schema, string table);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Index {Index} in {Schema}: dropped the invalid leftover of an interrupted concurrent build before building it again")]
  private static partial void LogDroppedLeftover(ILogger logger, string index, string schema);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Index {Replaced} in {Schema} dropped concurrently: {Index}, which replaces it, is valid")]
  private static partial void LogDroppedReplaced(ILogger logger, string replaced, string schema, string index);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Index {Index} in {Schema}: an index build is in progress on its table; this run steps aside")]
  private static partial void LogBusy(ILogger logger, string index, string schema);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Index {Index} in {Schema} on {Table} could not be built concurrently; reads that would use it run without it, and the next maintenance run tries again")]
  private static partial void LogFailed(ILogger logger, Exception exception, string index, string schema, string table);
}
