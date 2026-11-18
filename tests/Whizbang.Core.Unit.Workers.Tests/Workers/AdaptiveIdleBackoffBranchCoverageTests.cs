// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The idle delay must never exceed the ceiling, including when the growth is computed in double
/// precision and the ceiling's tick count is not exactly representable as a double.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/AdaptiveIdleBackoff.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class AdaptiveIdleBackoffBranchCoverageTests {

  // 2^53 + 3 rounds UP to 2^53 + 4 as a double (ties to even), so the double-precision Min against
  // the ceiling can yield one tick more than the ceiling. The final clamp must catch that.
  private const long CEILING_TICKS = (1L << 53) + 3;
  private const long FLOOR_TICKS = (1L << 52) + 2;

  [Test]
  public async Task Next_GrowthRoundsAboveCeilingInDoublePrecision_ClampsToExactCeilingAsync() {
    var floor = TimeSpan.FromTicks(FLOOR_TICKS);
    var ceiling = TimeSpan.FromTicks(CEILING_TICKS);
    var backoff = new AdaptiveIdleBackoff(floor, ceiling);

    var firstWait = backoff.Next(foundWork: false);

    await Assert.That(firstWait).IsEqualTo(floor);
    await Assert.That(backoff.Current.Ticks).IsEqualTo(CEILING_TICKS)
      .Because("doubling the floor lands on 2^53 + 4 ticks, one past the ceiling, and must be clamped");
    await Assert.That(backoff.Next(foundWork: false)).IsEqualTo(ceiling);
  }
}
