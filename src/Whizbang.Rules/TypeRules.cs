// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Rules;

/// <summary>
/// The declared eligibility rules of one event type (#2362): the fields that decide whether an event of the type should
/// exist, and so whether an integrity digest counts it. Two services that count a shared type must hold the same rules
/// for it; the pair guard compares <see cref="Hash"/> per shared type.
/// </summary>
/// <remarks>
/// Only rules are here, never timing: when a service physically deletes an event (its idle gate, its end-of-life
/// triggers, a rewind grace, a time to live) is local and may differ between services. What decides whether the event
/// is counted at all is a rule.
/// </remarks>
/// <param name="TypeName">The event type's name without its assembly (<c>Ns.Type</c>, <c>Ns.Outer+Nested</c>).</param>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Core.Tests/Rules/TypeRulesTests.cs</tests>
public sealed record TypeRules(string TypeName) {
  /// <summary>The type's ephemeral mode (<c>destruction;storage</c>), or null when it is sourced.</summary>
  public string? Ephemeral { get; init; }

  /// <summary>Whether the type is superseding (latest wins).</summary>
  public bool Superseding { get; init; }

  /// <summary>The superseding group, or null when the type is its own group.</summary>
  public string? SupersedingGroup { get; init; }

  /// <summary>The superseding key property, or null for one key per stream.</summary>
  public string? SupersedingKey { get; init; }

  /// <summary>Whether an event of the type ends its stream.</summary>
  public bool Terminal { get; init; }

  /// <summary>The end-of-life action of the streams the type opens, or null when it has no policy.</summary>
  public string? EndOfLife { get; init; }

  /// <summary>Whether no integrity digest ever counts the type (the audit system events).</summary>
  public bool DigestExempt { get; init; }

  /// <summary>The rules as one canonical line: every field, in a fixed order, so equal rules write equal text.</summary>
  /// <returns>The canonical text.</returns>
  public string Canonical() =>
    string.Create(CultureInfo.InvariantCulture,
      $"type={TypeName};ephemeral={Ephemeral};superseding={Superseding};group={SupersedingGroup};key={SupersedingKey};"
      + $"terminal={Terminal};end_of_life={EndOfLife};digest_exempt={DigestExempt}");

  /// <summary>The rules' hash: the first 16 lowercase hex digits of the SHA-256 of <see cref="Canonical"/>.</summary>
  /// <returns>The hash.</returns>
  public string Hash() => RuleHashing.Short(Canonical());
}
