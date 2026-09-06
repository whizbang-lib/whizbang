using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Background service that periodically reads <see cref="INotifyDebounceStatsProvider"/> and
/// refreshes <see cref="NotifyDebounceMetrics"/>. Runs every 15 seconds — responsive enough to see
/// a flood engage and clear, cheap enough to be a rounding error against normal query load — and
/// waits for schema readiness before its first read (the state table arrives with migration 137).
/// </summary>
/// <remarks>
/// Modelled on <see cref="TableStatisticsCollector"/>: a periodic <see cref="BackgroundService"/>
/// refreshing gauge caches, with a public once-through event as the deterministic test seam. A
/// provider read failure is logged and retried next tick — a metrics hiccup must never fault the
/// host.
/// </remarks>
/// <docs>operations/observability/metrics#notify-debounce</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/NotifyDebounceStatsCollectorTests.cs</tests>
public sealed partial class NotifyDebounceStatsCollector(
  IServiceScopeFactory scopeFactory,
  NotifyDebounceMetrics metrics,
  Whizbang.Core.Workers.ISchemaReadyGate schemaReadyGate,
  ILogger<NotifyDebounceStatsCollector> logger
) : BackgroundService {

  // Required, not optional: an optional injected interface param is silently null wherever the
  // type is built by hand (CompositionSatisfiabilityTests guards the surface from growing). DI
  // always supplies ILogger<T>; tests pass NullLogger.
  private readonly ILogger<NotifyDebounceStatsCollector> _logger =
    logger ?? throw new ArgumentNullException(nameof(logger));

  private const int COLLECTION_INTERVAL_SECONDS = 15;

  /// <summary>
  /// Raised once every collection cycle, AFTER the metric cache has been written — so an observer
  /// can tell a cycle's values have actually landed (watching the provider only proves it was asked).
  /// </summary>
  internal event Action? CycleCompleted;

  /// <inheritdoc/>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Don't query the state table before migration 137 has created it.
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
        var provider = scope.ServiceProvider.GetService<INotifyDebounceStatsProvider>();
        if (provider is null) {
          LogProviderNotRegistered(_logger);
          return;
        }

        var stats = await provider.GetStatsAsync(stoppingToken);
        metrics.Update(stats);
        CycleCompleted?.Invoke();
      } catch (ObjectDisposedException) {
        break;  // Host is shutting down — exit the loop
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

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "INotifyDebounceStatsProvider not registered — adaptive notify-debounce metrics disabled")]
  static partial void LogProviderNotRegistered(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Error collecting adaptive notify-debounce stats — will retry")]
  static partial void LogCollectionError(ILogger logger, Exception exception);
}
