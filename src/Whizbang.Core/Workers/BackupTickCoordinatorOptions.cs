namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="BackupTickCoordinator"/>. Slice 4 of
/// zero-idle-polling.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference#backup-tick-coordinator</docs>
public sealed class BackupTickCoordinatorOptions {
  /// <summary>
  /// Killswitch — set to <c>false</c> to disable the coordinator entirely.
  /// The hosted service still runs but its <c>ExecuteAsync</c> returns
  /// immediately. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Quiet period before the coordinator transitions from ASLEEP to POLLING.
  /// While <see cref="IIdleActivityTracker.TimeSinceLastActivity"/> is below
  /// this threshold the coordinator does no DB calls at all. Default 30 s.
  /// </summary>
  public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Cadence between backup-tick runs while in POLLING state and the gate
  /// reports NOTIFY healthy. Default 30 s — balances detection latency for
  /// missed work against per-tick DB cost.
  /// </summary>
  public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Cadence used when the gate reports NOTIFY broken
  /// (<see cref="Notifications.INotifySignalingGate.IsAvailable"/> is
  /// <c>false</c>). Tighter because the system has no NOTIFY signal to wake
  /// it — every tick is the only chance to discover work. Default 5 s.
  /// </summary>
  public TimeSpan FastPollingInterval { get; set; } = TimeSpan.FromSeconds(5);
}
