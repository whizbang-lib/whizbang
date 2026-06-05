using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Decoupled heartbeat timer. Fires <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>
/// on a fixed cadence (5 s default) independent of the polling claim worker. Replaces
/// the legacy "heartbeat embedded in <c>process_work_batch</c>" coupling.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public partial class HeartbeatWorker(
  IServiceScopeFactory scopeFactory,
  IServiceInstanceProvider instanceProvider,
  ISchemaReadyGate schemaReadyGate,
  IOptions<HeartbeatWorkerOptions> options,
  ILogger<HeartbeatWorker> logger
) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly HeartbeatWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<HeartbeatWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <summary>
  /// Fires after every successful <c>RecordHeartbeatAsync</c> call. Slice 4 of
  /// zero-idle-polling subscribes <see cref="IIdleActivityTracker.Touch"/> to
  /// this so the <see cref="BackupTickCoordinator"/> never engages POLLING
  /// during a brand-new pod's startup window before any real work has
  /// arrived.
  /// </summary>
  public event Action? OnHeartbeatRecorded;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.IntervalSeconds, _instanceProvider.InstanceId);

    // Diagnostic: dump the receptor-registry snapshot at startup so operators can verify
    // the multi-assembly [ModuleInitializer] pattern populated correctly. If the
    // contribution count or receptor-type count is zero, the receive-boundary drop-gate
    // will silently drop every message and chat / cascades will not work.
    var (Contributions, AnyConsumerTypes, InboxHandlerTypes, StageTypeCount) = Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.GetDiagnosticSnapshot();
    LogReceptorRegistrySnapshot(_logger,
      Contributions, AnyConsumerTypes, InboxHandlerTypes, StageTypeCount);

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
        await _heartbeatOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        // Heartbeat failures are non-fatal — peers may flag this instance stale,
        // which is the correct behavior. Log and continue.
        LogError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  private async Task _heartbeatOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId), ct);
    OnHeartbeatRecorded?.Invoke();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "HeartbeatWorker started: interval={IntervalSeconds}s, instance={InstanceId}")]
  static partial void LogStarted(ILogger logger, int intervalSeconds, Guid instanceId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "HeartbeatWorker call failed; will retry on next tick")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "HeartbeatWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "HeartbeatWorker disabled via options — heartbeat skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Whizbang receptor registry: {Contributions} assembly contribution(s), {AnyConsumerTypes} any-consumer type(s), {InboxHandlerTypes} inbox-handler type(s), {StageTypeCount} lifecycle-stage receptor type(s) across all stages. Zero values mean the multi-assembly [ModuleInitializer] pattern did not populate — every message will be dropped at the receive boundary.")]
  static partial void LogReceptorRegistrySnapshot(ILogger logger, int contributions, int anyConsumerTypes, int inboxHandlerTypes, int stageTypeCount);
}

/// <summary>
/// Configuration for <see cref="HeartbeatWorker"/>.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class HeartbeatWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the heartbeat loop entirely. The worker
  /// stays registered as a hosted service but skips its <see cref="ExecuteAsync"/> body.
  /// Without heartbeats, peers will eventually flag this instance stale — useful for
  /// gracefully draining an instance ahead of decommission. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Heartbeat cadence in seconds. Default 30.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Slice 5 of zero-idle-polling raised this from 5 s to 30 s. Safe because
  /// orphan detection now has two independent freshness signals: the
  /// timer-driven <c>wh_service_instances.last_heartbeat_at</c> column AND
  /// the TCP-fresh LISTEN connection in <c>pg_stat_activity</c> joined
  /// via the <c>wh_live_instances</c> view (migration 052). The
  /// <c>claim_orphaned_*</c> functions (migrations 024/025/027 after
  /// Slice 2b) consult both, so a 30 s gap between heartbeat row UPDATEs
  /// doesn't weaken orphan detection while the pod's LISTEN connection
  /// stays alive.
  /// </para>
  /// <para>
  /// Must remain less than
  /// <c>WorkCoordinatorPublisherOptions.AbandonStaleInstanceThresholdSeconds / 3</c>
  /// so peers don't false-flag this instance stale during a transient
  /// gap. With the threshold's default of 600 s and three-renewal margin,
  /// 30 s sits comfortably below.
  /// </para>
  /// </remarks>
  public int IntervalSeconds { get; set; } = 30;
}
