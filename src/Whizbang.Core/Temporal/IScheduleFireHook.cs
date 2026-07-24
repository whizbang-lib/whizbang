namespace Whizbang.Core.Temporal;

/// <summary>
/// What the developer knows about an occurrence that is <em>about to run</em>.
/// </summary>
/// <param name="ScheduleId">The schedule this occurrence belongs to.</param>
/// <param name="OccurrenceId">This occurrence's id (the outbox message id).</param>
/// <param name="OccurrenceNumber">Which occurrence of the schedule this is.</param>
/// <param name="AuthorityPrincipalId">The principal the occurrence runs as (captured at schedule time).</param>
/// <param name="AuthorityClaimsJson">The <b>snapshot</b> of that principal's claims, taken at create time — it may be stale.</param>
/// <param name="EventType">The occurrence event type.</param>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public readonly record struct ScheduleFireContext(
  Guid ScheduleId,
  Guid OccurrenceId,
  long OccurrenceNumber,
  Guid AuthorityPrincipalId,
  string? AuthorityClaimsJson,
  string EventType);

/// <summary>How the engine should treat this occurrence.</summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public enum FireAction {
  /// <summary>Run it.</summary>
  Proceed = 0,

  /// <summary>Drop <em>this</em> occurrence only; the schedule keeps its cadence.</summary>
  Skip = 1,

  /// <summary>Drop this occurrence <em>and</em> void the schedule (no future fires).</summary>
  Cancel = 2,

  /// <summary>Don't run it now — retry the same occurrence at <see cref="FireDecision.DeferUntil"/>.</summary>
  Defer = 3,
}

/// <summary>
/// The hook's verdict. Use the factory members rather than constructing directly.
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public readonly record struct FireDecision {
  /// <summary>What to do.</summary>
  public FireAction Action { get; private init; }

  /// <summary>When to retry, for <see cref="FireAction.Defer"/>.</summary>
  public DateTimeOffset? DeferUntil { get; private init; }

  /// <summary>
  /// Optional re-resolved claims for the run-as principal. The framework can't know how to re-resolve an
  /// arbitrary principal, so this is the seam where the developer refreshes the stale create-time snapshot;
  /// when supplied it is written back to the schedule so subsequent fires start from the fresh snapshot.
  /// </summary>
  public string? RefreshedAuthorityClaimsJson { get; private init; }

  /// <summary>Run the occurrence, optionally refreshing the stored authority snapshot.</summary>
  public static FireDecision Proceed(string? refreshedAuthorityClaimsJson = null) =>
    new() { Action = FireAction.Proceed, RefreshedAuthorityClaimsJson = refreshedAuthorityClaimsJson };

  /// <summary>Drop this occurrence; leave the schedule running.</summary>
  public static FireDecision Skip() => new() { Action = FireAction.Skip };

  /// <summary>Drop this occurrence and void the schedule.</summary>
  public static FireDecision Cancel() => new() { Action = FireAction.Cancel };

  /// <summary>Retry this same occurrence at <paramref name="until"/>.</summary>
  public static FireDecision Defer(DateTimeOffset until) =>
    new() { Action = FireAction.Defer, DeferUntil = until };
}

/// <summary>
/// The <b>pre-fire hook</b>: developer code that runs immediately before a scheduled occurrence executes.
/// This is the dial for everything situational about an async fire — above all <em>security</em>: the
/// occurrence carries a run-as principal captured (and snapshotted) at schedule time, and by the time it
/// fires that principal may have been deactivated, had roles revoked, or left the company. Only the
/// application knows what that should mean, so it decides here: <see cref="FireDecision.Proceed"/> (with
/// optionally refreshed claims), <see cref="FireDecision.Skip"/>, <see cref="FireDecision.Cancel"/>, or
/// <see cref="FireDecision.Defer"/>.
/// <para>
/// Registering a hook is optional — with none registered, occurrences run unchanged.
/// </para>
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public interface IScheduleFireHook {
  /// <summary>Decide whether (and as whom) this occurrence should run.</summary>
  ValueTask<FireDecision> OnBeforeFireAsync(ScheduleFireContext context, CancellationToken cancellationToken = default);
}
