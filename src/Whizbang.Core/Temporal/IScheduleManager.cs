namespace Whizbang.Core.Temporal;

/// <summary>
/// The management API for schedules (DB = source of truth). Create / pause / resume / cancel are
/// mutations that any instance may issue; a later increment also rings the arm-on-mutation doorbell so
/// the owning instance re-arms its in-memory timer. Backed by <c>wh_create_schedule</c> /
/// <c>wh_transition_schedule</c>. Pause/resume/cancel take an optional expected version for optimistic
/// concurrency and return <c>false</c> when the row was not in a transitionable state or the version
/// did not match.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public interface IScheduleManager {
  /// <summary>Create a schedule, or idempotently update it when its <see cref="ScheduleDefinition.Key"/> exists.</summary>
  Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default);

  /// <summary>Pause an Active schedule. Returns whether the transition applied.</summary>
  Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default);

  /// <summary>Resume a Paused schedule. Returns whether the transition applied.</summary>
  Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default);

  /// <summary>Cancel a schedule (no future fires). Returns whether the transition applied.</summary>
  Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default);

  /// <summary>
  /// Fire an <b>extra</b> occurrence for a non-terminal schedule right now, <b>without disturbing its
  /// cadence</b> — <c>next_fire_at</c> / <c>last_fire_at</c> / <c>occurrence_count</c> are untouched, so
  /// the regular schedule continues exactly as it would have and the manual fire does not consume a
  /// <see cref="ScheduleDefinition.MaxOccurrences"/> slot. Logged as a <c>TriggeredEarly</c> run.
  /// Returns the spawned occurrence id, or <c>null</c> when the schedule is missing or terminal.
  /// </summary>
  Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Reconfigure a non-terminal schedule and recompute its next fire. Returns the recomputed next fire +
  /// new version, or <c>null</c> when the schedule is missing, terminal, or the expected version did not
  /// match. Identity / stream / event-type are immutable here — see <see cref="ScheduleUpdate"/>.
  /// </summary>
  Task<ScheduleUpdateResult?> UpdateAsync(
    Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken cancellationToken = default);
}
