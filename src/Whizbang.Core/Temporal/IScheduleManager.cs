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
}
