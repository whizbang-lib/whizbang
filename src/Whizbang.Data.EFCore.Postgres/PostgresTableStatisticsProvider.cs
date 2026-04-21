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

    await using var cmd = new NpgsqlCommand($"""
      SELECT 'inbox' as queue_name, COUNT(*) as depth FROM {inboxTable} WHERE processed_at IS NULL
      UNION ALL
      SELECT 'outbox', COUNT(*) FROM {outboxTable} WHERE processed_at IS NULL
      """, connection);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      results[reader.GetString(0)] = reader.GetInt64(1);
    }

    return results;
  }
}
