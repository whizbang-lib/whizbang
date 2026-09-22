using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The cadence every periodic probe shares when it has nothing to do: stay at the floor while
/// work is found, double on each idle pass up to a ceiling that already exists as an option, and
/// snap back to the floor on the first pass that finds work. Pure and clock-free so a probe's
/// cadence can be proven without driving its loop.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/AdaptiveIdleBackoff.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class AdaptiveIdleBackoffTests {

  [Test]
  public async Task Construction_StartsAtTheFloorAsync() {
    var backoff = new AdaptiveIdleBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    await Assert.That(backoff.Current).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(backoff.Floor).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(backoff.Ceiling).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task IdlePasses_DoubleUpToTheCeilingAndStayThereAsync() {
    // 2, 4, 8, 16, 30, 30: the returned delay is the one to wait before the next pass, and the
    // growth applies after it so the first idle pass still waits only the floor.
    var backoff = new AdaptiveIdleBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    var delays = new List<double>();
    for (var i = 0; i < 6; i++) {
      delays.Add(backoff.Next(foundWork: false).TotalSeconds);
    }

    await Assert.That(delays).IsEquivalentTo(new double[] { 2, 4, 8, 16, 30, 30 })
      .Because("the delay grows multiplicatively after each idle pass and clamps at the ceiling");
  }

  [Test]
  public async Task FindingWork_SnapsBackToTheFloorAsync() {
    var backoff = new AdaptiveIdleBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    _ = backoff.Next(foundWork: false);
    _ = backoff.Next(foundWork: false);
    _ = backoff.Next(foundWork: false);

    var afterWork = backoff.Next(foundWork: true);

    await Assert.That(afterWork).IsEqualTo(TimeSpan.FromSeconds(2))
      .Because("work arriving is the signal that the probe is needed again at full cadence");
    await Assert.That(backoff.Current).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task Reset_ReturnsToTheFloorWithoutAPassAsync() {
    var backoff = new AdaptiveIdleBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    _ = backoff.Next(foundWork: false);
    _ = backoff.Next(foundWork: false);

    backoff.Reset();

    await Assert.That(backoff.Current).IsEqualTo(TimeSpan.FromSeconds(2))
      .Because("an external signal (a doorbell, a join) may reset the cadence without waiting for a pass");
  }

  [Test]
  public async Task CustomMultiplier_IsHonoredAsync() {
    var backoff = new AdaptiveIdleBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), multiplier: 3);

    _ = backoff.Next(foundWork: false);

    await Assert.That(backoff.Current).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task CeilingEqualToFloor_NeverGrowsAsync() {
    // The degenerate configuration that turns the backoff off: legal, and it must behave like a
    // fixed cadence rather than throw.
    var backoff = new AdaptiveIdleBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    _ = backoff.Next(foundWork: false);
    _ = backoff.Next(foundWork: false);

    await Assert.That(backoff.Current).IsEqualTo(TimeSpan.FromSeconds(5));
  }

  [Test]
  public async Task InvalidArguments_ThrowAsync() {
    await Assert.That(() => new AdaptiveIdleBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(1)))
      .Throws<ArgumentOutOfRangeException>().Because("a zero floor would spin the probe");
    await Assert.That(() => new AdaptiveIdleBackoff(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1)))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new AdaptiveIdleBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4)))
      .Throws<ArgumentOutOfRangeException>().Because("a ceiling below the floor has no meaning");
    await Assert.That(() => new AdaptiveIdleBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), multiplier: 1))
      .Throws<ArgumentOutOfRangeException>().Because("a multiplier of one never backs off");
    await Assert.That(() => new AdaptiveIdleBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), multiplier: 0.5))
      .Throws<ArgumentOutOfRangeException>();
  }
}
