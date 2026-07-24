namespace Whizbang.Core.Observability;

/// <summary>
/// Provides estimated table sizes and queue depths for Whizbang infrastructure tables.
/// Implementations use database-specific catalog queries
/// (e.g., PostgreSQL pg_stat_user_tables + pg_total_relation_size).
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
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
}
