using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ITableStatisticsProvider"/>.
/// Uses pg_stat_user_tables + pg_total_relation_size for table sizes
/// and partial-index COUNT for queue depths. Zero table scans.
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresTableStatisticsProviderTests.cs</tests>
public sealed class PostgresTableStatisticsProvider(
  NpgsqlDataSource dataSource,
  string schema = "public") : ITableStatisticsProvider {

  private static readonly string[] _trackedTables = [
    "wh_inbox", "wh_outbox", "wh_event_store", "wh_active_streams",
    "wh_perspective_events", "wh_perspective_cursors", "wh_perspective_snapshots"
  ];

  /// <inheritdoc />
  /// <remarks>
  /// Heap bytes per live row over the width those rows should need. The expected width comes
  /// from <c>pg_stats.avg_width</c> (planner statistics, already maintained by autoanalyze) plus
  /// per-tuple overhead, so this costs nothing beyond a catalog read — unlike pgstattuple, which
  /// is exact but scans the table and needs an extension that managed Postgres may not allow.
  ///
  /// Around 1.0 means the heap is about the size its rows need. A sustained large multiple means
  /// space that cannot be used: dead tuples awaiting vacuum, or — the case autovacuum can never
  /// fix — bytes left behind by a dropped column, which persist in every row written before the
  /// drop until the table is rewritten.
  ///
  /// Small tables are excluded: with few rows the per-row average is dominated by page overhead
  /// and reports alarming ratios for tables measured in kilobytes.
  /// </remarks>
  public async Task<IReadOnlyDictionary<string, double>> GetTableBloatRatiosAsync(CancellationToken ct = default) {
    var results = new Dictionary<string, double>();

    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
      SELECT st.relname,
             (pg_relation_size(st.relid)::numeric / st.n_live_tup) / GREATEST(w.expected, 1)
      FROM pg_stat_user_tables st
      JOIN LATERAL (
        SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
        FROM pg_stats s
        WHERE s.schemaname = st.schemaname AND s.tablename = st.relname
      ) w ON TRUE
      WHERE st.schemaname = @schema
        AND st.relname = ANY(@tables)
        AND st.n_live_tup > 1000
      """, connection);

    cmd.Parameters.AddWithValue("schema", schema);
    cmd.Parameters.AddWithValue("tables", _trackedTables);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      results[reader.GetString(0)] = (double)reader.GetDecimal(1);
    }
    return results;
  }

  public async Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) {
    var results = new Dictionary<string, long>();

    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
      SELECT relname, pg_total_relation_size(relid) as size_bytes
      FROM pg_stat_user_tables
      WHERE schemaname = @schema
        AND relname = ANY(@tables)
      """, connection);

    cmd.Parameters.AddWithValue("schema", schema);
    cmd.Parameters.AddWithValue("tables", _trackedTables);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      results[reader.GetString(0)] = reader.GetInt64(1);
    }

    return results;
  }

  public async Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) {
    var results = new Dictionary<string, long>();

    await using var connection = await dataSource.OpenConnectionAsync(ct);

    // Schema-qualify table names for multi-schema deployments
    var inboxTable = $"{schema}.wh_inbox";
    var outboxTable = $"{schema}.wh_outbox";
    var deadLettersTable = $"{schema}.wh_dead_letters";

    // Dead letters are a queue like any other, sliced by status because the three
    // populations mean three different things to an operator: held is quarantine awaiting
    // a verdict, pending is the recovery backlog, failed is the operator-decision pile.
    // Emitted even at zero — "no quarantine" must be a positively-reported value, not an
    // absent series (#683: only hold TRANSITIONS were counted, so a standing five-figure
    // held population was invisible while the services that happened to be transitioning
    // were the only ones charted). Recovered rows are receipts, not depth.
    await using var cmd = new NpgsqlCommand($"""
      SELECT 'inbox' as queue_name, COUNT(*) as depth FROM {inboxTable} WHERE processed_at IS NULL
      UNION ALL
      SELECT 'outbox', COUNT(*) FROM {outboxTable} WHERE processed_at IS NULL
      UNION ALL
      SELECT 'dead_letters_held', COUNT(*) FROM {deadLettersTable} WHERE recovery_status = 2
      UNION ALL
      SELECT 'dead_letters_pending', COUNT(*) FROM {deadLettersTable} WHERE recovery_status = 0 AND recovered_at IS NULL
      UNION ALL
      SELECT 'dead_letters_failed', COUNT(*) FROM {deadLettersTable} WHERE recovery_status = 4
      """, connection);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      results[reader.GetString(0)] = reader.GetInt64(1);
    }

    return results;
  }
}
