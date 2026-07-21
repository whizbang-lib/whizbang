namespace Whizbang.Core.Temporal;

/// <summary>
/// What the engine does with fires that were missed while a schedule was paused or the owner was down.
/// Matches the <c>misfire_policy</c> column on <c>wh_schedules</c> (SMALLINT).
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public enum MisfirePolicy {
  /// <summary>Collapse all missed fires into a single catch-up occurrence (default, burst-safe).</summary>
  Coalesce = 0,

  /// <summary>Replay each missed occurrence (throttled + lookback-bounded — increment 5c).</summary>
  CatchUp = 1,

  /// <summary>Skip the missed window entirely; resume from the next scheduled fire.</summary>
  Skip = 2,
}
