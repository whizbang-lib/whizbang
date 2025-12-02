using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Background worker that maintains service instance presence in the database.
/// Registers the instance on startup, sends periodic heartbeats, and marks inactive on shutdown.
/// This enables lease-based coordination and orphaned work detection across multiple service instances.
/// </summary>
public sealed class InstanceTrackerWorker : BackgroundService {
  private readonly IWorkCoordinator _workCoordinator;
  private readonly Guid _instanceId;
  private readonly TimeSpan _heartbeatInterval;
  private readonly ILogger<InstanceTrackerWorker> _logger;

  /// <summary>
  /// Creates a new InstanceTrackerWorker.
  /// </summary>
  /// <param name="workCoordinator">Work coordinator for heartbeat updates</param>
  /// <param name="instanceId">Unique identifier for this service instance</param>
  /// <param name="heartbeatInterval">Interval between heartbeat updates (default 30 seconds)</param>
  /// <param name="logger">Logger for diagnostics</param>
  public InstanceTrackerWorker(
    IWorkCoordinator workCoordinator,
    Guid instanceId,
    ILogger<InstanceTrackerWorker> logger,
    TimeSpan? heartbeatInterval = null) {
    _workCoordinator = workCoordinator ?? throw new ArgumentNullException(nameof(workCoordinator));
    _instanceId = instanceId;
    _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// Starts the instance tracker and begins sending heartbeats.
  /// </summary>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation(
      "Instance tracker starting for instance {InstanceId} with heartbeat interval {Interval}",
      _instanceId,
      _heartbeatInterval);

    try {
      // Send initial heartbeat to register instance
      await SendHeartbeatAsync(stoppingToken);

      // Continue sending heartbeats until stopped
      while (!stoppingToken.IsCancellationRequested) {
        await Task.Delay(_heartbeatInterval, stoppingToken);
        await SendHeartbeatAsync(stoppingToken);
      }
    } catch (OperationCanceledException) {
      // Expected when stopping
      _logger.LogInformation("Instance tracker stopping for instance {InstanceId}", _instanceId);
    } catch (Exception ex) {
      _logger.LogError(ex, "Instance tracker failed for instance {InstanceId}", _instanceId);
      throw;
    }
  }

  /// <summary>
  /// Cleanup on shutdown - mark instance as inactive.
  /// </summary>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Instance tracker shutting down for instance {InstanceId}", _instanceId);

    try {
      // Mark instance as inactive on shutdown
      // Note: We don't have a direct "mark inactive" method yet, but the heartbeat
      // expiry will handle this automatically. Future enhancement: add explicit deactivation.
      await base.StopAsync(cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex, "Error during instance tracker shutdown for instance {InstanceId}", _instanceId);
    }
  }

  /// <summary>
  /// Sends a heartbeat update to the database.
  /// </summary>
  private async Task SendHeartbeatAsync(CancellationToken cancellationToken) {
    try {
      // Process work batch with no messages - just updates heartbeat
      var result = await _workCoordinator.ProcessWorkBatchAsync(
        _instanceId,
        outboxCompletedIds: [],
        outboxFailedMessages: [],
        inboxCompletedIds: [],
        inboxFailedMessages: [],
        cancellationToken: cancellationToken);

      _logger.LogDebug(
        "Heartbeat sent for instance {InstanceId} at {Timestamp}",
        _instanceId,
        DateTimeOffset.UtcNow);
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to send heartbeat for instance {InstanceId}", _instanceId);
      // Don't rethrow - continue trying on next interval
    }
  }
}
