namespace Whizbang.Core.Notifications;

/// <summary>
/// Options for the slice 26 commit-order stamper. The stamper is a singleton per service
/// schema (enforced via <c>pg_try_advisory_lock</c> on a schema-scoped key); every service
/// instance runs the worker but only the lock-holder actively stamps. Services that share a
/// database in separate schemas each elect their own stamper.
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public sealed class CommitOrderStamperOptions {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>
  /// The role the stamper's leader holds when duties are held by assignment (#966): one holder per
  /// schema, renewed by the stamping loop itself, with every stamp fenced by the holder's epoch.
  /// </summary>
  public const string ROLE = "commit-stamper";
#pragma warning restore CA1707

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
  /// Retry cadence while unstamped rows remain after a stamp call returned zero — that is,
  /// the per-database ordering fence is held by an in-flight same-database transaction. The
  /// leader keeps re-stamping at this interval until the pending set drains, so stamping
  /// latency after the fence clears is bounded by this value instead of by the next external
  /// wake (a later commit's NOTIFY or the backup tick). Without it, a commit whose own wake
  /// landed while any older transaction was still open sat unstamped — and therefore
  /// invisible to perspective fetches — until the backstop cadence fired. Default 250 ms.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence#fenced-retry</docs>
  public TimeSpan FencedRetryInterval { get; set; } = TimeSpan.FromMilliseconds(250);

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
  /// Base of the advisory lock key. The stamper does not lock on this value directly: it
  /// derives its key from this value and the service's schema, so every instance of one service
  /// contends for the same lock while services in other schemas of the same database elect their
  /// own stampers. Must be the same across all instances of the same service. Default is a stable
  /// constant; change it only to move the derived key away from a colliding application lock.
  /// </summary>
  public long AdvisoryLockKey { get; set; } = 0x57480001_5557_5048L;
}
