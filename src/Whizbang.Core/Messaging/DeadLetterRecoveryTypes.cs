namespace Whizbang.Core.Messaging;

/// <summary>
/// Lifecycle of a row in <c>wh_dead_letters</c>. Mirrors the SQL enum mapping.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DeadLetterRecoveryPolicyTests.cs:RecoveryStatusEnum_HasExpectedValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:ShouldRecover_HoldForReview_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:ShouldRecover_PermanentlyFailed_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreDeadLetterRecoveryServiceTests.cs:MarkHoldingAsync_FlipsStatusToHoldForReviewAsync</tests>
public enum DeadLetterRecoveryStatus {
  /// <summary>Awaiting next_recovery_at — eligible for automatic retry.</summary>
  Pending = 0,
  /// <summary>Currently being attempted by a recovery worker.</summary>
  Recovering = 1,
  /// <summary>No auto-retry; the policy or operator wants a human to look first.</summary>
  HoldForReview = 2,
  /// <summary>Re-emitted successfully back into a source work table.</summary>
  Recovered = 3,
  /// <summary>Exhausted all recovery policies; will not be auto-retried again.</summary>
  PermanentlyFailed = 4,
}

/// <summary>
/// Operator-driven disposition override. Set via the DLQ operator API to influence the
/// recovery worker's next scan.
/// </summary>
/// <docs>operations/dead-letter-queue/operator-api</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DeadLetterRecoveryPolicyTests.cs:DispositionEnum_HasExpectedValuesAsync</tests>
public enum DeadLetterDisposition {
  /// <summary>Default — let the recovery policy decide.</summary>
  None = 0,
  /// <summary>Operator requested immediate retry (reset next_recovery_at = NOW()).</summary>
  RetryNow = 1,
  /// <summary>Operator wants this row held indefinitely; recovery worker skips it.</summary>
  HoldIndefinitely = 2,
  /// <summary>Operator gave up; mark PermanentlyFailed.</summary>
  MarkPermanentlyFailed = 3,
}

/// <summary>
/// Recovery rule applied per <see cref="MessageFailureReason"/>. Captures the budget and
/// cadence the recovery worker should use for rows that dead-lettered with this reason.
/// </summary>
/// <param name="Name">Short name for log / metric tagging (e.g. <c>"AggressiveRetry"</c>).</param>
/// <param name="MaxRecoveryAttempts">How many recovery attempts to make before exhaustion.</param>
/// <param name="Cooldown">How long to wait between recovery attempts.</param>
/// <param name="HoldForReviewAfterExhaustion">When <c>true</c>, exhausting
/// <see cref="MaxRecoveryAttempts"/> lands the row at
/// <see cref="DeadLetterRecoveryStatus.HoldForReview"/> instead of
/// <see cref="DeadLetterRecoveryStatus.PermanentlyFailed"/> — useful for reasons that
/// typically need human attention rather than just more retries.</param>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:GetPolicy_KnownReason_ReturnsConfiguredPolicyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:GetPolicy_MaxAttemptsExceeded_HoldsAfterExhaustionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:GetPolicy_UnknownReasonNotInDictionary_FallsBackToUnknownEntryAsync</tests>
public sealed record RecoveryPolicy(
  string Name,
  int MaxRecoveryAttempts,
  TimeSpan Cooldown,
  bool HoldForReviewAfterExhaustion);

/// <summary>
/// Stream-aware recovery hint. The default <see cref="IDeadLetterRecoveryPolicy"/>
/// returns this per row so the recovery worker knows whether to recover the row in
/// isolation or in coordination with other DLQ entries on the same stream.
/// </summary>
public enum StreamRecoveryMode {
  /// <summary>Recover the row independently of other DLQ entries on the same stream.</summary>
  PerMessage = 0,
  /// <summary>
  /// Coordinate recovery with sibling DLQ entries on the same <c>stream_id</c>. The
  /// recovery worker collects all such entries, sorts by original event order, and
  /// re-emits them together so FIFO is preserved if every recovery succeeds. (When any
  /// individual recovery fails, the rest remain in DLQ; ordering is best-effort.)
  /// </summary>
  TailAware = 1,
}

/// <summary>
/// Reference for an entry in <c>wh_dead_letters</c>. Decision-making subset of the row
/// surface — full row is fetched only when re-emission needs the envelope.
/// </summary>
public sealed record DeadLetterEntry(
  Guid DeadLetterId,
  string SourceTable,
  Guid SourceId,
  Guid? StreamId,
  string MessageType,
  MessageFailureReason FailureReason,
  int AttemptsWhenDlq,
  DateTimeOffset DeadLetteredAt,
  DeadLetterRecoveryStatus RecoveryStatus,
  int RecoveryAttempts,
  string Generation);

/// <summary>
/// Decides whether and how to recover a dead-lettered row. Default implementation reads
/// <see cref="DeadLetterRecoveryOptions.PolicyByReason"/> + <c>[StreamRecovery]</c> /
/// <c>[FifoStreamRecovery]</c> attributes; operators can register a custom implementation
/// for fully programmatic control.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:GetPolicy_TransportException_ReturnsMediumRetryAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:GetStreamMode_StreamIdPresent_ReturnsTailAwareAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerTests.cs:EntryWithHoldForReviewStatus_IsSkippedByPolicyAsync</tests>
public interface IDeadLetterRecoveryPolicy {
  /// <summary>Returns the policy for this entry's <see cref="MessageFailureReason"/>.</summary>
  RecoveryPolicy GetPolicy(DeadLetterEntry entry);

  /// <summary>Returns whether to recover stream-aware or per-message.</summary>
  StreamRecoveryMode GetStreamMode(DeadLetterEntry entry);

  /// <summary>
  /// When <c>false</c>, the recovery worker skips this entry entirely on the current
  /// scan. Used for application-specific "don't touch yet" decisions; the default impl
  /// returns <c>true</c> unless <see cref="DeadLetterRecoveryStatus.HoldForReview"/> or
  /// <see cref="DeadLetterDisposition.HoldIndefinitely"/> apply.
  /// </summary>
  bool ShouldRecover(DeadLetterEntry entry);
}

/// <summary>Startup posture toward HELD dead-letter rows.</summary>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
public enum RetryHeldOnStartupMode {
  /// <summary>Held rows stay held (default).</summary>
  Off = 0,
  /// <summary>Probe each cohort; release only cohorts whose probes all recover.</summary>
  Canary = 1,
  /// <summary>Release every held cohort, staggered, without probing.</summary>
  Full = 2,
}

/// <summary>
/// Configuration for the DLQ recovery subsystem.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:GetPolicy_UnknownReasonNotInDictionary_FallsBackToUnknownEntryAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DefaultDeadLetterRecoveryPolicyTests.cs:Constructor_NullOptions_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DeadLetterRecoveryPolicyTests.cs:ThrottledReason_DefaultsToAggressiveRetryAsync</tests>
public sealed class DeadLetterRecoveryOptions {
  /// <summary>Killswitch — disables the recovery worker entirely. Default <c>true</c>.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Backstop interval between scans, in minutes. The recovery worker ALSO triggers on
  /// idle signals + generation transitions; this cadence catches anything those signals
  /// miss. Default <c>10</c>.
  /// </summary>
  public int ScanIntervalMinutes { get; set; } = 10;

  /// <summary>
  /// Maximum DLQ rows fetched per scan cycle. Bounds how many rows a single cycle
  /// processes — subsequent scans pick up where prior ones stopped. Default <c>200</c>.
  /// </summary>
  public int ScanBatchSize { get; set; } = 200;

  /// <summary>
  /// When <c>true</c> (default), the worker runs one extra scan on startup that auto-
  /// resets <c>next_recovery_at = NOW()</c> for every DLQ row whose current generation
  /// is not in <c>retried_on_generations</c>. Implements the "we shipped a fix" auto-replay.
  /// </summary>
  public bool EnableGenerationReplay { get; set; } = true;

  /// <summary>
  /// Whether recovery stops itself when it detects that it is generating the dead letters it is
  /// recovering. Default <c>true</c>.
  /// </summary>
  /// <remarks>
  /// Turn this off only when something else bounds the cycle. Recovery republishes a failed message,
  /// and a message that fails again is recorded as a NEW row, so the per-row
  /// <see cref="RecoveryPolicy.MaxRecoveryAttempts"/> check never sees the same message twice and
  /// cannot end the cycle.
  /// </remarks>
  public bool LoopBreakerEnabled { get; set; } = true;

  /// <summary>
  /// Whether recovery waits for the service to be idle before re-driving dead letters.
  /// Default <c>true</c>.
  /// </summary>
  /// <remarks>
  /// Re-driving puts work back onto the very queues it failed on, so recovery mid-drain is how a
  /// backlog becomes a second storm. When true, each scan asks the housekeeping arbiter for the
  /// slot: recovery holds the HIGHEST rank (the dead-letter table frequently contains exactly what
  /// integrity would otherwise detect as a gap and re-request over the wire), but it still yields
  /// to a service with unprocessed backlog and resumes on its own once the queues clear. Set false
  /// to re-drive on the scan cadence regardless of load — appropriate only where recovery latency
  /// matters more than interactive throughput.
  /// </remarks>
  public bool WaitForIdle { get; set; } = true;

  /// <summary>
  /// Startup campaign over HELD rows. <see cref="RetryHeldOnStartupMode.Off"/> (default)
  /// leaves held rows alone. <see cref="RetryHeldOnStartupMode.Canary"/> probes
  /// <see cref="CanaryProbeSize"/> rows per fingerprint cohort and releases a cohort only
  /// when every probe recovers. <see cref="RetryHeldOnStartupMode.Full"/> releases every
  /// held cohort without probing — a trust shortcut, never a pacing shortcut: release is
  /// always staggered eligibility drained by the normal paced scans. An operator sets
  /// this and restarts; it binds turnkey from Whizbang:DeadLetterRecovery.
  /// </summary>
  public RetryHeldOnStartupMode RetryHeldOnStartup { get; set; } = RetryHeldOnStartupMode.Off;

  /// <summary>Probe rows per cohort in Canary mode. Default 10.</summary>
  public int CanaryProbeSize { get; set; } = 10;

  /// <summary>
  /// Distinct build generations a cohort's campaigns may FAIL before the cohort becomes
  /// permanently pending an operator decision. Attempt counts are evidence about a build;
  /// this bounds how many builds get to re-test the hypothesis. Default 3.
  /// </summary>
  public int GenerationBudget { get; set; } = 3;

  /// <summary>
  /// When a NEW build generation is detected at startup (generation replay found rows from
  /// an older build), run the canary campaign automatically even with
  /// <see cref="RetryHeldOnStartup"/> Off: held rows are evidence about an old build, and
  /// a deploy that fixed the bug should self-heal its cohorts at probe cost. An explicit
  /// operator mode always wins over this default. Default <c>true</c>.
  /// </summary>
  public bool AutoCanaryOnNewGeneration { get; set; } = true;

  /// <summary>
  /// Window the release of a cohort is staggered across, so the paced scans drain it
  /// instead of one giant due-set arriving at once. Default 30 minutes.
  /// </summary>
  public int ReleaseStaggerMinutes { get; set; } = 30;

  /// <summary>
  /// Dead letters normalized into the relational stack layer per scan (the async half of
  /// the two-layer stack contract; the inline metric is the real-time half). Bounded so a
  /// storm's backlog normalizes across ticks instead of one giant pass. Default 500;
  /// 0 disables backfill.
  /// </summary>
  public int StackBackfillBatchSize { get; set; } = 500;

  /// <summary>
  /// Rolling retention, in days, for the stack-history log (<c>wh_stack_daily</c>): the
  /// recovery worker prunes daily rows older than this so the history survives dead-letter
  /// purging without growing without bound. Default 90. A non-positive value disables the
  /// rolling cleanup — the log is then kept forever.
  /// </summary>
  public int StackHistoryRetentionDays { get; set; } = 90;

  /// <summary>
  /// Share of a scan batch that must postdate the previous scan before that cycle counts as
  /// self-inflicted. Default <c>0.5</c>.
  /// </summary>
  /// <remarks>
  /// At half, recovery is already only breaking even: it is replacing dead letters as fast as it
  /// clears them. New failures arriving while a real backlog drains stay well under this.
  /// </remarks>
  public double LoopBreakerFreshFraction { get; set; } = 0.5;

  /// <summary>
  /// Consecutive self-inflicted cycles required before recovery suspends itself. Default <c>3</c>.
  /// </summary>
  /// <remarks>
  /// One cycle proves nothing: an unrelated burst of failures arriving mid-scan looks identical for
  /// a single tick. Requiring persistence keeps a spike from disabling recovery.
  /// </remarks>
  public int LoopBreakerConsecutiveCycles { get; set; } = 3;

  /// <summary>
  /// Minutes recovery stays suspended after the breaker trips, before it retries. Default <c>60</c>.
  /// </summary>
  /// <remarks>
  /// The breaker closes again on its own so a transient condition does not need an operator, but the
  /// window is long enough that a genuinely stuck deployment is not re-storming every few minutes.
  /// Set to 0 to keep it open until the process restarts.
  /// </remarks>
  public int LoopBreakerCooldownMinutes { get; set; } = 60;

  /// <summary>
  /// Per-<see cref="MessageFailureReason"/> recovery rules. Defaults follow the
  /// design doc's matrix (see plans/dlq-recovery.md).
  /// </summary>
  public Dictionary<MessageFailureReason, RecoveryPolicy> PolicyByReason { get; set; } = new() {
    [MessageFailureReason.Throttled] = new("AggressiveRetry", 3, TimeSpan.FromMinutes(30), HoldForReviewAfterExhaustion: false),
    [MessageFailureReason.TransportException] = new("MediumRetry", 3, TimeSpan.FromHours(1), HoldForReviewAfterExhaustion: false),
    [MessageFailureReason.LeaseExpired] = new("AggressiveRetry", 5, TimeSpan.FromSeconds(0), HoldForReviewAfterExhaustion: false),
    [MessageFailureReason.MaxAttemptsExceeded] = new("ConservativeRetry", 1, TimeSpan.FromHours(6), HoldForReviewAfterExhaustion: true),
    [MessageFailureReason.EventStorageFailure] = new("HoldForReview", 0, TimeSpan.Zero, HoldForReviewAfterExhaustion: true),
    [MessageFailureReason.ValidationError] = new("HoldForReview", 0, TimeSpan.Zero, HoldForReviewAfterExhaustion: true),
    [MessageFailureReason.SerializationError] = new("HoldForReview", 0, TimeSpan.Zero, HoldForReviewAfterExhaustion: true),
    [MessageFailureReason.TransportNotReady] = new("MediumRetry", 3, TimeSpan.FromMinutes(30), HoldForReviewAfterExhaustion: false),
    [MessageFailureReason.Unknown] = new("OneShotThenHold", 1, TimeSpan.FromHours(1), HoldForReviewAfterExhaustion: true),
    // A broker dead-letter usually means "this build could not process the message" — retry on a
    // sane cadence (generation replay additionally re-offers after every deploy), don't park on
    // arrival, and hold for review once the budget is spent so poison stays visible.
    [MessageFailureReason.BrokerDeadLetter] = new("MediumRetry", 3, TimeSpan.FromHours(1), HoldForReviewAfterExhaustion: true),
    // The observation counter proved redelivery is NOT making progress for this message —
    // re-driving it mints a fresh dead letter and recovery ping-pongs with the quarantine
    // (measured in production at ~190 rows/minute, throttled only by the loop breaker).
    // Hold it where an operator can see it; auto-re-drive is the one certainly-wrong answer.
    [MessageFailureReason.PoisonRedeliveryLoop] = new("HoldForReview", 0, TimeSpan.Zero, HoldForReviewAfterExhaustion: true),
  };
}

/// <summary>
/// Default <see cref="IDeadLetterRecoveryPolicy"/> implementation: dictionary lookup by
/// reason + simple stream-mode default (TailAware when stream_id is set, PerMessage
/// otherwise). Applications register their own implementation to override.
/// </summary>
public sealed class DefaultDeadLetterRecoveryPolicy(
  Microsoft.Extensions.Options.IOptions<DeadLetterRecoveryOptions> options) : IDeadLetterRecoveryPolicy {
  private readonly DeadLetterRecoveryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

  /// <inheritdoc />
  public RecoveryPolicy GetPolicy(DeadLetterEntry entry) {
    return _options.PolicyByReason.TryGetValue(entry.FailureReason, out var p)
      ? p
      : _options.PolicyByReason[MessageFailureReason.Unknown];
  }

  /// <inheritdoc />
  public StreamRecoveryMode GetStreamMode(DeadLetterEntry entry) {
    return entry.StreamId.HasValue ? StreamRecoveryMode.TailAware : StreamRecoveryMode.PerMessage;
  }

  /// <inheritdoc />
  public bool ShouldRecover(DeadLetterEntry entry) {
    if (entry.RecoveryStatus == DeadLetterRecoveryStatus.HoldForReview) { return false; }
    if (entry.RecoveryStatus == DeadLetterRecoveryStatus.Recovered) { return false; }
    if (entry.RecoveryStatus == DeadLetterRecoveryStatus.PermanentlyFailed) { return false; }
    return true;
  }
}
