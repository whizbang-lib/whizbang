using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A claim narrower than the floor is too small a sample to move the window in either direction. One
/// re-offered row is 100 % churn on paper, and a loop that halved on it walked a 1000-stream window to
/// the floor in under a second while the queue held a single row (observed under a bulk import). The
/// floor is the smallest claim the window ever makes, so a sample smaller than it cannot have been
/// limited by the window and says nothing about it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/AdaptiveClaimWindow.cs</code-under-test>
public class AdaptiveClaimWindowSampleSizeTests {
  private static AdaptiveClaimWindow _grownTo100() {
    var window = new AdaptiveClaimWindow(ceiling: 1000, floor: 25, additiveStep: 25);
    for (var i = 0; i < 3; i++) {
      window.Observe(claimedRows: 100, reclaimedRows: 0);
    }
    return window;
  }

  [Test]
  public async Task SampleSize_OneReofferedRow_DoesNotHalveTheWindowAsync() {
    var window = _grownTo100();

    window.Observe(claimedRows: 1, reclaimedRows: 1);

    await Assert.That(window.Current).IsEqualTo(100)
      .Because("one re-offered row is 100 % churn on paper and nothing in practice; halving on it walks the window to the floor in a second");
  }

  [Test]
  public async Task SampleSize_NarrowerThanTheFloor_EarnsNoGrowthEitherAsync() {
    var window = _grownTo100();

    window.Observe(claimedRows: 24, reclaimedRows: 0);

    await Assert.That(window.Current).IsEqualTo(100)
      .Because("a clean sample narrower than the floor was not limited by the window, so it says nothing about headroom");
  }

  [Test]
  public async Task SampleSize_AtLeastTheFloorWide_IsJudgedAsBeforeAsync() {
    var window = _grownTo100();

    window.Observe(claimedRows: 25, reclaimedRows: 25);
    await Assert.That(window.Current).IsEqualTo(50)
      .Because("a sample at least the floor wide is a real reading: full churn halves the window");

    window.Observe(claimedRows: 25, reclaimedRows: 0);
    await Assert.That(window.Current).IsEqualTo(75)
      .Because("and a clean one grows it by the additive step");
  }
}
