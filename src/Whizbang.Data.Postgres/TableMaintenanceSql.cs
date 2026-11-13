// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The table-maintenance statements both Postgres work coordinators run: the rewrite queue and its measurements, the
/// bloat estimate and the approximate measurement, <c>VACUUM (FULL)</c>, <c>ANALYZE</c> and <c>VACUUM ANALYZE</c>, the
/// pg_squeeze call, the ownership check that decides whether the connection's role may run them, and the queue-index
/// survey, lookup, drop and rebuild.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift. The Dapper coordinator used to inherit the interface's inert defaults for every
/// one of them, so on that driver the maintenance cycle reported nothing to rewrite, vacuum or rebuild (#2051).
/// </para>
/// <para>
/// Which connection a statement runs on is the caller's decision: the EF Core coordinator uses the schema-initialization
/// connection when one is configured and its own otherwise; the Dapper coordinator, whose schema is created on the
/// application connection, uses its own. Every table and index name that reaches DDL is either a framework constant, a
/// name checked by <see cref="RequireFrameworkTable"/>, or one the catalog has confirmed, and it is quoted.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperTableMaintenanceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteRequestSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkTableVacuumTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OnlineTableRewriteSqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier, table names are checked against the framework's naming rule and index names are confirmed by the catalog and quoted. Every value is a bound parameter.")]
internal static class TableMaintenanceSql {
  /// <summary>How long one work-table <c>VACUUM ANALYZE</c> may run before the vacuum gives up on it.</summary>
  internal const int WORK_TABLE_VACUUM_TIMEOUT_SECONDS = 120;

  /// <summary>The work tables vacuumed when an instance acquires the maintainer duty (#1351), unqualified, in order.</summary>
  internal static readonly string[] VACUUMED_WORK_TABLES = ["wh_outbox", "wh_inbox", "wh_perspective_events", "wh_message_deduplication"];

  /// <summary>Refuses a name that is not one of the framework's own tables: it is interpolated into DDL.</summary>
  internal static void RequireFrameworkTable(string tableName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    if (!IsFrameworkTableName(tableName)) {
      throw new ArgumentException(
        $"Refusing to rewrite '{tableName}': only framework tables matching wh_[a-z0-9_] may be rewritten.",
        nameof(tableName));
    }
  }

  /// <summary>
  /// Whether a name is one of the framework's own tables, checked character by character rather than by regex: this is
  /// the only thing between a caller-supplied name and interpolated DDL, and a plain scan has no backtracking to reason about.
  /// </summary>
  internal static bool IsFrameworkTableName(string name) {
    if (name.Length <= 3 || name.Length > 63 || !name.StartsWith("wh_", StringComparison.Ordinal)) {
      return false;
    }
    foreach (var c in name) {
      var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
      if (!ok) {
        return false;
      }
    }
    return true;
  }

  /// <summary>
  /// The rewrite candidates: every table the scan estimates bloated, and every queued one, with the heap size an online
  /// rewrite copies, a queued table's measurement and method when it was measured before it was queued (#1807), and the
  /// threshold the scan compared with; then every queued table owed a rewrite for a lowered fillfactor that the scan
  /// did not offer, as <see cref="TableRewriteReason.FillFactorDecrease"/> (#2300). Largest ratio first.
  /// </summary>
  internal static async Task<IReadOnlyList<TableRewriteCandidate>> TablesNeedingRewriteAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $"WITH b AS MATERIALIZED (SELECT * FROM {prefix}wh_tables_needing_rewrite()), "
      + "r AS (SELECT b.table_name, b.bloat_ratio, b.requested, FALSE AS decrease FROM b "
      + $"UNION ALL SELECT f.table_name, f.bloat_ratio, TRUE, TRUE FROM {prefix}wh_tables_owed_fillfactor_rewrite() f "
      + "WHERE NOT EXISTS (SELECT 1 FROM b WHERE b.table_name = f.table_name)) "
      + "SELECT r.table_name, COALESCE(m.bloat_ratio, r.bloat_ratio), r.requested, "
      + "COALESCE(pg_relation_size(to_regclass(@prefix || quote_ident(r.table_name))), 0), m.method, "
      + $"COALESCE((SELECT s.setting_value FROM {prefix}wh_settings s WHERE s.setting_key = 'table_rewrite_bloat_threshold'), '3.0')::NUMERIC, "
      + "r.decrease "
      + $"FROM r LEFT JOIN {prefix}wh_table_rewrite_measurements m ON r.requested AND m.table_name = r.table_name "
      + "ORDER BY 2 DESC, 1";
    cmd.Parameters.Add(new NpgsqlParameter(nameof(prefix), prefix));
    var results = new List<TableRewriteCandidate>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new TableRewriteCandidate(reader.GetString(0), (double)reader.GetDecimal(1), reader.GetBoolean(2)) {
        SizeBytes = reader.GetInt64(3),
        Method = BloatMeasurementMethods.Parse(reader.IsDBNull(4) ? null : reader.GetString(4)),
        RewriteThreshold = (double)reader.GetDecimal(5),
        Reason = reader.GetBoolean(6) ? TableRewriteReason.FillFactorDecrease : TableRewriteReason.Bloat,
      });
    }
    return results;
  }

  /// <summary>Queues a rewrite of the table (<c>wh_request_table_rewrite</c>); queuing it again is absorbed.</summary>
  internal static Task RequestRewriteAsync(DbCommand cmd, string prefix, string tableName, CancellationToken cancellationToken) =>
    _tableFunctionAsync(cmd, prefix + "wh_request_table_rewrite", tableName, cancellationToken);

  /// <summary>Clears the table's rewrite request and its measurement (<c>wh_clear_table_rewrite</c>).</summary>
  internal static Task ClearRewriteRequestAsync(DbCommand cmd, string prefix, string tableName, CancellationToken cancellationToken) =>
    _tableFunctionAsync(cmd, prefix + "wh_clear_table_rewrite", tableName, cancellationToken);

  /// <summary>Queues a rewrite with the measurement that confirmed it and the estimate it confirmed (<c>wh_request_measured_table_rewrite</c>).</summary>
  internal static async Task RequestMeasuredRewriteAsync(
      DbCommand cmd, string prefix, string tableName, BloatMeasurement measurement, double estimatedRatio, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}wh_request_measured_table_rewrite(@t, @ratio::NUMERIC, @method, @estimate::NUMERIC)";
    cmd.Parameters.Add(new NpgsqlParameter("t", tableName));
    cmd.Parameters.Add(new NpgsqlParameter("ratio", measurement.Ratio));
    cmd.Parameters.Add(new NpgsqlParameter("method", measurement.Method.ToValue()));
    cmd.Parameters.Add(new NpgsqlParameter("estimate", estimatedRatio));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The approximate measurement (<c>wh_measure_table_bloat_approx</c>, <c>pgstattuple_approx</c>), or null for a table
  /// that is not there. Raises when pgstattuple is not installed.
  /// </summary>
  internal static async Task<double?> MeasureApproxAsync(DbCommand cmd, string prefix, string tableName, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}wh_measure_table_bloat_approx(@t)";
    cmd.Parameters.Add(new NpgsqlParameter("t", tableName));
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is decimal ratio ? (double)ratio : null;
  }

  /// <summary>
  /// The table's bloat ratio, measured as <c>wh_tables_needing_rewrite</c> measures it, in <paramref name="schema"/>, or in
  /// the connection's <c>current_schema()</c> when it is null.
  /// </summary>
  /// <remarks>
  /// <c>pg_class.reltuples</c>, not <c>pg_stat_user_tables.n_live_tup</c>: the stats collector is asynchronous, so
  /// <c>n_live_tup</c> can lag the rewrite that just ran and the effectiveness comparison then races it. Rounded exactly as
  /// the scan rounds the ratio it is compared with: unrounded, a rewrite that reclaimed nothing measured as an improvement.
  /// </remarks>
  internal static async Task<double?> MeasureRatioAsync(
      DbConnection conn, string tableName, string? schema, CancellationToken cancellationToken) {
    await using var measure = CoordinatorCommandTimeout.Apply(conn.CreateCommand());
    measure.CommandText = """
      SELECT round((pg_relation_size(c.oid)::NUMERIC / NULLIF(c.reltuples::NUMERIC, 0)) / GREATEST(w.expected, 1), 2)
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      JOIN LATERAL (
        SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
        FROM pg_stats s WHERE s.schemaname = n.nspname AND s.tablename = c.relname
      ) w ON TRUE
      WHERE n.nspname = COALESCE(@s, current_schema()) AND c.relname = @t
      """;
    measure.Parameters.Add(new NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)schema ?? DBNull.Value });
    measure.Parameters.Add(new NpgsqlParameter("t", tableName));
    return await measure.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is decimal d ? (double)d : null;
  }

  /// <summary>The remedy the EF Core coordinator names for a table its role does not own.</summary>
  internal const string INIT_CONNECTION_REMEDY =
    "no schema-initialization (-init) connection is configured; configure one so the table owner runs it";

  /// <summary>
  /// Null when the connection's role owns <paramref name="qualified"/> (is a member of its owner, as <c>VACUUM</c>
  /// requires); otherwise why the table cannot be maintained on it, naming the table, the role and
  /// <paramref name="remedy"/> (by default <see cref="INIT_CONNECTION_REMEDY"/>).
  /// </summary>
  internal static async Task<string?> NotOwnedReasonAsync(
      DbConnection conn, string qualified, string table, CancellationToken cancellationToken, string remedy = INIT_CONNECTION_REMEDY) {
    await using var check = CoordinatorCommandTimeout.Apply(conn.CreateCommand());
    check.CommandText = """
      SELECT current_user::TEXT, (SELECT pg_has_role(current_user, c.relowner, 'MEMBER') FROM pg_class c WHERE c.oid = to_regclass(@q))
      """;
    check.Parameters.Add(new NpgsqlParameter("q", qualified));
    await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    var role = reader.GetString(0);
    if (reader.IsDBNull(1)) {
      return $"{table} does not exist";
    }
    return reader.GetBoolean(1)
      ? null
      : $"role '{role}' does not own {table} and {remedy}";
  }

  /// <summary>The rewrite itself, then <c>ANALYZE</c> so the caller's re-measure sees what the rewrite did.</summary>
  internal static async Task VacuumFullAndAnalyzeAsync(DbConnection conn, string qualified, CancellationToken cancellationToken) {
    await using (var vacuum = conn.CreateCommand()) {
      vacuum.CommandText = $"VACUUM (FULL) {qualified}";
      vacuum.CommandTimeout = 0;   // a large table can take minutes; the operator opted into this
      await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    await AnalyzeAsync(conn, qualified, cancellationToken).ConfigureAwait(false);
  }

  /// <summary><c>ANALYZE</c>, so a re-measure reads the row count the rewrite left rather than the one before it.</summary>
  internal static async Task AnalyzeAsync(DbConnection conn, string qualified, CancellationToken cancellationToken) {
    await using var analyze = CoordinatorCommandTimeout.Apply(conn.CreateCommand());
    analyze.CommandText = $"ANALYZE {qualified}";
    await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// <c>VACUUM ANALYZE</c> of every work table on <paramref name="conn"/>, the one loop both coordinators run (#1351,
  /// #2140). A table <paramref name="notOwned"/> names a reason for is reported and left alone; a table the database
  /// records as vacuumed (manually or by autovacuum, by any instance) within <paramref name="skipVacuumedWithin"/> is
  /// reported as <see cref="WorkTableVacuum.RecentlyVacuumed"/> and left alone, so a hand-off shortly after a takeover
  /// does not vacuum again.
  /// </summary>
  /// <param name="conn">An open connection outside any transaction.</param>
  /// <param name="qualify">Qualifies a work table's name with the coordinator's schema.</param>
  /// <param name="skipVacuumedWithin">How recent a vacuum makes another unnecessary; zero vacuums every table.</param>
  /// <param name="notOwned">The ownership check, or null when the connection owns the tables.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>One outcome per work table.</returns>
  internal static async Task<IReadOnlyList<WorkTableVacuum>> VacuumWorkTablesAsync(
      DbConnection conn, Func<string, string> qualify, TimeSpan skipVacuumedWithin,
      Func<DbConnection, string, string, CancellationToken, Task<string?>>? notOwned, CancellationToken cancellationToken) {
    var outcomes = new List<WorkTableVacuum>(VACUUMED_WORK_TABLES.Length);
    foreach (var table in VACUUMED_WORK_TABLES) {
      var qualified = qualify(table);
      if (notOwned is not null && await notOwned(conn, qualified, table, cancellationToken).ConfigureAwait(false) is { } reason) {
        outcomes.Add(new WorkTableVacuum(table, false, reason));
        continue;
      }
      if (skipVacuumedWithin > TimeSpan.Zero
          && await _vacuumedSecondsAgoAsync(conn, qualified, skipVacuumedWithin, cancellationToken).ConfigureAwait(false) is { } ago) {
        outcomes.Add(new WorkTableVacuum(table, false, string.Create(System.Globalization.CultureInfo.InvariantCulture,
          $"{table} was vacuumed {ago:F0} s ago, within the {skipVacuumedWithin.TotalSeconds:F0} s window")) { RecentlyVacuumed = true });
        continue;
      }
      await VacuumAnalyzeAsync(conn, qualified, cancellationToken).ConfigureAwait(false);
      outcomes.Add(new WorkTableVacuum(table, true, null));
    }
    return outcomes;
  }

  /// <summary>
  /// How many seconds ago the database last vacuumed the table, manually or by autovacuum, when that is inside
  /// <paramref name="window"/>; null otherwise (never vacuumed, or longer ago). The statistics view records it for every
  /// instance, so this is the fleet's memory of the last vacuum.
  /// </summary>
  private static async Task<double?> _vacuumedSecondsAgoAsync(
      DbConnection conn, string qualified, TimeSpan window, CancellationToken cancellationToken) {
    await using var cmd = CoordinatorCommandTimeout.Apply(conn.CreateCommand());
    cmd.CommandText = """
      SELECT EXTRACT(EPOCH FROM now() - GREATEST(s.last_vacuum, s.last_autovacuum))::DOUBLE PRECISION
        FROM pg_stat_user_tables s
       WHERE s.relid = to_regclass(@q)::OID
         AND GREATEST(s.last_vacuum, s.last_autovacuum) > now() - @window
      """;
    cmd.Parameters.Add(new NpgsqlParameter("q", qualified));
    cmd.Parameters.Add(new NpgsqlParameter(nameof(window), window));
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as double?;
  }

  /// <summary><c>VACUUM ANALYZE</c> of one work table, bounded by <see cref="WORK_TABLE_VACUUM_TIMEOUT_SECONDS"/>.</summary>
  internal static async Task VacuumAnalyzeAsync(DbConnection conn, string qualified, CancellationToken cancellationToken) {
    await using var vacuum = conn.CreateCommand();
    vacuum.CommandText = $"VACUUM ANALYZE {qualified}";
    vacuum.CommandTimeout = WORK_TABLE_VACUUM_TIMEOUT_SECONDS;
    await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Checks the prerequisites a squeeze needs on the connection, then squeezes and analyzes the table; returns the first
  /// unmet prerequisite instead, having run nothing. <c>squeeze_table</c> waits for its background worker, so it has no timeout.
  /// </summary>
  internal static async Task<string?> SqueezeAndAnalyzeAsync(
      DbConnection conn, string qualified, string tableName,
      Func<PgSqueezePrerequisiteFacts, string, string?> prerequisitesUnmet, CancellationToken cancellationToken) {
    var facts = await PgSqueezePrerequisites.ReadAsync(conn, qualified, cancellationToken).ConfigureAwait(false);
    if (prerequisitesUnmet(facts, tableName) is { } unmet) {
      return unmet;
    }
    await using (var squeeze = conn.CreateCommand()) {
      squeeze.CommandText = """
        SELECT squeeze.squeeze_table(n.nspname, c.relname, NULL, NULL, NULL)
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.oid = to_regclass(@q)
        """;
      squeeze.Parameters.Add(new NpgsqlParameter("q", qualified));
      squeeze.CommandTimeout = 0;   // the worker copies the whole table; the budget bounds how much per cycle
      await squeeze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    await AnalyzeAsync(conn, qualified, cancellationToken).ConfigureAwait(false);
    return null;
  }

  /// <summary>The queue-index survey (<see cref="QueueIndexSql.SURVEY"/>): one catalog read, nothing scans an index.</summary>
  internal static async Task<IReadOnlyList<QueueIndexBloat>> QueueIndexSurveyAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = QueueIndexSql.SURVEY;
    cmd.Parameters.Add(new NpgsqlParameter(nameof(prefix), prefix));
    cmd.Parameters.Add(new NpgsqlParameter("tables", QueueIndexSql.TABLES));
    var results = new List<QueueIndexBloat>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new QueueIndexBloat(
          reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)) {
        Valid = reader.GetBoolean(4),
        BuildInProgress = reader.GetBoolean(5),
        Method = BloatMeasurementMethod.Estimate,
      });
    }
    return results;
  }

  /// <summary>The queue table <paramref name="indexName"/> indexes and whether it is valid; a null table when it is not one.</summary>
  internal static async Task<(string? Table, bool Valid)> QueueIndexLookupAsync(
      DbConnection conn, string prefix, string indexName, CancellationToken cancellationToken) {
    await using var cmd = CoordinatorCommandTimeout.Apply(conn.CreateCommand());
    cmd.CommandText = QueueIndexSql.LOOKUP;
    cmd.Parameters.Add(new NpgsqlParameter(nameof(prefix), prefix));
    cmd.Parameters.Add(new NpgsqlParameter("tables", QueueIndexSql.TABLES));
    cmd.Parameters.Add(new NpgsqlParameter("index", indexName));
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
      ? (reader.GetString(0), reader.GetBoolean(1))
      : (null, false);
  }

  /// <summary>Why the index may not be touched, or null when it may: it must be on a queue table and of the validity wanted.</summary>
  internal static string? QueueIndexRefusal(string? table, bool valid, bool wantValid) => (table, valid, wantValid) switch {
    (null, _, _) => "it is not an index on a queue table",
    _ when valid == wantValid => null,
    (_, _, true) => "it is invalid; it is dropped, not rebuilt",
    _ => "it is valid; only an invalid leftover is dropped",
  };

  /// <summary>
  /// <c>DROP INDEX CONCURRENTLY</c> of an index the catalog confirmed. It waits for the transactions using the table to
  /// finish, as a concurrent drop must, so it has no timeout.
  /// </summary>
  internal static async Task<bool> DropIndexConcurrentlyAsync(DbConnection conn, string qualified, CancellationToken cancellationToken) {
    await using var drop = conn.CreateCommand();
    drop.CommandText = $"DROP INDEX CONCURRENTLY IF EXISTS {qualified}";
    drop.CommandTimeout = 0;
    await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    return true;
  }

  /// <summary>
  /// <c>REINDEX INDEX CONCURRENTLY</c> of an index the catalog confirmed, then its size. It builds a copy while the table
  /// stays readable and writable, so it has no timeout: one index per cycle bounds it.
  /// </summary>
  internal static async Task<long?> ReindexConcurrentlyAsync(DbConnection conn, string qualified, CancellationToken cancellationToken) {
    await using (var reindex = conn.CreateCommand()) {
      reindex.CommandText = $"REINDEX INDEX CONCURRENTLY {qualified}";
      reindex.CommandTimeout = 0;
      await reindex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    await using var size = CoordinatorCommandTimeout.Apply(conn.CreateCommand());
    size.CommandText = "SELECT pg_relation_size(to_regclass(@q))";
    size.Parameters.Add(new NpgsqlParameter("q", qualified));
    return (long)(await size.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  private static async Task _tableFunctionAsync(DbCommand cmd, string function, string tableName, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {function}(@t)";
    cmd.Parameters.Add(new NpgsqlParameter("t", tableName));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }
}
