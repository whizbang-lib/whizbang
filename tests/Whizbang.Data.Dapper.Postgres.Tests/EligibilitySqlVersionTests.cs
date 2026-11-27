// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Rules;

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
    // #2588: an operator's exclusion (EventFlags.IntegrityExcluded, 256) is counted by no digest and no checkpoint.
    [2] = "35131e86c253eee6faddb03e39da35b88e752c34fe52cc21cf8dbffce2573f4e",
  };

  private static string _hashOf(string digestEligible, string integrityCounted) {
    var text = $"__DIGEST_ELIGIBLE__={digestEligible}\n"
      + $"__INTEGRITY_COUNTED__={integrityCounted}\n"
      + $"__DIGEST_EXEMPT_TYPES__={EligibilitySql.DIGEST_EXEMPT_TYPES}\n";
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
  }

  [Test]
  public async Task TheEligibilityConstants_MatchTheGoldenHashOfTheirVersionAsync() {
    var hash = _hashOf(EligibilitySql.DIGEST_ELIGIBLE, EligibilitySql.INTEGRITY_COUNTED);

    await Assert.That(_goldenByVersion.ContainsKey(RuleSemantics.ELIGIBILITY_SQL_VERSION)).IsTrue()
      .Because("every eligibility SQL version records the hash of the rule it names");
    await Assert.That(hash).IsEqualTo(_goldenByVersion[RuleSemantics.ELIGIBILITY_SQL_VERSION])
      .Because("the SQL eligibility rule changed: bump RuleSemantics.ELIGIBILITY_SQL_VERSION and record the new hash, "
        + "so a mixed pair halts instead of comparing digests folded two ways");
  }

  /// <summary>
  /// #2588: the shipped migrations write version 1 of the rule (<c>__DIGEST_ELIGIBLE__</c>), and a migration is never
  /// edited, so that text can never change: changing it would change the hash of every shipped file that writes it and
  /// re-run them all. Version 2 is written as its own tokens by the migration that redefines every reader.
  /// </summary>
  [Test]
  public async Task TheVersionOneRule_IsFrozen_AndTheCurrentRuleIsVersionTwoAsync() {
    await Assert.That(_hashOf(EligibilitySql.DIGEST_ELIGIBLE_V1, EligibilitySql.INTEGRITY_COUNTED_V1)).IsEqualTo(_goldenByVersion[1])
      .Because("the shipped migrations write this text; it is frozen");
    await Assert.That(RuleSemantics.ELIGIBILITY_SQL_VERSION).IsEqualTo(2)
      .Because("the rule tests the integrity-excluded flag (#2588)");
  }

  /// <summary>
  /// #2343: the rules library holds the SQL rule's source; the migrations read the same text from constants.txt, which
  /// every substitution path (the runtime provider, the generated DbContext, the migration lint) reads. The two must
  /// say the same, so neither can change alone.
  /// </summary>
  [Test]
  public async Task TheMigrationConstants_AreTheRulesLibrarysSourceAsync() {
    await Assert.That(MigrationConstants.Tokens["__DIGEST_ELIGIBLE_V2__"]).IsEqualTo(EligibilitySql.DIGEST_ELIGIBLE);
    await Assert.That(MigrationConstants.Tokens["__INTEGRITY_COUNTED_V2__"]).IsEqualTo(EligibilitySql.INTEGRITY_COUNTED);
    await Assert.That(MigrationConstants.Tokens["__DIGEST_ELIGIBLE__"]).IsEqualTo(EligibilitySql.DIGEST_ELIGIBLE_V1);
    await Assert.That(MigrationConstants.Tokens["__INTEGRITY_COUNTED__"]).IsEqualTo(EligibilitySql.INTEGRITY_COUNTED_V1);
    await Assert.That(MigrationConstants.Tokens["__DIGEST_EXEMPT_TYPES__"]).IsEqualTo(EligibilitySql.DIGEST_EXEMPT_TYPES);
  }
}
