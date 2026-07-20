namespace Whizbang.Core.Temporal;

/// <summary>
/// The occurrence-level operations the pre-fire gate needs (as distinct from schedule management):
/// defer an in-flight occurrence, record a gate outcome in the run log, and write back a refreshed
/// authority snapshot.
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public interface IScheduleOccurrenceStore {
  /// <summary>
  /// Retry this same occurrence at <paramref name="until"/> instead of now: reschedules the pending
  /// message and releases its lease. The occurrence is NOT dropped and NOT re-created.
  /// </summary>
  Task DeferAsync(Guid occurrenceId, DateTimeOffset until, CancellationToken cancellationToken = default);

  /// <summary>Append a row to the run log (status: 0 Success, 1 Failed, 2 Skipped, 3 TriggeredEarly).</summary>
  Task LogRunAsync(Guid scheduleId, Guid occurrenceId, short status, string? note, CancellationToken cancellationToken = default);

  /// <summary>
  /// Write back a re-resolved authority snapshot, so subsequent fires of this schedule start from the
  /// fresh claims rather than the stale create-time ones.
  /// </summary>
  Task RefreshAuthorityClaimsAsync(Guid scheduleId, string claimsJson, CancellationToken cancellationToken = default);
}
