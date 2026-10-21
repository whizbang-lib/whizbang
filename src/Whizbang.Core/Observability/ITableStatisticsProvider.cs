// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>
/// Provides estimated table sizes and queue depths for Whizbang infrastructure tables.
/// Implementations use database-specific catalog queries
/// (e.g., PostgreSQL pg_stat_user_tables + pg_total_relation_size).
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/TableStatisticsCollectorBranchTests.cs:ProviderRegistered_PopulatesMetricsThenWaitsAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/TableStatisticsCollectorBranchTests.cs:ProviderThrows_LogsAndContinuesLoopAsync</tests>
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
  /// Returns, per table, what its size is made of and how many rows it holds, from one catalog read.
  /// </summary>
  /// <remarks>
  /// <para>
  /// One reading answers the size, the split of that size into heap, TOAST and indexes, the live row
  /// count and the bloat ratio for every table, so a refresh costs one catalog statement however many
  /// tables the schema holds. A provider that implements this is asked for nothing else about tables
  /// on a refresh: the sizes and bloat ratios are taken from it.
  /// </para>
  /// <para>
  /// The number of tables answered is bounded by the provider. Tables past its cap, smallest first,
  /// are summed under <see cref="TableStatisticsOptions.OTHER_TABLES"/>, so the totals are preserved
  /// while the number of series is not allowed to grow with the schema.
  /// </para>
  /// <para>
  /// Defaults to empty, which tells the collector to ask the older per-reading methods instead, so a
  /// provider written before this existed keeps reporting exactly what it reported.
  /// </para>
  /// </remarks>
  /// <param name="ct">Cancels the read.</param>
  /// <returns>The storage reading per table name, or empty where the provider does not offer one.</returns>
  /// <docs>operations/observability/metrics#table-statistics</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsContractTests.cs:AProviderWithoutTheMeasuredReadings_AnswersEmptyRatherThanFailingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresTableStatisticsProviderTests.cs:GetTableStorageAsync_SplitsEachTableIntoHeapToastAndIndexesAsync</tests>
  Task<IReadOnlyDictionary<string, TableStorageStatistics>> GetTableStorageAsync(CancellationToken ct = default) =>
    Task.FromResult<IReadOnlyDictionary<string, TableStorageStatistics>>(
      new Dictionary<string, TableStorageStatistics>());

  /// <summary>
  /// Samples the event store and reports its stored bytes by event type, when this instance takes the
  /// window's claim.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The table sizes say how large the event store is; this says which event types the bytes belong to,
  /// and how much of each event is payload and how much is metadata. That is what decides whether a type
  /// should be excluded from audit, marked ephemeral or slimmed, a decision otherwise made with ad-hoc SQL.
  /// </para>
  /// <para>
  /// Reading every row would cost as much as the table is large, so the reading is a sample of the table's
  /// pages, and the totals are extrapolated from it. One instance of the fleet takes it per window: the
  /// claim is <see cref="EventTypeSampleRequest.ClaimKey"/>, and an instance that does not get it answers
  /// null without reading anything.
  /// </para>
  /// <para>Defaults to null, so a provider that does not sample reports nothing.</para>
  /// </remarks>
  /// <param name="request">The window's claim, the share of the table to read, and the cap on types.</param>
  /// <param name="ct">Cancels the read.</param>
  /// <returns>
  /// The sample per event type, or null where another instance holds the window or the provider does not sample.
  /// </returns>
  /// <docs>operations/observability/metrics#event-type-bytes</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresTableStatisticsProviderTests.cs:SampleEventTypeBytesAsync_AtAFullSample_ReportsEachTypesBytesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsContractTests.cs:AProviderThatDoesNotSample_AnswersNullAsync</tests>
  Task<IReadOnlyDictionary<string, EventTypeBytes>?> SampleEventTypeBytesAsync(
      EventTypeSampleRequest request, CancellationToken ct = default) =>
    Task.FromResult<IReadOnlyDictionary<string, EventTypeBytes>?>(null);

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

  /// <summary>
  /// Returns, per table, the document filters the database has measured as expensive.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Where the engine records statement statistics, the advice can name the filter rather than only
  /// the table. Scan counters say reading is happening; this says what was reading.
  /// </para>
  /// <para>
  /// Defaults to empty, and an implementation is expected to answer empty rather than throw when
  /// the statistics are not collected. Naming the filter is an improvement on the advice, never a
  /// condition of it, so an engine without it still advises on the table.
  /// </para>
  /// </remarks>
  /// <param name="ct">Cancels the read.</param>
  /// <returns>The measured filters per table name, or empty where none are recorded.</returns>
  Task<IReadOnlyDictionary<string, IReadOnlyList<ExpensivePredicate>>> GetExpensivePredicatesAsync(
      CancellationToken ct = default) =>
    Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ExpensivePredicate>>>(
      new Dictionary<string, IReadOnlyList<ExpensivePredicate>>());
}

/// <summary>
/// A filter the database has measured as expensive, and the field it reads.
/// </summary>
/// <remarks>
/// This is what turns "look at this perspective" into "promote this property". The scan counters
/// say a table is being read through; this says which filter was doing the reading, which is the
/// difference between a finding someone has to investigate and one they can act on.
/// </remarks>
/// <param name="Document">The stored document the field is read out of: data, metadata or scope.</param>
/// <param name="Field">The field read out of it, as the filter names it.</param>
/// <param name="Calls">How many times the statement ran over the measured window.</param>
/// <param name="MeanMilliseconds">Its mean execution time, which is what makes it worth naming.</param>
/// <docs>operations/diagnostics/whiz306</docs>
public readonly record struct ExpensivePredicate(
    string Document, string Field, long Calls, double MeanMilliseconds);

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

/// <summary>
/// What one table's size is made of, and how many rows it holds.
/// </summary>
/// <remarks>
/// The three byte components add up to <see cref="TotalBytes"/>, which is the size
/// <c>whizbang.table.estimated_bytes</c> reports, so a dashboard can show the split without the total
/// changing meaning.
/// </remarks>
/// <param name="HeapBytes">The table's own pages: the rows stored inline, plus its free space and visibility maps.</param>
/// <param name="ToastBytes">
/// Values too large to store inline (large JSON documents, long strings), kept in the table's TOAST
/// relation together with that relation's index.
/// </param>
/// <param name="IndexBytes">Every index on the table.</param>
/// <param name="LiveRows">
/// The planner's estimate of the rows the table holds, refreshed by every <c>ANALYZE</c> and
/// <c>VACUUM</c>, which autovacuum runs on its own.
/// </param>
/// <param name="BloatRatio">
/// Heap bytes per live row over the width those rows should need, or null where the table is too
/// small for the ratio to mean anything. See <see cref="ITableStatisticsProvider.GetTableBloatRatiosAsync"/>.
/// </param>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsStorageTests.cs:TotalBytes_IsTheSumOfTheThreeComponentsAsync</tests>
public readonly record struct TableStorageStatistics(
    long HeapBytes, long ToastBytes, long IndexBytes, long LiveRows, double? BloatRatio) {
  /// <summary>The table's whole size on disk: heap, TOAST and indexes together.</summary>
  public long TotalBytes => HeapBytes + ToastBytes + IndexBytes;
}

/// <summary>What one sample of the event store by event type reads.</summary>
/// <param name="ClaimKey">The window's claim; the instance that takes it samples, the others do not.</param>
/// <param name="SamplePercent">The share of the table's pages read, greater than 0 and at most 100.</param>
/// <param name="MaxEventTypes">
/// The most event types reported by name, at least 1; the rest are summed under
/// <see cref="TableStatisticsOptions.OTHER_EVENT_TYPES"/>.
/// </param>
/// <docs>operations/observability/metrics#event-type-bytes</docs>
public readonly record struct EventTypeSampleRequest(string ClaimKey, double SamplePercent, int MaxEventTypes) {
  /// <summary>The bytes the whole table holds, estimated from the bytes the sample read.</summary>
  /// <param name="sampledBytes">The bytes the sample read.</param>
  /// <returns>The sampled bytes scaled up by the share of the table that was not read.</returns>
  public long Extrapolate(long sampledBytes) => 0;
}

/// <summary>The stored bytes of one event type, from a sample of the event store.</summary>
/// <param name="SampledRows">How many of the type's events the sample read.</param>
/// <param name="MeanDataBytes">The mean stored size of the payload, after any compression.</param>
/// <param name="MeanMetadataBytes">The mean stored size of the metadata, after any compression.</param>
/// <param name="EstimatedTotalBytes">
/// The payload and metadata bytes of every event of the type, extrapolated from the share sampled.
/// </param>
/// <docs>operations/observability/metrics#event-type-bytes</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsMetricsEventTypeTests.cs</tests>
public readonly record struct EventTypeBytes(
    long SampledRows, double MeanDataBytes, double MeanMetadataBytes, long EstimatedTotalBytes);
