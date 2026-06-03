namespace Whizbang.Core.Messaging;

/// <summary>
/// Lifecycle of a row in <c>wh_dead_letters</c>. Mirrors the SQL enum mapping.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
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

/// <summary>
/// Configuration for the DLQ recovery subsystem.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
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
  /// When <c>true</c> (default), the worker runs one extra scan on startup that auto-
  /// resets <c>next_recovery_at = NOW()</c> for every DLQ row whose current generation
  /// is not in <c>retried_on_generations</c>. Implements the "we shipped a fix" auto-replay.
  /// </summary>
  public bool EnableGenerationReplay { get; set; } = true;

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
