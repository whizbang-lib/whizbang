// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The single place that decides whether an event is audited, and under what name.
/// </summary>
/// <remarks>
/// Shared because this decision was previously made in two files that had to agree and had no way
/// to. Duplicated decisions are how the audit paths came to disagree about scope; one implementation
/// removes the possibility rather than the symptom.
/// </remarks>
/// <docs>fundamentals/events/system-events#audit-decisions</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditDecisionHookTests.cs</tests>
public static class AuditEligibility {

  /// <summary>
  /// The <see cref="AuditEventAttribute"/> declared on <paramref name="eventType"/>, or null. The one place the
  /// attribute is read, so every audit writer takes its reason and level from the same source as the decision.
  /// </summary>
  /// <param name="eventType">The event's CLR type.</param>
  /// <returns>The attribute, or null when the type declares none.</returns>
  /// <remarks>Read from <see cref="AuditEventRegistry"/>, which the generator fills at module load (#1580).</remarks>
  internal static AuditEventAttribute? AttributeOf(Type eventType) => AuditEventRegistry.Find(eventType);

  /// <summary>
  /// Whether an audit record of <paramref name="eventType"/> keeps the event's body: when the type asks for it
  /// with <see cref="AuditEventAttribute.IncludeBody"/>, or when the default is <see cref="AuditBodyDefault.Include"/>.
  /// </summary>
  /// <param name="eventType">The event's CLR type, or null when it could not be resolved.</param>
  /// <param name="bodyDefault">The configured default.</param>
  /// <returns>True when the body is kept.</returns>
  internal static bool IncludesBody(Type? eventType, AuditBodyDefault bodyDefault) =>
    bodyDefault == AuditBodyDefault.Include || (eventType is not null && AttributeOf(eventType)?.IncludeBody == true);

  /// <summary>Decides whether this occurrence is audited, and how it should be labelled.</summary>
  /// <param name="payload">The event instance, or null when only the type is known.</param>
  /// <param name="eventType">The event's CLR type.</param>
  /// <param name="mode">OptOut audits everything not excluded; OptIn audits only what is marked.</param>
  /// <param name="hook">Optional per-occurrence hook. Null behaves exactly as before hooks existed.</param>
  /// <returns>The decision, carrying any name or description the hook supplied.</returns>
  public static AuditDecision Decide(
      object? payload, Type eventType, AuditMode mode, IAuditDecisionHook? hook) {
    ArgumentNullException.ThrowIfNull(eventType);

    // Auditing an audit record is an infinite loop. Checked before the hook so no hook and no mode
    // can re-open it.
    if (eventType == typeof(EventAudited) || eventType == typeof(CommandAudited)) {
      return AuditDecision.Skip;
    }

    var attr = AttributeOf(eventType);

    var eligibleByType = mode == AuditMode.OptOut
      ? attr?.Exclude != true    // audit unless excluded
      : attr?.Exclude == false;  // audit only if marked

    if (hook is null || payload is null) {
      return eligibleByType ? AuditDecision.Record() : AuditDecision.Skip;
    }

    var decision = hook.Decide(payload, eventType);

    // No opinion defers to the attribute. If this read as "skip", a hook added for one event type
    // would mute every other type it was asked about.
    return decision.Verdict switch {
      true => decision,
      false => AuditDecision.Skip,
      null => eligibleByType
        ? AuditDecision.Record(decision.Name, decision.Description)
        : AuditDecision.Skip,
    };
  }
}
