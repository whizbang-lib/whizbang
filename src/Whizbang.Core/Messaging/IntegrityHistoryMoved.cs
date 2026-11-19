// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Why an origin's history moved since the generation an asker knows (#2365), as an answer says it
/// (<see cref="IntegrityManifest.HistoryMovedReason"/>) and as the asker counts the seal it lowers.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-instruments</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs:RequestReceptor_SaysWhyHistoryMoved_SinceTheGenerationTheAskerKnowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs:ManifestReceptor_GenerationChanged_CountsTheSealLoweredByTheOriginsReasonAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TwoServiceHistoryMovedReasonTests.cs</tests>
public static class IntegrityHistoryMoved {
#pragma warning disable CA1707
  /// <summary>The origin rewrote history under the same eligibility rules.</summary>
  public const string REWRITE = "rewrite";

  /// <summary>The origin's eligibility rules changed after the generation the asker knows.</summary>
  public const string RULE_CHANGE = "rule_change";

  /// <summary>The answer gave no reason this release knows: an origin on an older release, or a newer reason.</summary>
  public const string UNKNOWN = "unknown";
#pragma warning restore CA1707

  /// <summary>
  /// The reason an origin gives: none when the asker named no generation or knows the current one; a rule change when
  /// the rules last changed after the generation the asker knows; a rewrite otherwise.
  /// </summary>
  /// <param name="knownGeneration">The generation the ask named.</param>
  /// <param name="currentGeneration">The origin's generation now.</param>
  /// <param name="rulesChangedAtGeneration">The generation the origin's rules last changed at; null when never noted.</param>
  /// <returns>The reason, or null.</returns>
  public static string? Reason(long? knownGeneration, long currentGeneration, long? rulesChangedAtGeneration) {
    if (knownGeneration is not { } known || known >= currentGeneration) {
      return null;
    }
    return rulesChangedAtGeneration > known ? RULE_CHANGE : REWRITE;
  }

  /// <summary>The reason an asker counts for an answer's <paramref name="reason"/>: a known one, or unknown.</summary>
  /// <param name="reason">The answer's reason, or null.</param>
  /// <returns><see cref="REWRITE"/>, <see cref="RULE_CHANGE"/> or <see cref="UNKNOWN"/>.</returns>
  public static string Counted(string? reason) => reason switch {
    REWRITE => REWRITE,
    RULE_CHANGE => RULE_CHANGE,
    _ => UNKNOWN,
  };
}
