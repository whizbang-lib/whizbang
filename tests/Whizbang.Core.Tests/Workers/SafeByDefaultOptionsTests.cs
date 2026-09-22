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
  public async Task SafeDefault_OutstandingBudgetIsOnNowThatItIsPerCategoryAsync() {
    var options = new ClaimWorkerOptions();

    await Assert.That(options.AdaptiveOutstandingBudget).IsTrue()
      .Because("the budget is now per category and row-bound (#719): it reads its headroom against inbox "
             + "rows only, so a perspective backlog cannot collapse inbox acquisition, and it passes that "
             + "headroom to the store as a row bound, so a collapse cannot turn into one row per cycle. The "
             + "failure it prevents is silent (rows dead-letter as MaxAttemptsExceeded without reaching a "
             + "receptor; consumers are killed holding work they cannot drain), which is why it is no longer opt-in.");
    await Assert.That(options.MaxPerspectiveDrainBacklog).IsGreaterThan(0)
      .Because("perspective acquisition has its own cap; without one a bulk ingest queues every perspective "
             + "stream it touches in process memory ahead of a fixed-parallelism drain");
  }
}
