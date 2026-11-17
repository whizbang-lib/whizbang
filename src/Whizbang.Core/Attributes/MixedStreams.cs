// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Whether a read model opted into mixing (<see cref="DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute"/>) also
/// lets one stream hold the ephemeral and the Sourced events it applies (#2255).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/MixedStreamAllowanceTests.cs:Allowed_TheDefaultRefuse_AllowsNothingAsync</tests>
public enum MixedStreams {
  /// <summary>
  /// The default: the attribute only silences WHIZ130. A stream still never holds both kinds (WHIZ133), and the history
  /// reclassify keeps treating the read model's durable inputs as blockers.
  /// </summary>
  Refuse = 0,

  /// <summary>
  /// Each (ephemeral type, Sourced type) pair the read model applies may share a stream, provided every read model that
  /// applies both opts in the same way: either kind may be appended to a stream holding the other, the history
  /// reclassify reclassifies the ephemeral events in such streams and keeps the Sourced ones, and the reaper reaps the
  /// ephemeral bodies on their normal rules.
  /// </summary>
  Allow = 1,
}
