using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Low-cadence worker that wakes the per-instance NOTIFY channels for any wh_outbox or
/// wh_inbox rows whose <c>scheduled_for</c> retry timestamp has elapsed. Replaces the role
/// that the 250 ms ClaimWorker baseline poll used to play for scheduled_for discovery —
/// keeps retry latency bounded without paying the polling tax across the rest of the work
/// coordinator.
/// </summary>
/// <remarks>
/// <para>
/// Default cadence: 10 s. That's the worst-case latency between a scheduled_for boundary
/// passing and the owning instance receiving its catch-up NOTIFY. Bumping it lower gives
/// tighter retry latency at the cost of one extra <c>notify_scheduled_retry_due()</c> SQL
/// call per second saved; bumping higher saves DB calls.
/// </para>
/// <para>
/// This worker DOES NOT claim or process work — it only emits NOTIFYs. The actual claim
/// happens via <see cref="ClaimWorker"/>'s normal NOTIFY-driven wake path. That isolation
/// keeps this worker cheap (one cheap indexed query per cycle) and lets ClaimWorker remain
/// the single owner of the claim semantics.
/// </para>
/// <para>
/// When this worker is disabled, scheduled retries still get discovered — via the
/// <see cref="ClaimWorker.PollingIntervalMilliseconds"/> / <see cref="ClaimWorker.NotifyHealthyPollingIntervalMilliseconds"/>
/// safety-net poll. So this worker is an optimization, not a correctness requirement.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/scheduled-retries</docs>
public partial class ScheduledRetryWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<ScheduledRetryWorkerOptions> options,
  ILogger<ScheduledRetryWorker> logger
) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly ScheduledRetryWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<ScheduledRetryWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  private long _totalNotifyCycles;
  private long _totalStreamsWoken;

  /// <summary>Number of cycles executed since process start. Exposed for tests + observability.</summary>
  public long TotalNotifyCycles => Interlocked.Read(ref _totalNotifyCycles);

  /// <summary>Cumulative count of distinct streams the worker has woken via NOTIFY.</summary>
  public long TotalStreamsWoken => Interlocked.Read(ref _totalStreamsWoken);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.PollIntervalSeconds);

    if (!_options.Enabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      return;
    }

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        var woken = await _wakeScheduledAsync(stoppingToken);
        Interlocked.Increment(ref _totalNotifyCycles);
        if (woken > 0) {
          Interlocked.Add(ref _totalStreamsWoken, woken);
          LogWoke(_logger, woken);
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        // Wake failures are non-fatal — the next ClaimWorker poll covers any rows we miss.
        LogError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  private async Task<int> _wakeScheduledAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    return await coordinator.NotifyScheduledRetryDueAsync(ct);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "ScheduledRetryWorker started: pollIntervalSeconds={PollIntervalSeconds}")]
  static partial void LogStarted(ILogger logger, int pollIntervalSeconds);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ScheduledRetryWorker disabled — no scheduled-retry NOTIFYs will be emitted; ClaimWorker safety-net poll covers retries")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "ScheduledRetryWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "ScheduledRetryWorker cycle failed; will retry on next tick")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
    Message = "ScheduledRetryWorker woke {StreamsWoken} stream(s) with elapsed scheduled_for")]
  static partial void LogWoke(ILogger logger, int streamsWoken);
}

/// <summary>Configuration for <see cref="ScheduledRetryWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/scheduled-retries</docs>
public sealed class ScheduledRetryWorkerOptions {
  /// <summary>
  /// Killswitch. Default <c>true</c>. When <c>false</c>, scheduled retries are discovered
  /// via the ClaimWorker safety-net poll instead of dedicated NOTIFYs — correct but slower.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Interval between scheduled-retry-due scans in seconds. Default <c>10</c>. Bound on
  /// scheduled-retry latency under NOTIFY-only operation. Lower values trade more SQL calls
  /// for tighter retry latency; higher saves DB load.
  /// </summary>
  public int PollIntervalSeconds { get; set; } = 10;
}
