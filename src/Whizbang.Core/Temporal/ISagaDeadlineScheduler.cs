namespace Whizbang.Core.Temporal;

/// <summary>
/// Saga / process-manager deadlines ("if no response by T, fire X"). A deadline is simply a <b>keyed
/// one-shot schedule</b> on the saga's stream, so it reuses the whole temporal engine — exactly-once
/// occurrence creation, the leased DB claim, the precise in-memory timer, arm-on-mutation, and the run
/// log — rather than introducing a second timing mechanism.
/// <para>
/// The schedule id is <b>derived deterministically</b> from (saga stream, deadline name), so setting the
/// same deadline again is idempotent (it re-arms / moves the deadline rather than stacking duplicates),
/// and cancelling needs no bookkeeping on the caller's side.
/// </para>
/// </summary>
/// <docs>fundamentals/temporal/saga-deadlines</docs>
public interface ISagaDeadlineScheduler {
  /// <summary>
  /// Arm (or move) a deadline: at <paramref name="at"/>, spawn <paramref name="eventType"/> on the saga's
  /// stream. Re-arming the same (saga, name) replaces the existing deadline in place.
  /// </summary>
  Task<ScheduleHandle> SetDeadlineAsync(
    Guid sagaStreamId,
    string deadlineName,
    DateTimeOffset at,
    string eventType,
    string? eventDataJson = null,
    string? scopeJson = null,
    CancellationToken cancellationToken = default);

  /// <summary>Cancel a deadline (the saga completed in time). Returns whether it was still cancellable.</summary>
  Task<bool> CancelDeadlineAsync(Guid sagaStreamId, string deadlineName, CancellationToken cancellationToken = default);

  /// <summary>The deterministic schedule id for a (saga, deadline-name) pair — for ops/diagnostics.</summary>
  Guid DeadlineScheduleId(Guid sagaStreamId, string deadlineName);
}
