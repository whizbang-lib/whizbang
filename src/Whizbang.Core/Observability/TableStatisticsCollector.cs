using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Background service that periodically queries <see cref="ITableStatisticsProvider"/>
/// and updates <see cref="TableStatisticsMetrics"/> caches.
/// Runs every 30 seconds. Waits for database readiness before starting.
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/TableStatisticsCollectorTests.cs</tests>
public sealed partial class TableStatisticsCollector(
  IServiceScopeFactory scopeFactory,
  TableStatisticsMetrics metrics,
  Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate = null,
  ILogger<TableStatisticsCollector>? logger = null
) : BackgroundService {

  private readonly ILogger<TableStatisticsCollector> _logger =
    logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TableStatisticsCollector>.Instance;

  private const int COLLECTION_INTERVAL_SECONDS = 30;

  /// <summary>
  /// Raised once every collection cycle, AFTER all three metric caches have been written.
  /// </summary>
  /// <remarks>
  /// Exists so an observer can tell that a cycle's values have actually landed. Watching the
  /// provider instead only proves the collector asked — the writes happen after the provider
  /// returns, so anything keyed on the request races the update it is waiting for.
  /// </remarks>
  internal event Action? CycleCompleted;

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

        var sizes = await provider.GetEstimatedTableSizesAsync(stoppingToken);
        metrics.UpdateTableSizes(sizes);

        var depths = await provider.GetQueueDepthsAsync(stoppingToken);
        metrics.UpdateQueueDepths(depths);

        // Space a table holds but cannot use is invisible without being asked for: dead tuples
        // awaiting vacuum, or a dropped column whose bytes Postgres keeps in every pre-existing
        // row until the table is rewritten. Publishing the ratio means an operator is told
        // rather than having to go looking.
        var bloat = await provider.GetTableBloatRatiosAsync(stoppingToken);
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

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "ITableStatisticsProvider not registered — table statistics collection disabled")]
  static partial void LogProviderNotRegistered(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Error collecting table statistics — will retry")]
  static partial void LogCollectionError(ILogger logger, Exception exception);
}
