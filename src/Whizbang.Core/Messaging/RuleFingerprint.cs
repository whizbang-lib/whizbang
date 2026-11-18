// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Rules;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The eligibility rules a service applies to the event types an integrity message is about (#2362), as the message
/// carries them: both rule versions, the fingerprint, and each covered type's rule hash. Every manifest request,
/// manifest, checkpoint and redelivery request carries one; the receiver compares it with its own rules over the types
/// both name and halts the pair on a mismatch. A message without one comes from a release that predates it and is a
/// mismatch too.
/// </summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RuleFingerprintWireTests.cs</tests>
public sealed record RuleFingerprint {
  /// <summary>The sender's <see cref="RuleSemantics.VERSION"/>.</summary>
  public required int SemanticsVersion { get; init; }

  /// <summary>The sender's <see cref="RuleSemantics.ELIGIBILITY_SQL_VERSION"/>.</summary>
  public required int EligibilitySqlVersion { get; init; }

  /// <summary>The fingerprint over the covered types (<see cref="RuleSet.Fingerprint"/>), for logs and reports.</summary>
  public required string Fingerprint { get; init; }

  /// <summary>Each covered type the sender knows, with its rule hash.</summary>
  public List<TypeRuleHash> Types { get; init; } = [];

  /// <summary>The wire form of a rule set.</summary>
  /// <param name="rules">The rules.</param>
  /// <returns>The fingerprint the message carries.</returns>
  public static RuleFingerprint From(RuleSet rules) {
    ArgumentNullException.ThrowIfNull(rules);
    return new RuleFingerprint {
      SemanticsVersion = rules.SemanticsVersion,
      EligibilitySqlVersion = rules.EligibilitySqlVersion,
      Fingerprint = rules.Fingerprint(),
      Types = [.. rules.TypeHashes.OrderBy(t => t.Key, StringComparer.Ordinal)
        .Select(t => new TypeRuleHash { Type = t.Key, Hash = t.Value })],
    };
  }

  /// <summary>The rule set the message carried, for the comparison.</summary>
  /// <returns>The rules.</returns>
  public RuleSet ToRuleSet() {
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var type in Types) {
      hashes[type.Type] = type.Hash;
    }
    return new RuleSet(SemanticsVersion, EligibilitySqlVersion, hashes);
  }
}

/// <summary>One event type's rule hash on the wire (#2362).</summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
public sealed record TypeRuleHash {
  /// <summary>The type's name without its assembly.</summary>
  public required string Type { get; init; }

  /// <summary>Its <see cref="TypeRules.Hash"/>.</summary>
  public required string Hash { get; init; }
}
