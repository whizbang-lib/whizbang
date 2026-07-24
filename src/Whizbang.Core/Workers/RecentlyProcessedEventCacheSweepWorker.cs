using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background service that periodically evicts expired entries from the
/// <see cref="RecentlyProcessedEventCache"/>. Phase H step 7 slice 7.
/// </summary>
/// <remarks>
/// <para>
/// Lookups via <see cref="RecentlyProcessedEventCache.WasRecentlyProcessed"/> already use lazy
/// expiry — past-TTL entries return <c>false</c> even before the sweep runs. The sweep's only
/// job is to bound memory: stale entries linger until either the next sweep or until insert-time
/// cap eviction reclaims them.
/// </para>
/// <para>
/// Disabled when <see cref="RecentlyProcessedEventCacheOptions.Enabled"/> is <c>false</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed partial class RecentlyProcessedEventCacheSweepWorker(
    RecentlyProcessedEventCache cache,
    IOptions<RecentlyProcessedEventCacheOptions> options,
    ILogger<RecentlyProcessedEventCacheSweepWorker>? logger = null) : BackgroundService {
  private readonly RecentlyProcessedEventCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
  private readonly RecentlyProcessedEventCacheOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
  private readonly ILogger<RecentlyProcessedEventCacheSweepWorker> _logger =
    logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RecentlyProcessedEventCacheSweepWorker>.Instance;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled) {
      LogDisabled(_logger);
      await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
      return;
    }

    var interval = TimeSpan.FromSeconds(Math.Max(1, _options.SweepIntervalSeconds));
    LogStarted(_logger, interval.TotalSeconds);

    using var timer = new PeriodicTimer(interval);
    try {
      while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
        try {
          _cache.SweepExpired();
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          LogSweepFailed(_logger, ex);
        }
      }
    } catch (OperationCanceledException) {
      // shutdown
    }

    LogStopped(_logger);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "RecentlyProcessedEventCacheSweepWorker started — sweep interval {IntervalSeconds:F0} s")]
  static partial void LogStarted(ILogger logger, double intervalSeconds);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "RecentlyProcessedEventCacheSweepWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "RecentlyProcessedEventCacheSweepWorker disabled via options — worker idle")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "RecentlyProcessedEventCacheSweepWorker: SweepExpired threw")]
  static partial void LogSweepFailed(ILogger logger, Exception ex);
}
