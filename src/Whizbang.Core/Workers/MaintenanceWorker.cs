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
        await RunMaintenanceOnceAsync(stoppingToken);
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

  internal async Task RunMaintenanceOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var results = await coordinator.PerformMaintenanceAsync(ct);
    foreach (var r in results) {
      LogMaintenanceResult(_logger, r.TaskName, r.RowsAffected, r.DurationMs);
    }

    // v0.657 slice 5: structural stuck-row sentinel. Runs after the regular
    // maintenance cycle so backings that don't implement it (default no-op
    // returning empty list) pay zero cost; Postgres backends use the partial
    // indexes added in migration 054 for O(log N) scan on a ~0-sized index.
    if (_options.StuckRowSentinelEnabled) {
      await _runStuckRowSentinelAsync(coordinator, ct);
    }
  }

  private async Task _runStuckRowSentinelAsync(IWorkCoordinator coordinator, CancellationToken ct) {
    var max = _options.StuckRowSentinelMaxAttempts;
    var limit = _options.StuckRowSentinelLimit;
    var stuckOutbox = await coordinator.FindStuckOutboxRowsAsync(max, limit, ct);
    foreach (var row in stuckOutbox) {
      LogStuckOutboxRow(_logger, row.MessageId, row.MessageType, row.StreamId, row.Attempts, row.ClaimedSince);
    }
    var stuckInbox = await coordinator.FindStuckInboxRowsAsync(max, limit, ct);
    foreach (var row in stuckInbox) {
      LogStuckInboxRow(_logger, row.MessageId, row.MessageType, row.StreamId, row.Attempts, row.ClaimedSince);
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

  // v0.657 slice 5: stuck-row sentinel. One Warning per stuck row so operators
  // can grep by MessageId, GROUP BY MessageType to find spammy producers, and
  // correlate ClaimedSince against deploy boundaries.
  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Stuck outbox row sentinel: message_id={MessageId} type={MessageType} stream={StreamId} attempts={Attempts} since={ClaimedSince:o} — row claimed past MaxOutboxAttempts but never drained. Investigate; see operations/observability/stuck-row-sentinel.")]
  static partial void LogStuckOutboxRow(ILogger logger, Guid messageId, string messageType, Guid? streamId, int attempts, DateTime claimedSince);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "Stuck inbox row sentinel: message_id={MessageId} type={MessageType} stream={StreamId} attempts={Attempts} since={ClaimedSince:o} — row claimed past MaxInboxAttempts but never drained. Investigate; see operations/observability/stuck-row-sentinel.")]
  static partial void LogStuckInboxRow(ILogger logger, Guid messageId, string messageType, Guid? streamId, int attempts, DateTime claimedSince);
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

  /// <summary>
  /// v0.657 slice 5: structural stuck-row sentinel killswitch. When <c>true</c>
  /// (default), the maintenance worker calls
  /// <see cref="IWorkCoordinator.FindStuckOutboxRowsAsync"/> and
  /// <see cref="IWorkCoordinator.FindStuckInboxRowsAsync"/> once per cycle and
  /// emits a Warning per row. Set to <c>false</c> if the canary ever becomes
  /// noisy and you want to disable it independently of the rest of maintenance.
  /// </summary>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  public bool StuckRowSentinelEnabled { get; set; } = true;

  /// <summary>
  /// Attempts threshold for the stuck-row sentinel. A row is "stuck" when
  /// <c>attempts &gt; StuckRowSentinelMaxAttempts</c> AND <c>processed_at IS NULL</c>.
  /// Default 10 — matches the
  /// <see cref="OutboxDrainWorkerOptions.MaxOutboxAttempts"/> default.
  /// </summary>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  public int StuckRowSentinelMaxAttempts { get; set; } = 10;

  /// <summary>
  /// Cap on returned stuck rows per cycle so Warning emission is bounded under
  /// saturation. Default 50 — high enough to catch a typical incident,
  /// low enough that log volume stays manageable.
  /// </summary>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  public int StuckRowSentinelLimit { get; set; } = 50;
}
