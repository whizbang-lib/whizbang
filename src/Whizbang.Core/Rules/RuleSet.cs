// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Whizbang.Core.Rules;

/// <summary>
/// The eligibility rules one service applies to a set of event types (#2362): the rule-semantics version, the SQL
/// eligibility rule's version and each type's rule hash, keyed by the type's name without its assembly.
/// </summary>
/// <param name="SemanticsVersion">The service's <see cref="RuleSemantics.VERSION"/>.</param>
/// <param name="EligibilitySqlVersion">The service's <see cref="RuleSemantics.ELIGIBILITY_SQL_VERSION"/>.</param>
/// <param name="TypeHashes">Each covered type's <see cref="TypeRules.Hash"/>, by type name.</param>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Core.Tests/Rules/RuleSetTests.cs</tests>
public sealed record RuleSet(int SemanticsVersion, int EligibilitySqlVersion, IReadOnlyDictionary<string, string> TypeHashes) {
  /// <summary>
  /// The fingerprint of the set: the first 16 lowercase hex digits of the SHA-256 of both versions and every type's
  /// name and hash, in ordinal type order. Equal rule sets have equal fingerprints whatever order they were built in.
  /// </summary>
  /// <returns>The fingerprint.</returns>
  public string Fingerprint() {
    var text = new StringBuilder();
    text.Append(CultureInfo.InvariantCulture, $"semantics={SemanticsVersion};sql={EligibilitySqlVersion}");
    foreach (var (type, hash) in TypeHashes.OrderBy(t => t.Key, StringComparer.Ordinal)) {
      text.Append(CultureInfo.InvariantCulture, $"\n{type}={hash}");
    }
    return RuleHashing.Short(text.ToString());
  }

  /// <summary>
  /// Compares this service's rules with a peer's over the types the two share (#2362, owner decision Q3): only a type
  /// both sets name is compared, so a rule change on a type the pair does not share halts nothing. A peer that sent no
  /// rules (a service on a release that predates the fingerprint) is a mismatch: it may count differently.
  /// </summary>
  /// <param name="peer">The peer's rules, or null when it sent none.</param>
  /// <returns>Whether the pair agrees and, when not, why and on which types.</returns>
  public RulePairVerdict CompareWith(RuleSet? peer) {
    if (peer is null) {
      return new RulePairVerdict(RulePairHaltReason.NoFingerprint, []);
    }
    if (peer.SemanticsVersion != SemanticsVersion) {
      return new RulePairVerdict(RulePairHaltReason.SemanticsVersion, []);
    }
    if (peer.EligibilitySqlVersion != EligibilitySqlVersion) {
      return new RulePairVerdict(RulePairHaltReason.EligibilitySqlVersion, []);
    }
    List<string> differing = [.. TypeHashes
      .Where(mine => peer.TypeHashes.TryGetValue(mine.Key, out var theirs) && !string.Equals(theirs, mine.Value, StringComparison.Ordinal))
      .Select(mine => mine.Key)
      .Order(StringComparer.Ordinal)];
    return differing.Count == 0 ? RulePairVerdict.Agrees : new RulePairVerdict(RulePairHaltReason.TypeRules, differing);
  }
}

/// <summary>Why a service pair's integrity is halted (#2362).</summary>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
public enum RulePairHaltReason {
  /// <summary>Not halted: the pair applies the same rules to the types it shares.</summary>
  None = 0,

  /// <summary>The peer sent no fingerprint: it runs a release that predates it, and may count differently.</summary>
  NoFingerprint = 1,

  /// <summary>The two run different rule-semantics versions.</summary>
  SemanticsVersion = 2,

  /// <summary>The two run different SQL eligibility rules.</summary>
  EligibilitySqlVersion = 3,

  /// <summary>The two declare different rules for at least one type they share.</summary>
  TypeRules = 4,
}

/// <summary>The result of comparing two services' rules (#2362).</summary>
/// <param name="Reason">Why the pair is halted; <see cref="RulePairHaltReason.None"/> when it agrees.</param>
/// <param name="DifferingTypes">The shared types whose rules differ, in ordinal order; empty unless
/// <paramref name="Reason"/> is <see cref="RulePairHaltReason.TypeRules"/>.</param>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
public sealed record RulePairVerdict(RulePairHaltReason Reason, IReadOnlyList<string> DifferingTypes) {
  /// <summary>The verdict for a pair that agrees.</summary>
  public static RulePairVerdict Agrees { get; } = new(RulePairHaltReason.None, []);

  /// <summary>True when the pair applies the same rules to the types it shares.</summary>
  public bool IsAgreed => Reason == RulePairHaltReason.None;
}
