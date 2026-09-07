using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Defaults a consumer gets with no configuration must be the safe ones. Each test here pins a default
/// that was changed after it caused an outage on a consumer running the framework's defaults.
/// </summary>
public class SafeByDefaultOptionsTests {
  [Test]
  public async Task SafeDefault_OutstandingBudgetIsOffUntilItIsPerCategoryAsync() {
    var options = new ClaimWorkerOptions();

    await Assert.That(options.AdaptiveOutstandingBudget).IsFalse()
      .Because("the budget samples inbox completions only and counts every category as outstanding: "
             + "a perspective backlog reads as a zero drain rate, headroom collapses to one row per cycle, "
             + "and inbox acquisition starves while the database sits idle. Off by default until it is "
             + "per work category; the churn-based claim window remains the bound.");
  }
}
