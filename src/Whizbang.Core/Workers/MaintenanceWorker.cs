using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Periodic timer that calls <see cref="IWorkCoordinator.PerformMaintenanceAsync"/> on a
/// configurable interval. Mirrors the legacy publisher's <c>_runPeriodicMaintenanceAsync</c>
/// (lines 476-488) — same behavior, decoupled from the polling loop.
/// </summary>
/// <remarks>
/// Maintenance is a no-op on most engines that ship default <c>PerformMaintenanceAsync</c>
/// (returns an empty list). Engines with active housekeeping (stale-instance purge,
/// dead-letter cleanup, dedup pruning) light up automatically.
/// </remarks>
/// <docs>fundamentals/work-coordinator/maintenance</docs>
public sealed partial class MaintenanceWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<MaintenanceWorkerOptions> options,
  ILogger<MaintenanceWorker> logger) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly MaintenanceWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<MaintenanceWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.IntervalMinutes);

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
        await _maintenanceOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  private async Task _maintenanceOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var results = await coordinator.PerformMaintenanceAsync(ct);
    foreach (var r in results) {
      LogMaintenanceResult(_logger, r.TaskName, r.RowsAffected, r.DurationMs);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "MaintenanceWorker started: intervalMinutes={IntervalMinutes}")]
  static partial void LogStarted(ILogger logger, int intervalMinutes);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "MaintenanceWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "MaintenanceWorker disabled via options — maintenance skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "MaintenanceWorker tick failed; will retry on next interval")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Maintenance task '{TaskName}' affected {RowsAffected} rows in {DurationMs}ms")]
  static partial void LogMaintenanceResult(ILogger logger, string taskName, long rowsAffected, double durationMs);
}

/// <summary>Configuration for <see cref="MaintenanceWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class MaintenanceWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the maintenance loop. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Interval between maintenance runs, in minutes. Default 10.
  /// <para>
  /// perform_maintenance is the only path that cleans abandoned <c>wh_active_streams</c> rows
  /// (rows whose owning instance is no longer in <c>wh_service_instances</c>). When an instance
  /// dies or is scaled in, its ownership rows persist until this loop runs. >10 min lets that
  /// accumulate, degrading owner-preferring claim. The audit gap #5 lock test in
  /// <c>MaintenanceWorkerTests.DefaultIntervalMinutes_IsLessThanOrEqualTo10MinutesAsync</c>
  /// fails if a future refactor weakens this default.
  /// </para>
  /// </summary>
  public int IntervalMinutes { get; set; } = 10;
}
