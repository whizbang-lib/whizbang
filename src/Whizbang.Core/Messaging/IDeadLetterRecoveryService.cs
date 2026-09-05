namespace Whizbang.Core.Messaging;

/// <summary>
/// Recovery-side persistence surface for <c>wh_dead_letters</c>. Separate from
/// <see cref="IDeadLetterStore"/> (the failure-path Move) because callers don't typically
/// hold both: failure workers only need Move; the recovery worker holds this surface.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DlqCanaryCampaignSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StackHistorySqlTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerTests.cs:PendingEntry_RetryableReason_GetsRecoveredAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerTests.cs:Startup_RunsGenerationReplayOnceAsync</tests>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/DeadLetterOperatorEndpointsTests.cs:PostRetry_SchedulesIdForImmediateAttemptAsync</tests>
public interface IDeadLetterRecoveryService {
  /// <summary>
  /// Returns up to <paramref name="maxCount"/> DLQ rows ready for the recovery worker
  /// to attempt. Skips terminal-status rows (Recovered, PermanentlyFailed, HoldForReview)
  /// and rows whose operator_disposition is HoldIndefinitely / MarkPermanentlyFailed.
  /// </summary>
  Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default);

  /// <summary>
  /// Atomically re-emits the dead-lettered row back into its source work table with
  /// <c>attempts=0</c> and marks the DLQ row Recovered. Returns <c>true</c> on success,
  /// <c>false</c> if the row was already terminal or claimed by another worker.
  /// </summary>
  Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default);

  /// <summary>Terminal state — moves the row to HoldForReview.</summary>
  Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default);

  /// <summary>Terminal state — moves the row to PermanentlyFailed.</summary>
  Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default);

  /// <summary>
  /// Settles a row whose message belongs to a DISABLED subsystem: recovery_status becomes
  /// Recovered with an explanatory operator note, without re-driving anything. The message
  /// has no meaning while its feature is off — holding it forever recreates invisible
  /// quarantine inventory, and re-driving it is impossible for reasons like
  /// PoisonRedeliveryLoop whose policy quarantines before dispatch (#684).
  /// </summary>
  /// <docs>operations/dead-letter-queue/canary-recovery</docs>
  Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default);

  /// <summary>
  /// After a failed recovery attempt, sets <c>next_recovery_at</c> to apply the policy
  /// cooldown and returns the row to Pending.
  /// </summary>
  Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default);

  /// <summary>
  /// Generation-replay sweep: schedules every non-terminal DLQ row whose generation has
  /// not yet seen the current build for immediate retry. Idempotent (exactly-once per
  /// generation). Returns the number of rows scheduled.
  /// </summary>
  /// <param name="currentGeneration">The build identity being replayed onto.</param>
  /// <param name="staggerMinutes">Window to spread re-offers across (#669); 0 schedules all immediately.</param>
  /// <param name="ct">Cancellation token.</param>
  Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default);

  // -------------------- Held-cohort campaign surface (P1) --------------------
  // Backing for DeadLetterRecoveryOptions.RetryHeldOnStartup. Campaigns operate on
  // HELD rows grouped by error_fingerprint; the worker orchestrates, this surface
  // persists. See plans/dlq-stack-intelligence.md.

  /// <summary>
  /// The data-driven grandfather gate: held rows that lack a re-drivable payload can
  /// never be recovered by any campaign — marks them PermanentlyFailed so campaigns
  /// operate only on rows the machinery can actually re-drive. Returns rows purged.
  /// </summary>
  Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default);

  /// <summary>Held rows grouped by error fingerprint — the campaign units.</summary>
  Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default);

  /// <summary>
  /// Starts a canary campaign for one cohort: selects up to <paramref name="probeSize"/>
  /// probe rows stratified across the cohort's message types, returns them to Pending
  /// due immediately (the normal paced scan re-drives them), resets the probed messages'
  /// redelivery observation windows (a bound-hit row would otherwise auto-fail its own
  /// probe), and records the campaign. Idempotent per (fingerprint, generation): an
  /// existing campaign is left untouched and 0 is returned; a cohort whose campaigns have
  /// FAILED on <paramref name="generationBudget"/> distinct generations returns -1 and
  /// touches nothing — permanently pending operator.
  /// </summary>
  Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default);

  /// <summary>
  /// Evaluates a campaign's probes: recovered probes count as successes; a probe whose
  /// message dead-lettered again (a newer unrecovered row for the same source id) counts
  /// as a failure; anything else is still outstanding. Persists and returns the verdict —
  /// <see cref="CanaryVerdictKind.Pending"/> while probes remain outstanding.
  /// </summary>
  Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default);

  /// <summary>
  /// Releases a held cohort back to Pending with next_recovery_at staggered across
  /// <paramref name="stagger"/> so the paced scan machinery drains it — release is
  /// eligibility, never a firehose. Returns rows released.
  /// </summary>
  Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default);

  /// <summary>
  /// Fingerprints whose canary campaign reached a terminal <see cref="CanaryVerdictKind.Pass"/>
  /// for <paramref name="generation"/>. A Pass is standing evidence about the build, not a
  /// one-shot release trigger: the recovery worker consults this before quarantining an
  /// exhausted row, so a proven-safe cohort keeps re-driving instead of re-holding (#681).
  /// </summary>
  /// <docs>operations/dead-letter-queue/canary-recovery</docs>
  Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default);

  /// <summary>
  /// Releases up to <paramref name="waveSize"/> held rows of a Mixed cohort as one
  /// trickle wave (staggered inside the wave window) and stamps the campaign's wave
  /// state. Returns rows released — 0 means the cohort is fully drained.
  /// </summary>
  Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default);

  /// <summary>
  /// Evaluates the current trickle wave: how many NEW unrecovered dead letters with this
  /// fingerprint arrived since the wave started (requarantines = the wave washing back).
  /// </summary>
  Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default);

  // -------------------- Stack backfill surface (P2) --------------------
  // The relational stack layer normalizes in C# (one implementation — see
  // Whizbang.Core.DeadLetters.StackNormalizer) and persists here.

  /// <summary>Dead letters not yet stamped with a stack id, newest first.</summary>
  Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default);

  /// <summary>
  /// Persists a normalized stack (frames, ordered links, stack row) idempotently, bumps the
  /// stack's <c>last_seen</c>, increments today's rolling-history count, and stamps the dead
  /// letter with its stack id.
  /// </summary>
  Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default);

  /// <summary>
  /// Records a whole batch of normalized stacks in one round trip — the stack backfill's
  /// hot path, so a storm-sized batch is one call rather than one per row. Returns the count
  /// of NEVER-BEFORE-SEEN stack ids in the batch — the new-failure-mode signal.
  /// </summary>
  Task<int> RecordStacksAsync(IReadOnlyList<(Guid DeadLetterId, Whizbang.Core.DeadLetters.StackIdentity Stack)> entries, CancellationToken ct = default);

  /// <summary>
  /// Prunes rolling stack-history rows older than <paramref name="retentionDays"/> days.
  /// A non-positive retention disables the cleanup (the log is kept forever). Returns rows
  /// pruned.
  /// </summary>
  Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default);
}

/// <summary>A dead letter awaiting stack normalization.</summary>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
public sealed record UnstackedDeadLetter(Guid DeadLetterId, string ErrorText);

/// <summary>One campaign unit: held rows sharing an error fingerprint.</summary>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
public sealed record HeldCohort(string Fingerprint, long RowCount, int MessageTypeCount);

/// <summary>How a canary campaign's probes resolved.</summary>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
public enum CanaryVerdictKind {
  /// <summary>Probes are still outstanding; evaluate again next scan.</summary>
  Pending = 0,
  /// <summary>Every probe recovered — the cohort is safe to release.</summary>
  Pass = 1,
  /// <summary>Every resolved probe failed — the cohort stays held.</summary>
  Fail = 2,
  /// <summary>Probes split — some message types recover, some do not. The cohort stays
  /// held for operator review; the split is reported rather than auto-released.</summary>
  Mixed = 3,
}

/// <summary>A campaign verdict with its probe arithmetic.</summary>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
public sealed record CanaryVerdict(
  CanaryVerdictKind Kind, int ProbesSucceeded, int ProbesFailed, int ProbesOutstanding);
