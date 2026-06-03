namespace Whizbang.Core.Messaging;

/// <summary>
/// Recovery-side persistence surface for <c>wh_dead_letters</c>. Separate from
/// <see cref="IDeadLetterStore"/> (the failure-path Move) because callers don't typically
/// hold both: failure workers only need Move; the recovery worker holds this surface.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
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
  /// After a failed recovery attempt, sets <c>next_recovery_at</c> to apply the policy
  /// cooldown and returns the row to Pending.
  /// </summary>
  Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default);

  /// <summary>
  /// Generation-replay sweep: schedules every non-terminal DLQ row whose generation has
  /// not yet seen the current build for immediate retry. Idempotent (exactly-once per
  /// generation). Returns the number of rows scheduled.
  /// </summary>
  Task<int> ResetForGenerationAsync(string currentGeneration, CancellationToken ct = default);
}
