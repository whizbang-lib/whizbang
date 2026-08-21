using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for <see cref="TopologyDriftState"/> + <see cref="TopologyDriftHealthSource"/>
/// (topology arc phase 5): the provisioning path records cross-service command-ownership
/// violations, and the health source degrades the <c>"topology"</c> component while any stand.
/// </summary>
public class TopologyDriftStateTests {
  [Test]
  public async Task HealthSource_NoFindings_ReportsOperationalAsync() {
    var state = new TopologyDriftState();
    var source = new TopologyDriftHealthSource(state);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
    await Assert.That(state.HasDrift).IsFalse();
  }

  [Test]
  public async Task HealthSource_WithFinding_ReportsDegradedWithDetailAsync() {
    // Degraded, never Faulted — the service still serves; the violation is a modeling error
    // that duplicates commands, not an outage.
    var state = new TopologyDriftState();
    state.Record(new TopologyDriftFinding(
      "inbox.myapp.orders.commands",
      "other-service-inbox.myapp.orders.commands",
      "second service's subscription on an owned command inbox"));
    var source = new TopologyDriftHealthSource(state);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail!).Contains("inbox.myapp.orders.commands");
    await Assert.That(health.Detail!).Contains("other-service-inbox.myapp.orders.commands");
  }

  [Test]
  public async Task HealthSource_Component_IsTopologyAsync() {
    var source = new TopologyDriftHealthSource(new TopologyDriftState());

    await Assert.That(source.Component).IsEqualTo("topology");
  }

  [Test]
  public async Task State_Findings_SnapshotsInRecordOrderAsync() {
    var state = new TopologyDriftState();
    state.Record(new TopologyDriftFinding("inbox.a", "svc-b", "first"));
    state.Record(new TopologyDriftFinding("inbox.c", "svc-d", "second"));

    var findings = state.Findings;

    await Assert.That(findings.Count).IsEqualTo(2);
    await Assert.That(findings[0].Entity).IsEqualTo("inbox.a");
    await Assert.That(findings[1].Entity).IsEqualTo("inbox.c");
  }

  [Test]
  public async Task State_RecordNull_ThrowsArgumentNullExceptionAsync() {
    var state = new TopologyDriftState();

    await Assert.That(() => state.Record(null!)).Throws<ArgumentNullException>();
  }
}
