// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Observability;

/// <summary>
/// Reads how much storage cleanup has left to do into the <c>Whizbang.Maintenance</c> meter, on one instance of the fleet
/// per window (#2289).
/// </summary>
/// <remarks>
/// <para>
/// The maintenance meter reports what each cleanup task removed; this reports what each would remove now and has not, so
/// a dashboard can tell "nothing left to clean" from "cleanup is falling behind", for example while the sweep is not
/// admitted. It runs on its own window, <see cref="TableStatisticsOptions.PendingReclamationSampleInterval"/>, never
/// inside the maintenance cycle, and each task's count reads at most
/// <see cref="TableStatisticsOptions.PendingReclamationCountCap"/> rows through the task's index, so a reading costs the
/// same however large the backlog is.
/// </para>
/// <para>
/// The window starts on a multiple of the interval, read from the clock, as the event-type sample's does: every instance
/// names it the same way, one takes it and reports, and the others clear their reading, so the readings are not counted
/// twice when summed across instances. A failed reading is logged and costs that window only. An interval of zero turns
/// the sampler off.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Resolves the provider for each reading.</param>
/// <param name="metrics">The meter the reading is reported on.</param>
/// <param name="options">The interval and the cap.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the window is read from; null is the system clock.</param>
/// <docs>operations/observability/metrics#pending-reclamation</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/PendingReclamationSamplerTests.cs</tests>
public sealed partial class PendingReclamationSampler(
    IServiceScopeFactory scopeFactory,
    MaintenanceMetrics metrics,
    IOptions<TableStatisticsOptions> options,
    ILogger<PendingReclamationSampler>? logger,
    TimeProvider? timeProvider = null) : BackgroundService {

  private const string CLAIM_PREFIX = "whizbang:pending-reclamation:";

  private readonly TableStatisticsOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

  /// <summary>Raised after each reading, with whether this instance took the window.</summary>
  /// <docs>operations/observability/metrics#pending-reclamation</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Observability/PendingReclamationSamplerTests.cs:TheInstanceThatTakesTheWindow_ReportsTheReadingAsync</tests>
  public event Action<bool>? SampleCompleted;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    var interval = _options.PendingReclamationSampleInterval;
    if (interval <= TimeSpan.Zero) {
      LogSampleOff(_logger);
      return;
    }

    while (true) {
      var now = _time.GetUtcNow().UtcTicks;
      var window = now - (now % interval.Ticks);
      try {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetService<ITableStatisticsProvider>();
        if (provider is null) {
          LogProviderNotRegistered(_logger);
          return;
        }

        var reading = await provider.SamplePendingReclamationAsync(
          new PendingReclamationRequest(
            CLAIM_PREFIX + window.ToString(CultureInfo.InvariantCulture), _options.PendingReclamationCountCap),
          stoppingToken).ConfigureAwait(false);
        metrics.UpdatePendingReclamation(reading ?? PendingReclamation.None);
        SampleCompleted?.Invoke(reading is not null);
      } catch (ObjectDisposedException) {
        return;  // the host is shutting down
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogSampleFailed(_logger, ex);
      }

      // Until the next window starts, measured from now, so a slow reading never makes an instance late for it.
      var untilNext = interval - TimeSpan.FromTicks(_time.GetUtcNow().UtcTicks % interval.Ticks);
      try {
        await Task.Delay(untilNext, _time, stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
    }
  }

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Pending-reclamation reading is off: Whizbang:TableStatistics:PendingReclamationSampleInterval is 00:00:00")]
  private static partial void LogSampleOff(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Pending-reclamation reading stopped: no table statistics provider is registered")]
  private static partial void LogProviderNotRegistered(ILogger logger);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Reading how much storage cleanup has left to do failed; the next window is read as usual")]
  private static partial void LogSampleFailed(ILogger logger, Exception exception);
}
