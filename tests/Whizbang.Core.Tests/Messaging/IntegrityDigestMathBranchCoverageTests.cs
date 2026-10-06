// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="IntegrityDigestMath.IsInsideSettle"/>: either side's bucket
/// changing inside the settle window holds the comparison back, and a null update time (a
/// recomputed row) never does. The times sit days either side of the window's floor, so the
/// wall clock read inside the method cannot move a case across it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityDigestMath.cs</code-under-test>
[Category("Messaging")]
public class IntegrityDigestMathBranchCoverageTests {
  private static readonly TimeSpan _settle = TimeSpan.FromHours(1);

  private static DateTimeOffset _inside() => DateTimeOffset.UtcNow.AddDays(1);
  private static DateTimeOffset _outside() => DateTimeOffset.UtcNow.AddDays(-10);

  [Test]
  public async Task IsInsideSettle_OriginChangedRecently_IsInsideAsync() {
    await Assert.That(IntegrityDigestMath.IsInsideSettle(_inside(), null, _settle)).IsTrue()
      .Because("an origin bucket still moving may have deliveries in flight");
  }

  [Test]
  public async Task IsInsideSettle_OriginUnknown_LocalChangedRecently_IsInsideAsync() {
    await Assert.That(IntegrityDigestMath.IsInsideSettle(null, _inside(), _settle)).IsTrue()
      .Because("the local side moving inside the window holds the comparison back on its own");
  }

  [Test]
  public async Task IsInsideSettle_OriginSettled_LocalChangedRecently_IsInsideAsync() {
    await Assert.That(IntegrityDigestMath.IsInsideSettle(_outside(), _inside(), _settle)).IsTrue();
  }

  [Test]
  public async Task IsInsideSettle_BothSettled_IsOutsideAsync() {
    await Assert.That(IntegrityDigestMath.IsInsideSettle(_outside(), _outside(), _settle)).IsFalse()
      .Because("two buckets quiet for longer than the window are safe to compare");
  }

  [Test]
  public async Task IsInsideSettle_OriginSettled_LocalUnknown_IsOutsideAsync() {
    await Assert.That(IntegrityDigestMath.IsInsideSettle(_outside(), null, _settle)).IsFalse();
  }

  [Test]
  public async Task IsInsideSettle_BothUnknown_IsOutsideAsync() {
    await Assert.That(IntegrityDigestMath.IsInsideSettle(null, null, _settle)).IsFalse()
      .Because("recomputed rows carry no update time, and a null time must never skip the comparison");
  }
}
