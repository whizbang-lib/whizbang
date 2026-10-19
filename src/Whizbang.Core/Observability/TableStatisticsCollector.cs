// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Background service that periodically queries <see cref="ITableStatisticsProvider"/>
/// and updates <see cref="TableStatisticsMetrics"/> caches.
/// Runs every 30 seconds. Waits for database readiness before starting. Registered by
/// <see cref="TableStatisticsServiceCollectionExtensions.AddWhizbangTableStatistics"/>, which both
/// PostgreSQL drivers call.
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/TableStatisticsCollectorTests.cs</tests>
public sealed partial class TableStatisticsCollector(
  IServiceScopeFactory scopeFactory,
  TableStatisticsMetrics metrics,
  Whizbang.Core.Workers.ISchemaReadyGate schemaReadyGate,
  ILogger<TableStatisticsCollector> logger
) : BackgroundService {

  private readonly ILogger<TableStatisticsCollector> _logger =
    logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TableStatisticsCollector>.Instance;

  /// <summary>
  /// Holds the advisory across cycles. With the per-process ledger that is what makes "once" hold at
  /// all; with a durable one it saves rebuilding a type whose state now lives in the database.
  /// </summary>
  /// <remarks>
  /// Created on the first cycle from the scope's logger factory rather than injected, so the advisory
  /// keeps its own log category without this collector growing another optional injected dependency.
  /// </remarks>
  private QueryExposureAdvisory? _exposureAdvisory;

  private const int COLLECTION_INTERVAL_SECONDS = 30;

  /// <summary>
  /// Raised once every collection cycle, after every metric cache has been written.
  /// </summary>
  /// <remarks>
  /// Exists so an observer can tell that a cycle's values have actually landed: a health check that
  /// wants to know the statistics are current, or a test waiting for the first reading. Watching the
  /// provider instead only proves the collector asked; the writes happen after the provider returns,
  /// so anything keyed on the request races the update it is waiting for. Not raised for a cycle that
  /// failed, which is logged instead.
  /// </remarks>
  /// <docs>operations/observability/metrics#table-statistics</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Observability/TableStatisticsCollectorTests.cs:Collector_PublishesTableBloatRatioAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperTableStatisticsTests.cs:ADapperHost_ReportsTableStatisticsAsync</tests>
  public event Action? CycleCompleted;

  /// <inheritdoc/>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Wait for schema readiness (replaces IDatabaseReadinessCheck — same intent: don't query
    // statistics tables before migrations have created them).
    if (schemaReadyGate is not null) {
      try {
        await schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetService<ITableStatisticsProvider>();
        if (provider is null) {
          LogProviderNotRegistered(_logger);
          return;
        }

        // One reading for the tables: sizes, their split, rows and bloat together, so a cycle reads the
        // catalog once for every table rather than once per kind of figure.
        var (storage, sizes, bloat) = await _readTablesAsync(provider, stoppingToken);
        metrics.UpdateTableSizes(sizes);
        metrics.UpdateTableStorage(storage);

        // The sizes are already in hand, and they are the half WHIZ306 could not know at build time:
        // an exposed perspective of a few hundred rows is fine, and the same exposure over several
        // gigabytes is the most expensive query shape there is. Reported here rather than from its own
        // cycle so it costs one dictionary walk instead of a second round trip.
        // The ledger decides how long "already said this" lasts. Falling back to the per-process one
        // HERE rather than defaulting it inside the advisory, because that choice is a property of
        // how the host is wired and belongs where the wiring is visible: a driver that registers a
        // durable ledger gets advice once per finding across the fleet, and a host with no store
        // gets it once per process, which is all it can offer.
        _exposureAdvisory ??= new QueryExposureAdvisory(
          scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<QueryExposureAdvisory>()
          ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<QueryExposureAdvisory>.Instance,
          scope.ServiceProvider.GetService<IAdvisoryLedger>() ?? new AdvisoryLedger());
        var findings = await _exposureAdvisory.ReportAsync(
          sizes,
          scope.ServiceProvider.GetService<Whizbang.Core.Perspectives.ICollectiveSiblingTableSource>(),
          cancellationToken: stoppingToken);

        // Emitted in its own method rather than inline: keeping the loop and its error handling out
        // of this method keeps this one readable, and the findings are already suppressed by the
        // time they arrive, so the log line and the emitted record cannot disagree about what was
        // reported.
        await EmitFindingsAsync(
          findings,
          scope.ServiceProvider.GetService<Whizbang.Core.SystemEvents.ISystemEventEmitter>(),
          _logger,
          stoppingToken);

        var depths = await provider.GetQueueDepthsAsync(stoppingToken);
        metrics.UpdateQueueDepths(depths);

        // Space a table holds but cannot use is invisible without being asked for: dead tuples
        // awaiting vacuum, or a dropped column whose bytes Postgres keeps in every pre-existing
        // row until the table is rewritten. Publishing the ratio means an operator is told
        // rather than having to go looking.
        metrics.UpdateTableBloat(bloat);

        CycleCompleted?.Invoke();
      } catch (ObjectDisposedException) {
        break;  // Host is shutting down — exit the collection loop
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogCollectionError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromSeconds(COLLECTION_INTERVAL_SECONDS), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>
  /// Reads the tables: the storage reading, with the sizes and bloat ratios taken from it, or, from a
  /// provider that offers no storage reading, the sizes and bloat ratios asked for separately.
  /// </summary>
  private static async Task<(
      IReadOnlyDictionary<string, TableStorageStatistics> Storage,
      IReadOnlyDictionary<string, long> Sizes,
      IReadOnlyDictionary<string, double> Bloat)> _readTablesAsync(
      ITableStatisticsProvider provider, CancellationToken cancellationToken) {
    var storage = await provider.GetTableStorageAsync(cancellationToken);
    if (storage.Count == 0) {
      // A provider written before the storage reading existed reports what it always reported.
      return (storage,
        await provider.GetEstimatedTableSizesAsync(cancellationToken),
        await provider.GetTableBloatRatiosAsync(cancellationToken));
    }

    return (storage,
      storage.ToDictionary(table => table.Key, table => table.Value.TotalBytes, StringComparer.Ordinal),
      storage
        .Where(table => table.Value.BloatRatio.HasValue)
        .ToDictionary(table => table.Key, table => table.Value.BloatRatio.GetValueOrDefault(), StringComparer.Ordinal));
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "ITableStatisticsProvider not registered — table statistics collection disabled")]
  static partial void LogProviderNotRegistered(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Error collecting table statistics — will retry")]
  static partial void LogCollectionError(ILogger logger, Exception exception);

  /// <summary>
  /// Emits each finding, or logs and carries on when one cannot be emitted.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The emitter is optional and its absence degrades to the log line the advisory already wrote.
  /// That is the opposite of the advisory's own logger, which is required, and the difference is
  /// worth keeping straight: without a logger the finding is lost, while without an emitter it is
  /// still reported -- just not routable.
  /// </para>
  /// <para>
  /// A failed emission is caught rather than allowed to end the statistics cycle. The queue depths
  /// and the bloat ratio measured after it are unrelated to an advisory, which is the least
  /// important thing that loop does.
  /// </para>
  /// </remarks>
  internal static async Task EmitFindingsAsync(
      IReadOnlyList<Whizbang.Core.SystemEvents.PerspectiveIndexAdvised> findings,
      Whizbang.Core.SystemEvents.ISystemEventEmitter? emitter,
      ILogger logger,
      CancellationToken cancellationToken) {
    if (emitter is null || findings is null || findings.Count == 0) {
      return;
    }

    foreach (var finding in findings) {
      try {
        await emitter.EmitAsync(finding, cancellationToken);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogAdvisoryEmitFailed(logger, ex);
      }
    }
  }

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Could not emit a perspective index advisory — the finding is in the log above, but nothing downstream was told")]
  static partial void LogAdvisoryEmitFailed(ILogger logger, Exception exception);
}
