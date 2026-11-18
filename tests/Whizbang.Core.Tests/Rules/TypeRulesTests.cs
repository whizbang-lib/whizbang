// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Rules;

namespace Whizbang.Core.Tests.Rules;

/// <summary>
/// #2362: a type's rules hash every rule field, so a change to any of them changes the hash, and equal rules hash equal.
/// </summary>
[Category("Rules")]
public class TypeRulesTests {
  private static readonly TypeRules _base = new("Contracts.OrderPlaced");

  [Test]
  public async Task Hash_IsSixteenLowercaseHexDigits_AndEqualRulesHashEqualAsync() {
    var hash = _base.Hash();

    await Assert.That(hash.Length).IsEqualTo(16);
    await Assert.That(hash.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c))).IsTrue();
    await Assert.That(new TypeRules("Contracts.OrderPlaced").Hash()).IsEqualTo(hash);
  }

  [Test]
  public async Task Canonical_NamesEveryFieldInAFixedOrderAsync() {
    var rules = new TypeRules("Contracts.OrderPlaced") {
      Ephemeral = "WhenConsumed;Memory",
      Superseding = true,
      SupersedingGroup = "g",
      SupersedingKey = "Model",
      Terminal = true,
      EndOfLife = "Archive",
      DigestExempt = true,
    };

    await Assert.That(rules.Canonical()).IsEqualTo(
      "type=Contracts.OrderPlaced;ephemeral=WhenConsumed;Memory;superseding=True;group=g;key=Model;terminal=True;"
      + "end_of_life=Archive;digest_exempt=True");
    await Assert.That(_base.Canonical()).IsEqualTo(
      "type=Contracts.OrderPlaced;ephemeral=;superseding=False;group=;key=;terminal=False;end_of_life=;digest_exempt=False");
  }

  /// <summary>Each rule field, changed alone, changes the hash: a rule a fingerprint ignored could differ unseen.</summary>
  [Test]
  public async Task Hash_ChangesWithEveryRuleFieldAsync() {
    TypeRules[] variants = [
      _base with { TypeName = "Contracts.Other" },
      _base with { Ephemeral = "WhenConsumed;Memory" },
      _base with { Superseding = true },
      _base with { SupersedingGroup = "g" },
      _base with { SupersedingKey = "Model" },
      _base with { Terminal = true },
      _base with { EndOfLife = "Archive" },
      _base with { DigestExempt = true },
    ];

    var hashes = variants.Select(v => v.Hash()).Append(_base.Hash()).ToList();

    await Assert.That(hashes.Distinct().Count()).IsEqualTo(hashes.Count);
  }
}
