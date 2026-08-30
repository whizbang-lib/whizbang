namespace Whizbang.Core.SystemEvents;

/// <summary>
/// One hook's verdict on whether a particular occurrence of an event should be audited, and what to
/// call it.
/// </summary>
/// <remarks>
/// <para>
/// Three-state on purpose. A bool cannot distinguish "skip this occurrence" from "no opinion, use
/// the attribute", and collapsing those makes a hook written for one event type silently suppress
/// every other type it is asked about.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#audit-decisions</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditDecisionHookTests.cs</tests>
public readonly record struct AuditDecision {

  /// <summary>What the hook decided: true record, false skip, null no opinion.</summary>
  public bool? Verdict { get; private init; }

  /// <summary>
  /// True when this occurrence is to be recorded. Distinct from <see cref="Verdict"/>: a hook's
  /// "no opinion" is not an answer, and only <see cref="AuditEligibility"/> resolves it against the
  /// attribute. Callers act on this; hooks return the three-state.
  /// </summary>
  public bool ShouldAudit => Verdict == true;

  /// <summary>Human-readable name for this occurrence, or null to use the default humanizer.</summary>
  public string? Name { get; private init; }

  /// <summary>Human-readable description for this occurrence, or null for the default.</summary>
  public string? Description { get; private init; }

  /// <summary>Record this occurrence, optionally naming it.</summary>
  /// <param name="name">Activity name, e.g. "Bulk record import".</param>
  /// <param name="description">Detail drawn from the payload, e.g. "Imported 500 records".</param>
  /// <returns>A decision to audit.</returns>
  public static AuditDecision Record(string? name = null, string? description = null) =>
    new() { Verdict = true, Name = name, Description = description };

  /// <summary>Do not record this occurrence, even though its type is auditable.</summary>
  public static AuditDecision Skip => new() { Verdict = false };

  /// <summary>Defer to the attribute. The default, and what an unhandled case must return.</summary>
  public static AuditDecision NoOpinion => default;
}

/// <summary>
/// Decides whether a specific OCCURRENCE of an auditable event is worth recording.
/// </summary>
/// <remarks>
/// <para>
/// <c>[AuditEvent]</c> answers whether a TYPE is ever worth auditing. Some decisions cannot be made
/// from the type alone:
/// </para>
/// <list type="bullet">
///   <item>
///     The same edit event is emitted whether a person changed one record or an import wrote ten
///     thousand. Only the first belongs in an audit trail, and the two differ only by a payload flag.
///   </item>
///   <item>
///     A bulk operation should read as one line — "a person imported 500 records" — which requires
///     counting something in the payload.
///   </item>
///   <item>
///     A saga's boundaries are worth recording as the ACTIVITY they represent. "SagaStartedEvent"
///     tells an auditor nothing; the activity name does.
///   </item>
/// </list>
/// <para>
/// This is why the existing name and description humanizers do not suffice: they are keyed on the
/// type NAME and never see the instance, so they cannot count, name from data, or veto.
/// </para>
/// <para>
/// A hook is additive. Returning <see cref="AuditDecision.NoOpinion"/> leaves behavior exactly as it
/// would be with no hook registered, so a hook written for one event type does not change the
/// treatment of any other.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#audit-decisions</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditDecisionHookTests.cs</tests>
public interface IAuditDecisionHook {

  /// <summary>Decides whether this occurrence should be audited.</summary>
  /// <param name="payload">The event instance. Cast to the types this hook handles.</param>
  /// <param name="eventType">The event's CLR type.</param>
  /// <returns>
  /// <see cref="AuditDecision.Record"/>, <see cref="AuditDecision.Skip"/>, or
  /// <see cref="AuditDecision.NoOpinion"/> for anything this hook does not handle.
  /// </returns>
  AuditDecision Decide(object payload, Type eventType);
}

/// <summary>
/// The shipped default hook: no opinion on any occurrence.
/// </summary>
/// <remarks>
/// <para>
/// An audit decision hook has a genuinely correct behavior when an application supplies none:
/// express no opinion and let the attribute decide, which is exactly how auditing behaved before
/// hooks existed. That makes this a safe inert default, so the constructor parameter can be
/// required and a hand-construction can no longer silently drop the hook.
/// </para>
/// <para>
/// Registered with TryAdd, so an application's own hook wins simply by being registered.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditDecisionHookTests.cs</tests>
public sealed class NoOpinionAuditDecisionHook : IAuditDecisionHook {

  /// <summary>A shared instance; the type is stateless.</summary>
  public static readonly NoOpinionAuditDecisionHook Instance = new();

  /// <inheritdoc />
  public AuditDecision Decide(object payload, Type eventType) => AuditDecision.NoOpinion;
}
