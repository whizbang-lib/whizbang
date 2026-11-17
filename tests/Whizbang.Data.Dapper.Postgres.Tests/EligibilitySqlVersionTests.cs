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
  };

  [Test]
  public async Task TheEligibilityConstants_MatchTheGoldenHashOfTheirVersionAsync() {
    var text = $"__DIGEST_ELIGIBLE__={EligibilitySql.DIGEST_ELIGIBLE}\n"
      + $"__INTEGRITY_COUNTED__={EligibilitySql.INTEGRITY_COUNTED}\n"
      + $"__DIGEST_EXEMPT_TYPES__={EligibilitySql.DIGEST_EXEMPT_TYPES}\n";
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    await Assert.That(_goldenByVersion.ContainsKey(RuleSemantics.ELIGIBILITY_SQL_VERSION)).IsTrue()
      .Because("every eligibility SQL version records the hash of the rule it names");
    await Assert.That(hash).IsEqualTo(_goldenByVersion[RuleSemantics.ELIGIBILITY_SQL_VERSION])
      .Because("the SQL eligibility rule changed: bump RuleSemantics.ELIGIBILITY_SQL_VERSION and record the new hash, "
        + "so a mixed pair halts instead of comparing digests folded two ways");
  }

  /// <summary>
  /// #2343: the rules library holds the SQL rule's source; the migrations read the same text from constants.txt, which
  /// every substitution path (the runtime provider, the generated DbContext, the migration lint) reads. The two must
  /// say the same, so neither can change alone.
  /// </summary>
  [Test]
  public async Task TheMigrationConstants_AreTheRulesLibrarysSourceAsync() {
    await Assert.That(MigrationConstants.Tokens["__DIGEST_ELIGIBLE__"]).IsEqualTo(EligibilitySql.DIGEST_ELIGIBLE);
    await Assert.That(MigrationConstants.Tokens["__INTEGRITY_COUNTED__"]).IsEqualTo(EligibilitySql.INTEGRITY_COUNTED);
    await Assert.That(MigrationConstants.Tokens["__DIGEST_EXEMPT_TYPES__"]).IsEqualTo(EligibilitySql.DIGEST_EXEMPT_TYPES);
  }
}
