// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <para>
/// By itself it changes nothing at run time: a stream still never holds both kinds (WHIZ133). Set
/// <see cref="Streams"/> to <see cref="MixedStreams.Allow"/> to let the pairs this read model applies share a stream
/// (#2255).
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute : Attribute {
  /// <summary>
  /// Whether one stream may hold the ephemeral and the Sourced events this read model applies.
  /// <see cref="MixedStreams.Refuse"/>, the default, keeps the runtime exactly as without the property. With
  /// <see cref="MixedStreams.Allow"/>, each (ephemeral type, Sourced type) pair the read model applies is allowed, but
  /// only when every read model that applies both types opts in the same way; otherwise the pair stays refused and
  /// WHIZ139 names the read model that did not opt in.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
  /// <tests>tests/Whizbang.Generators.Tests/MixedStreamAllowanceGeneratorTests.cs:Registry_AnAllowingReadModel_CarriesItsPairs_AndTheDefaultCarriesNoneAsync</tests>
  public MixedStreams Streams { get; set; } = MixedStreams.Refuse;
}
