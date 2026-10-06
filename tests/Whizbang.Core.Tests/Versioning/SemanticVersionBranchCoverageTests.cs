// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Versioning;

/// <summary>
/// Branch coverage for <see cref="SemanticVersion.CompareTo"/>'s pre-release rules, from the side the
/// existing cases never take: every existing comparison puts the higher-ranked version on the LEFT.
/// These put it on the right, so the "left ranks lower" outcome of each rule is pinned too: a shorter
/// identifier list below a longer one with the same prefix, and a numeric identifier below an
/// alphanumeric one.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Versioning/SemanticVersion.cs</code-under-test>
[Category("Versioning")]
public class SemanticVersionBranchCoverageTests {
  private static SemanticVersion _parse(string text) {
    if (!SemanticVersion.TryParse(text, out var version)) {
      throw new InvalidOperationException($"expected '{text}' to parse");
    }
    return version;
  }

  [Test]
  public async Task CompareTo_FewerIdentifiersOnTheLeft_RanksBelowAsync() {
    var comparison = _parse("1.0.0-alpha").CompareTo(_parse("1.0.0-alpha.1"));

    await Assert.That(comparison).IsEqualTo(-1)
      .Because("with an equal prefix, the shorter pre-release identifier list has lower precedence");
  }

  [Test]
  public async Task CompareTo_NumericIdentifierOnTheLeft_RanksBelowAlphanumericAsync() {
    var comparison = _parse("1.0.0-1").CompareTo(_parse("1.0.0-alpha"));

    await Assert.That(comparison).IsEqualTo(-1)
      .Because("a numeric identifier always has lower precedence than an alphanumeric one");
  }
}
