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
  IOptions<HeartbeatWorkerOptions> options,
  ILogger<HeartbeatWorker> logger
) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly HeartbeatWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<HeartbeatWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.IntervalSeconds, _instanceProvider.InstanceId);

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
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "HeartbeatWorker started: interval={IntervalSeconds}s, instance={InstanceId}")]
  static partial void LogStarted(ILogger logger, int intervalSeconds, Guid instanceId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "HeartbeatWorker call failed; will retry on next tick")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "HeartbeatWorker stopped")]
  static partial void LogStopped(ILogger logger);
}

/// <summary>
/// Configuration for <see cref="HeartbeatWorker"/>.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class HeartbeatWorkerOptions {
  /// <summary>
  /// Heartbeat cadence in seconds. Must be less than
  /// <c>WorkCoordinatorPublisherOptions.AbandonStaleInstanceThresholdSeconds / 3</c>
  /// so peers don't false-flag this instance stale (default 30 / 3 = 10 s). Default: 5.
  /// </summary>
  public int IntervalSeconds { get; set; } = 5;
}
