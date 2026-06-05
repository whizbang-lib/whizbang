namespace Whizbang.Core.Notifications;

/// <summary>
/// Options for the slice 26 commit-order stamper. The stamper is a per-DB singleton
/// (enforced via <c>pg_try_advisory_lock</c>); every service instance runs the worker
/// but only the lock-holder actively stamps.
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public sealed class CommitOrderStamperOptions {
  /// <summary>
  /// Polling interval — how often the lock-holder calls <c>stamp_pending_commit_sequences</c>
  /// when no <c>wh_committed</c> NOTIFY has arrived. Acts as the correctness floor in
  /// deployments without a direct connection / LISTEN capability. Default 250ms.
  /// </summary>
  public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(250);

  /// <summary>
  /// Relaxed polling interval used when <see cref="INotifySignalingGate.IsAvailable"/> is
  /// <c>true</c> — that is, when LISTEN/NOTIFY is verified working end-to-end so missed
  /// NOTIFYs are not the dominant failure mode. Default 30 seconds.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Only takes effect when its value is greater than <see cref="PollingInterval"/>; otherwise
  /// ignored. Set explicitly to <c>null</c> to restore pre-Slice-1 behavior (tight polling
  /// always). Falls back to <see cref="PollingInterval"/> immediately the moment the gate
  /// reports <c>IsAvailable = false</c>, so a listener outage never silently increases
  /// commit-stamping latency.
  /// </para>
  /// <para>
  /// Mirrors the same pattern <see cref="ClaimWorker"/> uses for its
  /// <c>NotifyHealthyPollingIntervalMilliseconds</c> — when the gate is healthy, NOTIFY-on-
  /// <c>wh_committed</c> drives sub-ms wake; the periodic poll is only a backstop and can
  /// run at relaxed cadence without risking stamping latency in practice.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/commit-sequence#polling-cadence</docs>
  [Obsolete("Slice 4 of zero-idle-polling moves backup polling into BackupTickCoordinator, which owns the cadence across all backstop concerns. This knob continues to honor its semantic for backward compatibility but will be removed when the stamper's standalone backstop loop is retired in a follow-up slice. Tune BackupTickCoordinatorOptions.PollingInterval instead.")]
  public TimeSpan? NotifyHealthyPollingInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long a non-holder waits before retrying the advisory-lock acquisition. Default 1.5s.
  /// On the holder's clean shutdown the lock auto-releases; the next contender's retry picks it up.
  /// </summary>
  public TimeSpan LeaderElectionRetry { get; set; } = TimeSpan.FromMilliseconds(1500);

  /// <summary>
  /// Maximum rows stamped per <c>stamp_pending_commit_sequences</c> call. Larger values
  /// reduce per-call overhead under heavy load but increase per-call latency. Default 1000.
  /// </summary>
  public int BatchSize { get; set; } = 1000;

  /// <summary>
  /// Killswitch. When true, the worker exits early and never acquires the lock.
  /// Polling backstop is unaffected (it lives in the worker; without the worker, there's no stamping).
  /// </summary>
  public bool DisableStamper { get; set; }

  /// <summary>
  /// Advisory lock key. Must be the same across all instances of the same service DB. Default
  /// is a stable constant chosen to not collide with application-level advisory locks.
  /// </summary>
  public long AdvisoryLockKey { get; set; } = 0x57480001_5557_5048L;
}
