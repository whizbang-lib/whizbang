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

  /// <summary>
  /// Returns, per table, how much reading has been done by sequential scan rather than by index.
  /// </summary>
  /// <remarks>
  /// <para>
  /// What a filter costs cannot be told from the shape of the model. A perspective with no index on
  /// the field it is filtered by is free while it holds two hundred rows and is a third of a
  /// service's database time when it holds two million, and nothing about the declaration changes
  /// in between. This is the counter that does change.
  /// </para>
  /// <para>
  /// Defaults to empty so an engine that does not keep these counters reports nothing rather than
  /// failing. An advisory built on it then simply has nothing to say, which is the correct answer
  /// where the measurement does not exist.
  /// </para>
  /// </remarks>
  /// <param name="ct">Cancels the read.</param>
  /// <returns>The scan statistics per table name.</returns>
  Task<IReadOnlyDictionary<string, TableScanStatistics>> GetTableScanStatisticsAsync(CancellationToken ct = default) =>
    Task.FromResult<IReadOnlyDictionary<string, TableScanStatistics>>(new Dictionary<string, TableScanStatistics>());
}

/// <summary>
/// How much of a table's reading has been done by sequential scan, and how much by index.
/// </summary>
/// <param name="SequentialScans">Sequential scans started since the counters were last reset.</param>
/// <param name="SequentialRowsRead">
/// Live rows returned by those scans. This is the number that says what the scanning cost, because
/// a sequential scan of a small table is cheap however often it happens.
/// </param>
/// <param name="IndexScans">Index scans started, which is what the sequential ones are weighed against.</param>
/// <docs>operations/observability/metrics#table-statistics</docs>
public readonly record struct TableScanStatistics(
    long SequentialScans, long SequentialRowsRead, long IndexScans);
