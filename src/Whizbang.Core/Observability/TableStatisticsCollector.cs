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
  IDatabaseReadinessCheck? databaseReadinessCheck = null,
  ILogger<TableStatisticsCollector>? logger = null
) : BackgroundService {

  private readonly ILogger<TableStatisticsCollector> _logger =
    logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TableStatisticsCollector>.Instance;

  private const int COLLECTION_INTERVAL_SECONDS = 30;

  /// <inheritdoc/>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Wait for database readiness
    if (databaseReadinessCheck is not null) {
      while (!stoppingToken.IsCancellationRequested) {
        if (await databaseReadinessCheck.IsReadyAsync(stoppingToken)) {
          break;
        }
        await Task.Delay(1000, stoppingToken);
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
