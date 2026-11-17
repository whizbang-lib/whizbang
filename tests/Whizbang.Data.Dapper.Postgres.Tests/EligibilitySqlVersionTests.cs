// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Rules;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2362: the SQL eligibility rule travels in every rule fingerprint as <see cref="RuleSemantics.ELIGIBILITY_SQL_VERSION"/>,
/// so two services whose digest folds count differently halt their pair. This guard holds the constants' text to a
/// golden hash recorded beside the version: a change to the rule's SQL fails here until the version is bumped and the
/// hash recorded with it.
/// </summary>
[Category("Migrations")]
public class EligibilitySqlVersionTests {
  /// <summary>The version each golden hash was recorded with. Add a row when the rule's SQL changes; never edit one.</summary>
  private static readonly Dictionary<int, string> _goldenByVersion = new() {
    [1] = "c3b2cbadd898f6460cd55ce17963be5c8b9331e4bc3f73d8d926da651d79eb5c",
  };

  private static readonly string[] _ruleTokens = ["__DIGEST_ELIGIBLE__", "__INTEGRITY_COUNTED__", "__DIGEST_EXEMPT_TYPES__"];

  [Test]
  public async Task TheEligibilityConstants_MatchTheGoldenHashOfTheirVersionAsync() {
    var text = string.Concat(_ruleTokens.Select(t => $"{t}={MigrationConstants.Tokens[t]}\n"));
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    await Assert.That(_goldenByVersion.ContainsKey(RuleSemantics.ELIGIBILITY_SQL_VERSION)).IsTrue()
      .Because("every eligibility SQL version records the hash of the rule it names");
    await Assert.That(hash).IsEqualTo(_goldenByVersion[RuleSemantics.ELIGIBILITY_SQL_VERSION])
      .Because("the SQL eligibility rule changed: bump RuleSemantics.ELIGIBILITY_SQL_VERSION and record the new hash, "
        + "so a mixed pair halts instead of comparing digests folded two ways");
  }
}
