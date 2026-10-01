using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Requirement 10 of #966: elections, hand-offs by reason, losses, releases, roles held and owed
/// work outcomes are meters, each tagged by role.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/RoleAssignmentMetrics.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class RoleAssignmentMetricsTests {

  [Test]
  public async Task Meters_AreReadablePerRole_AndTheHeldGaugeGoesBothWaysAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new RoleAssignmentMetrics(new WhizbangMetrics(factory));
    var role = RoleAssignmentMetrics.RoleTag("maintainer");

    metrics.Elections.Add(1, role);
    metrics.Handoffs.Add(1, role, new KeyValuePair<string, object?>(RoleAssignmentMetrics.REASON_TAG, "lapsed"));
    metrics.Lost.Add(1, role);
    metrics.Released.Add(1, role);
    metrics.Held.Add(1, role);
    metrics.Held.Add(-1, role);
    metrics.WorkRuns.Add(2, role, new KeyValuePair<string, object?>(RoleAssignmentMetrics.OUTCOME_TAG, RoleAssignmentMetrics.OUTCOME_COMPLETED));
    metrics.Drains.Add(1, role);
    metrics.BridgeSessionsEnded.Add(3, role);

    var meter = factory.CreatedMeters.Single(m => m.Name == RoleAssignmentMetrics.METER_NAME);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.roles.elections")).IsEqualTo(1);
    var handoff = ProbeMeterReader.ReadSeries(meter, "whizbang.roles.handoffs").Single(h => h.Tags.Count > 0);
    await Assert.That(handoff.Tags[RoleAssignmentMetrics.REASON_TAG]).IsEqualTo("lapsed");
    await Assert.That(handoff.Tags[RoleAssignmentMetrics.ROLE_TAG]).IsEqualTo("maintainer");
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.roles.lost")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.roles.released")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.roles.held")).IsEqualTo(0);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.roles.work_runs")).IsEqualTo(2);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.roles.drains")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.roles.bridge_sessions_ended")).IsEqualTo(3);
  }

  [Test]
  public async Task Constructor_RefusesNullAsync() {
    await Assert.That(() => new RoleAssignmentMetrics(null!)).Throws<ArgumentNullException>();
  }
}
