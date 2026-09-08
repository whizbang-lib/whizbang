using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The suppression window a duty elector opens after finding a duty held by another instance.
/// While the window is open, attempts are answered from memory rather than with a round trip:
/// a live holder is not going to release in the next second, and a dead holder's session lock
/// vanishes with it, so the next real attempt after the window sees the truth either way. The
/// window doubles on each consecutive contention up to a ceiling and closes on any other outcome.
/// Clock-free: the caller passes its notion of now.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/DutyContentionBackoff.cs</code-under-test>
[Category("Core")]
[Category("Startup")]
public class DutyContentionBackoffTests {
  private static readonly DateTimeOffset _t0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task Fresh_IsNotSuppressedAsync() {
    var backoff = new DutyContentionBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    await Assert.That(backoff.IsSuppressed(_t0, out var remaining)).IsFalse()
      .Because("nothing has been contended yet, so the first attempt must reach the database");
    await Assert.That(remaining).IsEqualTo(TimeSpan.Zero);
    await Assert.That(backoff.ConsecutiveContentions).IsEqualTo(0);
  }

  [Test]
  public async Task RecordContended_OpensAWindowOfTheFloorAsync() {
    var backoff = new DutyContentionBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    var window = backoff.RecordContended(_t0);

    await Assert.That(window).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(backoff.ConsecutiveContentions).IsEqualTo(1);
    await Assert.That(backoff.IsSuppressed(_t0.AddSeconds(1), out var remaining)).IsTrue()
      .Because("one second into a two second window the attempt is answered from memory");
    await Assert.That(remaining).IsEqualTo(TimeSpan.FromSeconds(1));
  }

  [Test]
  public async Task WindowEnd_IsNotSuppressedAsync() {
    var backoff = new DutyContentionBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    _ = backoff.RecordContended(_t0);

    await Assert.That(backoff.IsSuppressed(_t0.AddSeconds(2), out var remaining)).IsFalse()
      .Because("at the boundary the next attempt must go to the database: the holder may be gone");
    await Assert.That(remaining).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task ConsecutiveContentions_DoubleTheWindowUpToTheCeilingAsync() {
    // 2, 4, 8, 16, 30, 30: each real attempt that still finds the holder widens the next window.
    var backoff = new DutyContentionBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    var now = _t0;
    var windows = new List<double>();

    for (var i = 0; i < 6; i++) {
      var window = backoff.RecordContended(now);
      windows.Add(window.TotalSeconds);
      now += window;
    }

    await Assert.That(windows).IsEquivalentTo(new double[] { 2, 4, 8, 16, 30, 30 });
    await Assert.That(backoff.ConsecutiveContentions).IsEqualTo(6);
  }

  [Test]
  public async Task Reset_ClosesTheWindowAndForgetsTheStreakAsync() {
    var backoff = new DutyContentionBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    _ = backoff.RecordContended(_t0);
    _ = backoff.RecordContended(_t0.AddSeconds(2));

    backoff.Reset();

    await Assert.That(backoff.IsSuppressed(_t0.AddSeconds(2), out _)).IsFalse()
      .Because("a grant, a refusal, or an unavailable connection is not contention; the next attempt is a fresh one");
    await Assert.That(backoff.ConsecutiveContentions).IsEqualTo(0);
    await Assert.That(backoff.RecordContended(_t0.AddSeconds(3))).IsEqualTo(TimeSpan.FromSeconds(2))
      .Because("after a reset the streak starts again at the floor");
  }

  [Test]
  public async Task InvalidArguments_ThrowAsync() {
    await Assert.That(() => new DutyContentionBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(1)))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new DutyContentionBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)))
      .Throws<ArgumentOutOfRangeException>();
  }
}
