// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using FsCheck;
using FsCheck.Fluent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Property tests for the framework's UUIDv7 generator. <see cref="Uuid7GeneratorTests"/> pins each edge case
/// with a scripted clock; these drive the generator with generated clock histories (bursts inside one
/// millisecond, small and large regressions, jumps forward) and generated random bytes, and check that the
/// ordering contract holds for all of them.
/// </summary>
[Category("Core")]
[Category("ValueObjects")]
[Category("IdGeneration")]
[Category("Property")]
public class Uuid7GeneratorPropertyTests {
  private const long BASE_MILLISECOND = 1_790_000_000_000; // an ordinary 2026 unix millisecond

  /// <summary>One generated run: where the clock starts, how it moves before each id, and the random bytes it draws from.</summary>
  public sealed record ClockHistory(long Start, int[] Steps, byte[] RandomPool);

  private static readonly Gen<int> _clockStep = Gen.Frequency(
    (6, Gen.Constant(0)),         // another id in the same millisecond
    (3, Gen.Choose(1, 3)),        // time moves on a little
    (2, Gen.Choose(-3, -1)),      // the clock steps back a little
    (1, Gen.Choose(-1000, 1000))  // large jumps either way
  );

  private static readonly Gen<ClockHistory> _clockHistory =
    from startOffset in Gen.Choose(0, int.MaxValue)
    from steps in _clockStep.ArrayOf()
    from randomPool in Gen.Choose(0, 255).Select(b => (byte)b).NonEmptyListOf()
    select new ClockHistory(BASE_MILLISECOND + startOffset, steps, [.. randomPool]);

  /// <summary>
  /// For every clock history: each id sorts strictly after the one before it in big-endian byte order, carries
  /// version 7 and the RFC variant, and embeds the highest clock reading seen so far, so its timestamp never
  /// goes backwards even when the clock does. (Counter exhaustion, the one case that moves the timestamp past the
  /// clock, needs about 2^21 ids in one millisecond and is pinned separately in <see cref="Uuid7GeneratorTests"/>.)
  /// </summary>
  [Test]
  public async Task NewGuid_AnyClockHistory_IdsStrictlyIncreaseAndTimestampTracksHighestReadingAsync() {
    var property = Prop.ForAll(_clockHistory.ToArbitrary(), history => _issuesInOrder(history));

    await Assert.That(() => PropertyCheck.Run(nameof(Uuid7GeneratorPropertyTests), property, maxTest: 300, seed: 0x5EED_0007))
      .ThrowsNothing();
  }

  private static bool _issuesInOrder(ClockHistory history) {
    var now = history.Start;
    var poolIndex = 0;
    var generator = new Uuid7Generator(
      () => now,
      destination => {
        for (var i = 0; i < destination.Length; i++) {
          destination[i] = history.RandomPool[poolIndex++ % history.RandomPool.Length];
        }
      });

    var highestReading = long.MinValue;
    byte[]? previous = null;
    foreach (var step in history.Steps.Prepend(0)) {
      now += step;
      highestReading = Math.Max(highestReading, now);
      var bytes = generator.NewGuid().ToByteArray(bigEndian: true);

      var ordered = previous is null || bytes.AsSpan().SequenceCompareTo(previous) > 0;
      var versionAndVariant = (bytes[6] >> 4) == 7 && (bytes[8] >> 6) == 0b10;
      if (!ordered || !versionAndVariant || _millisecond(bytes) != highestReading) {
        return false;
      }
      previous = bytes;
    }
    return true;
  }

  private static long _millisecond(byte[] bytes) =>
    ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
    | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
}
