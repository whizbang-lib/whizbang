// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// A scalar a SQL function returns reads as the typed value it carries, and as false or zero when the
/// driver hands back null, DBNull or another type: the stores' SQL always returns a value, so these
/// fallbacks are the one place the "no value" answer is decided.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/ScalarResult.cs</code-under-test>
public class ScalarResultTests {
  [Test]
  public async Task IsTrue_OnlyABooleanTrueIsTrueAsync() {
    await Assert.That(ScalarResult.IsTrue(true)).IsTrue();
    await Assert.That(ScalarResult.IsTrue(false)).IsFalse();
    await Assert.That(ScalarResult.IsTrue(null)).IsFalse();
    await Assert.That(ScalarResult.IsTrue(DBNull.Value)).IsFalse();
    await Assert.That(ScalarResult.IsTrue("true")).IsFalse();
  }

  [Test]
  public async Task IntOrZero_AnIntIsItself_AndAnythingElseIsZeroAsync() {
    await Assert.That(ScalarResult.IntOrZero(42)).IsEqualTo(42);
    await Assert.That(ScalarResult.IntOrZero(null)).IsEqualTo(0);
    await Assert.That(ScalarResult.IntOrZero(DBNull.Value)).IsEqualTo(0);
    await Assert.That(ScalarResult.IntOrZero(42L)).IsEqualTo(0)
      .Because("the read is exact: a bigint result is a different SQL contract, not a value to coerce");
  }

  [Test]
  public async Task LongOrZero_ALongIsItself_AndAnythingElseIsZeroAsync() {
    await Assert.That(ScalarResult.LongOrZero(42L)).IsEqualTo(42L);
    await Assert.That(ScalarResult.LongOrZero(null)).IsEqualTo(0L);
    await Assert.That(ScalarResult.LongOrZero(DBNull.Value)).IsEqualTo(0L);
    await Assert.That(ScalarResult.LongOrZero(42)).IsEqualTo(0L);
  }

  [Test]
  public async Task InstantOrNull_ATimestampIsItsUtcInstant_AndAnythingElseIsNullAsync() {
    var at = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    var offset = new DateTimeOffset(2026, 11, 23, 7, 0, 0, TimeSpan.FromHours(-5));

    await Assert.That(ScalarResult.InstantOrNull(at)).IsEqualTo(new DateTimeOffset(at));
    await Assert.That(ScalarResult.InstantOrNull(DateTime.SpecifyKind(at, DateTimeKind.Unspecified)))
      .IsEqualTo(new DateTimeOffset(at))
      .Because("a timestamptz the driver hands back without a kind is still UTC");
    await Assert.That(ScalarResult.InstantOrNull(offset)).IsEqualTo(offset);
    await Assert.That(ScalarResult.InstantOrNull(null)).IsNull();
    await Assert.That(ScalarResult.InstantOrNull(DBNull.Value)).IsNull();
  }
}
