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
/// Samples the event store by event type for the <c>Whizbang.TableStatistics</c> meter, on one instance of the
/// fleet per window.
/// </summary>
/// <remarks>
/// <para>
/// The window is <see cref="TableStatisticsOptions.EventTypeSampleInterval"/> long and starts on a multiple of
/// it, read from the clock, so every instance names the same window the same way and asks for it at the same
/// moment. The provider claims the window; the instance that takes it samples and reports, and every other
/// instance clears its reading. That keeps one instance reporting at a time, so a dashboard that sums across
/// instances does not count the store twice.
/// </para>
/// <para>
/// A failed sample is logged and costs that window only. An interval of zero turns the sampler off.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Resolves the provider for each sample.</param>
/// <param name="metrics">The meter the sample is reported on.</param>
/// <param name="options">The interval, the share sampled and the cap on event types.</param>
/// <param name="schemaReadyGate">Holds the first sample until the schema exists; null starts at once.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="timeProvider">The clock the window is read from; null is the system clock.</param>
/// <docs>operations/observability/metrics#event-type-bytes</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/EventTypeBytesSamplerTests.cs</tests>
public sealed partial class EventTypeBytesSampler(
    IServiceScopeFactory scopeFactory,
    TableStatisticsMetrics metrics,
    IOptions<TableStatisticsOptions> options,
    Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate,
    ILogger<EventTypeBytesSampler>? logger,
    TimeProvider? timeProvider = null) : BackgroundService {

  private const string CLAIM_PREFIX = "whizbang:event-type-bytes:";

  private static readonly IReadOnlyDictionary<string, EventTypeBytes> _nothing = new Dictionary<string, EventTypeBytes>();

  private readonly TableStatisticsOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

  /// <summary>Raised after each sample, with whether this instance took the window.</summary>
  public event Action<bool>? SampleCompleted;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    var interval = _options.EventTypeSampleInterval;
    if (interval <= TimeSpan.Zero) {
      LogSampleOff(_logger);
      return;
    }

    if (schemaReadyGate is not null) {
      try {
        await schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
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

        var sample = await provider.SampleEventTypeBytesAsync(
          new EventTypeSampleRequest(
            CLAIM_PREFIX + window.ToString(CultureInfo.InvariantCulture),
            _options.EventTypeSamplePercent,
            _options.MaxEventTypes),
          stoppingToken).ConfigureAwait(false);
        metrics.UpdateEventTypeBytes(sample ?? _nothing);
        SampleCompleted?.Invoke(sample is not null);
      } catch (ObjectDisposedException) {
        return;  // the host is shutting down
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogSampleFailed(_logger, ex);
      }

      // Until the next window starts, measured from now, so a slow sample never makes an instance late for it.
      var untilNext = interval - TimeSpan.FromTicks(_time.GetUtcNow().UtcTicks % interval.Ticks);
      try {
        await Task.Delay(untilNext, _time, stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
    }
  }

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Event-type sample is off: Whizbang:TableStatistics:EventTypeSampleInterval is 00:00:00")]
  private static partial void LogSampleOff(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Event-type sample stopped: no table statistics provider is registered")]
  private static partial void LogProviderNotRegistered(ILogger logger);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Event-type sample of the event store failed; the next window is sampled as usual")]
  private static partial void LogSampleFailed(ILogger logger, Exception exception);
}
