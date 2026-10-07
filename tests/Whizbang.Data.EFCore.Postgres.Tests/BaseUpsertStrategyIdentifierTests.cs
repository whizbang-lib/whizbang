// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The atomic upsert interpolates table and physical-field names into SQL, so it accepts only a plain,
/// unquoted Postgres identifier: a letter or underscore first, then letters, digits or underscores, at
/// most 63 characters. Each case sits on one edge of a character class, where an off-by-one would let a
/// character through that needs quoting.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[Category("Shard4")]
public class BaseUpsertStrategyIdentifierTests {
  [Test]
  [Arguments("a", true)]
  [Arguments("z", true)]
  [Arguments("A", true)]
  [Arguments("Z", true)]
  [Arguments("_", true)]
  [Arguments("wh_per_order_2", true)]
  [Arguments("`a", false)]   // just below 'a'
  [Arguments("{a", false)]   // just above 'z'
  [Arguments("@a", false)]   // just below 'A'
  [Arguments("[a", false)]   // just above 'Z'
  [Arguments("0a", false)]   // a digit first
  [Arguments("a`", false)]
  [Arguments("a{", false)]
  [Arguments("a@", false)]
  [Arguments("a[", false)]
  [Arguments("a/", false)]   // just below '0'
  [Arguments("a:", false)]   // just above '9'
  [Arguments("a b", false)]
  [Arguments("", false)]
  public async Task IsValidSqlIdentifier_AcceptsOnlyPlainIdentifiersAsync(string name, bool expected) {
    await Assert.That(BaseUpsertStrategy.IsValidSqlIdentifier(name)).IsEqualTo(expected);
  }

  [Test]
  public async Task IsValidSqlIdentifier_RefusesMoreThan63CharactersAsync() {
    await Assert.That(BaseUpsertStrategy.IsValidSqlIdentifier(new string('a', 63))).IsTrue();
    await Assert.That(BaseUpsertStrategy.IsValidSqlIdentifier(new string('a', 64))).IsFalse()
      .Because("Postgres truncates identifiers past 63 bytes, so two long names could collide");
  }
}
