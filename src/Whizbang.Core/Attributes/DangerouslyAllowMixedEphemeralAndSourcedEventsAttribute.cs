namespace Whizbang.Core.Attributes;

/// <summary>
/// Explicit, in-code opt-in that silences <c>WHIZ130</c> on a perspective that deliberately applies both
/// ephemeral and Sourced events. Applying it is a decision: you accept that this perspective is neither a
/// pure rebuildable cache (Sourced) nor pure authoritative state (ephemeral), and you own the consequences
/// — it cannot be safely rebuilt from a log, and its ephemeral inputs self-destruct.
/// </summary>
/// <remarks>
/// <para>
/// The name is intentionally long and alarming (à la <c>dangerouslySetInnerHTML</c>): nobody types it by
/// accident, it shows up in code review as a named choice, and it is trivially greppable.
/// </para>
/// <para>
/// It exists because a <c>#pragma warning disable WHIZ130</c> or an <c>.editorconfig</c> override is
/// unavailable to teams that run warnings-as-errors with a no-suppression policy — such a team would
/// otherwise be hard-blocked from ever intentionally building a mixed-mode perspective. This attribute is
/// a first-class escape hatch that survives those policies while keeping the decision explicit and local.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute : Attribute;
