using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The two readings of a fleet the status surface can report, and why they must stay distinct.
/// <para>
/// "No other instances" and "cannot see the other instances" mean opposite things during an
/// incident: the first says this pod is alone and should take work, the second says the pod cannot
/// tell. Collapsing them — reporting an empty list for a failed read — makes a broken fleet query
/// look like a healthy single-instance deployment, which is the reading that leads to acting on it.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupFleetStatus.cs</code-under-test>
public class FleetStatusReportTests {

  [Test]
  public async Task AnUnavailableFleet_CarriesItsReasonAndNoInstancesAsync() {
    var report = FleetStatusReport.Unavailable("wh_service_instances unreachable");

    await Assert.That(report.Available).IsFalse();
    await Assert.That(report.UnavailableReason).IsEqualTo("wh_service_instances unreachable")
      .Because("the stated reason is the whole difference between a failed read and an empty one");
    await Assert.That(report.Instances).IsEmpty()
      .Because("an unavailable fleet has nothing to list — but the emptiness must be read through "
             + "Available, not mistaken for a healthy single-instance deployment");
  }

  [Test]
  public async Task AnAvailableButEmptyFleet_IsNotTheSameAsAnUnavailableOneAsync() {
    var alone = new FleetStatusReport(Available: true, UnavailableReason: null, Instances: []);
    var blind = FleetStatusReport.Unavailable("query failed");

    await Assert.That(alone.Available).IsTrue();
    await Assert.That(alone.Instances).IsEmpty();
    await Assert.That(alone).IsNotEqualTo(blind)
      .Because("this pod being alone and this pod being unable to look are opposite conclusions, "
             + "and only Available separates them");
  }
}
