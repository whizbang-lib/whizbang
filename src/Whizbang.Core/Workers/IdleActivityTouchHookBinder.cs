using Microsoft.Extensions.Hosting;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Workers;

/// <summary>
/// IHostedService that subscribes <see cref="IIdleActivityTracker.Touch"/> to
/// the four Whizbang.Core-side event sources at startup, and unsubscribes
/// on shutdown. Slice 4c of zero-idle-polling.
/// </summary>
/// <remarks>
/// <para>
/// Wired sources (in subscription order):
/// </para>
/// <list type="bullet">
/// <item><description><see cref="ClaimWorker.OnBatchClaimed"/> → <c>Touch("claim")</c></description></item>
/// <item><description><see cref="HeartbeatWorker.OnHeartbeatRecorded"/> → <c>Touch("heartbeat")</c></description></item>
/// <item><description><see cref="IWorkNotificationListener.OnSignal"/> → <c>Touch("notify")</c></description></item>
/// </list>
/// <para>
/// Driver-specific event sources (e.g. <c>PgCommitOrderStamperWorker.OnStampCompleted</c>,
/// <c>PgSharedNotifyConnection.OnAvailabilityChanged</c>) are wired by their own
/// service-collection extensions when the relevant driver is registered.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/idle-activity-tracking</docs>
public sealed class IdleActivityTouchHookBinder(
  IIdleActivityTracker tracker,
  ClaimWorker claimWorker,
  HeartbeatWorker heartbeatWorker,
  IWorkNotificationListener notificationListener
) : IHostedService {
  private readonly IIdleActivityTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
  private readonly ClaimWorker _claimWorker = claimWorker ?? throw new ArgumentNullException(nameof(claimWorker));
  private readonly HeartbeatWorker _heartbeatWorker = heartbeatWorker ?? throw new ArgumentNullException(nameof(heartbeatWorker));
  private readonly IWorkNotificationListener _notificationListener = notificationListener ?? throw new ArgumentNullException(nameof(notificationListener));

  private Action<Messaging.WorkBatch>? _onBatch;
  private Action? _onHeartbeat;
  private Action<WorkSignalCategory>? _onSignal;

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    _onBatch = _ => _tracker.Touch("claim");
    _onHeartbeat = () => _tracker.Touch("heartbeat");
    _onSignal = _ => _tracker.Touch("notify");
    _claimWorker.OnBatchClaimed += _onBatch;
    _heartbeatWorker.OnHeartbeatRecorded += _onHeartbeat;
    _notificationListener.OnSignal += _onSignal;
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) {
    if (_onBatch is not null) {
      _claimWorker.OnBatchClaimed -= _onBatch;
    }
    if (_onHeartbeat is not null) {
      _heartbeatWorker.OnHeartbeatRecorded -= _onHeartbeat;
    }
    if (_onSignal is not null) {
      _notificationListener.OnSignal -= _onSignal;
    }
    return Task.CompletedTask;
  }
}
