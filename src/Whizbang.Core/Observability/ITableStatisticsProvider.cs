namespace Whizbang.Core.Observability;

/// <summary>
/// Provides estimated table sizes and queue depths for Whizbang infrastructure tables.
/// Implementations use database-specific catalog queries
/// (e.g., PostgreSQL pg_stat_user_tables + pg_total_relation_size).
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsCollectorBranchTests.cs:ProviderRegistered_PopulatesMetricsThenWaitsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsCollectorBranchTests.cs:ProviderThrows_LogsAndContinuesLoopAsync</tests>
public interface ITableStatisticsProvider {
  /// <summary>
  /// Returns estimated disk size in bytes per table name.
  /// Uses database catalog statistics — no table scans.
  /// </summary>
  Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default);

  /// <summary>
  /// Returns unprocessed message count per queue (inbox, outbox).
  /// Uses partial index scans — cheap on indexed columns.
  /// </summary>
  Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default);

  /// <summary>
  /// Returns a per-table bloat ratio: heap bytes per live row divided by the expected row width.
  /// Roughly 1.0 means the heap is about the size its rows need; a large sustained multiple means
  /// the table is carrying space it cannot use — dead tuples awaiting vacuum, or bytes from a
  /// dropped column, which Postgres keeps in every pre-existing row until the table is rewritten.
  /// </summary>
  /// <remarks>
  /// Defaults to empty so providers that cannot estimate this keep working unchanged; the gauge
  /// simply reports nothing for them.
  /// </remarks>
  Task<IReadOnlyDictionary<string, double>> GetTableBloatRatiosAsync(CancellationToken ct = default) =>
    Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
}
