using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Data.EFCore.Postgres.Observability;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ITableStatisticsProvider"/>.
/// Uses pg_stat_user_tables + pg_total_relation_size for table sizes
/// and partial-index COUNT for queue depths. Zero table scans.
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresTableStatisticsProviderTests.cs</tests>
public sealed partial class PostgresTableStatisticsProvider(
  NpgsqlDataSource dataSource,
  string schema = "public") : ITableStatisticsProvider {

  private static readonly string[] _trackedTables = [
    "wh_inbox", "wh_outbox", "wh_event_store", "wh_active_streams",
    "wh_perspective_events", "wh_perspective_cursors", "wh_perspective_snapshots"
  ];

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// Heap bytes per live row over the width those rows should need. The expected width comes
  /// from <c>pg_stats.avg_width</c> (planner statistics, already maintained by autoanalyze) plus
  /// per-tuple overhead, so this costs nothing beyond a catalog read — unlike pgstattuple, which
  /// is exact but scans the table and needs an extension that managed Postgres may not allow.
  /// </para>
  /// <para>
  /// Around 1.0 means the heap is about the size its rows need. A sustained large multiple means
  /// space that cannot be used: dead tuples awaiting vacuum, or — the case autovacuum can never
  /// fix — bytes left behind by a dropped column, which persist in every row written before the
  /// drop until the table is rewritten.
  /// </para>
  /// <para>
  /// Small tables are excluded: with few rows the per-row average is dominated by page overhead
  /// and reports alarming ratios for tables measured in kilobytes.
  /// </para>
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
    // 162: the inbox depth gauge counts unprocessed rows, and processed_at is work state.
    var inboxTable = $"{schema}.wh_inbox_state";
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

  /// <inheritdoc/>
  /// <remarks>
  /// Read from the same catalog view the sizes come from, so this costs another cheap statement on
  /// the maintenance cadence rather than anything that touches the tables themselves.
  /// </remarks>
  public async Task<IReadOnlyDictionary<string, TableScanStatistics>> GetTableScanStatisticsAsync(
      CancellationToken ct = default) {
    var statistics = new Dictionary<string, TableScanStatistics>(StringComparer.Ordinal);

    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
      SELECT relname,
             COALESCE(seq_scan, 0) AS seq_scans,
             COALESCE(seq_tup_read, 0) AS seq_rows,
             COALESCE(idx_scan, 0) AS idx_scans
      FROM pg_stat_user_tables
      WHERE schemaname = @schema
      """, connection);

    cmd.Parameters.AddWithValue("schema", schema);

    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      statistics[reader.GetString(0)] =
        new TableScanStatistics(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    return statistics;
  }

  /// <summary>
  /// Adds the document filters one recorded statement names, to the table it reads.
  /// </summary>
  /// <remarks>
  /// Separated from the read so that what it understands can be established without a database.
  /// What it reads is the tag the naming interceptor writes, because a recorded statement has had
  /// its constants replaced by placeholders and a JSON key is a constant; see
  /// <see cref="DocumentFilterNaming"/>.
  /// </remarks>
  /// <param name="collected">The filters found so far, by table.</param>
  /// <param name="statement">The recorded statement text.</param>
  /// <param name="calls">How many times it ran.</param>
  /// <param name="meanMilliseconds">Its mean execution time.</param>
  internal static void Collect(
      Dictionary<string, List<ExpensivePredicate>> collected,
      string statement, long calls, double meanMilliseconds) {
    var table = DocumentFilterNaming.PerspectiveTable().Match(statement);
    if (!table.Success) {
      return;
    }

    var name = table.Value.ToLowerInvariant();
    if (!collected.TryGetValue(name, out var predicates)) {
      predicates = [];
      collected[name] = predicates;
    }

    foreach (var (document, field) in DocumentFilterNaming.FiltersIn(statement)) {
      // The same field named twice in one statement is one filter, and the same field across
      // statements is still one thing to promote, so the dearest sighting is the one kept -- which
      // is the first seen, because the statements arrive dearest first.
      if (!predicates.Exists(p => p.Document == document && p.Field == field)) {
        predicates.Add(new ExpensivePredicate(document, field, calls, meanMilliseconds));
      }
    }
  }

  /// <inheritdoc/>
  /// <remarks>
  /// Answers empty rather than throwing when the statistics are not collected. The extension has to
  /// be installed AND loaded at startup, and a deployment may have done neither; naming the filter
  /// is an improvement on the advice rather than a condition of it, so its absence is not a fault.
  /// </remarks>
  public async Task<IReadOnlyDictionary<string, IReadOnlyList<ExpensivePredicate>>> GetExpensivePredicatesAsync(
      CancellationToken ct = default) {
    var byTable = new Dictionary<string, IReadOnlyList<ExpensivePredicate>>(StringComparer.Ordinal);

    await using var connection = await dataSource.OpenConnectionAsync(ct);

    try {
      await using var cmd = new NpgsqlCommand("""
        SELECT query, calls, mean_exec_time
        FROM pg_stat_statements
        WHERE query ILIKE '%wh\_per\_%' AND query LIKE '%->%'
        ORDER BY mean_exec_time DESC
        LIMIT 200
        """, connection);

      await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
      var collected = new Dictionary<string, List<ExpensivePredicate>>(StringComparer.Ordinal);

      while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
        var statement = reader.GetString(0);
        var calls = reader.GetInt64(1);
        var mean = reader.GetDouble(2);

        Collect(collected, statement, calls, mean);
      }

      foreach (var (table, predicates) in collected) {
        byTable[table] = predicates;
      }
    } catch (PostgresException) {
      // Not installed, not loaded, or not readable by this role. The table-level advice stands on
      // its own and this only ever made it sharper.
      return byTable;
    }

    return byTable;
  }
}
