namespace Whizbang.Core.Temporal;

/// <summary>
/// Provider seam for the temporal engine's authoritative fire. An implementation claims due Active
/// schedules owned by this instance and, per schedule, spawns the occurrence + advances
/// <c>next_fire_at</c> + logs the run in one atomic unit (the Postgres provider calls
/// <c>wh_claim_due_schedules</c>). Returns the number of schedules fired this call.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public interface IScheduleClaimer {
  /// <summary>
  /// Claim and fire up to <paramref name="limit"/> due schedules for this instance. Returns the count
  /// fired (0 when nothing is due). The caller drains by repeating while the count equals the limit.
  /// </summary>
  Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default);
}
