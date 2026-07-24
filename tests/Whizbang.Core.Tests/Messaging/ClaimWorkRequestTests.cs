using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Surface tests for <see cref="ClaimWorkRequest"/> — the polling-claim
/// request record consumed by every <c>IWorkCoordinator.ClaimWorkAsync</c>
/// call (the new path that replaces the legacy <c>ProcessWorkBatchAsync</c>).
/// The defaults (MaxStreams=1000, PartitionCount=10000, LeaseSeconds=300) are
/// production-tuned and load-bearing; this file pins them so a future
/// "let's bump the default" lands explicitly here.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public class ClaimWorkRequestTests {

  [Test]
  public async Task PositionalCtor_RequiredFieldsRoundTripAsync() {
    var instanceId = Guid.NewGuid();
    var req = new ClaimWorkRequest(
      InstanceId: instanceId,
      ServiceName: "OrderService",
      HostName: "pod-1",
      ProcessId: 12345);

    await Assert.That(req.InstanceId).IsEqualTo(instanceId);
    await Assert.That(req.ServiceName).IsEqualTo("OrderService");
    await Assert.That(req.HostName).IsEqualTo("pod-1");
    await Assert.That(req.ProcessId).IsEqualTo(12345);
  }

  [Test]
  public async Task Defaults_MatchProductionTunedConstantsAsync() {
    var req = new ClaimWorkRequest(
      InstanceId: Guid.NewGuid(),
      ServiceName: "S",
      HostName: "H",
      ProcessId: 1);

    // These defaults are deployed across every a consumer application service; bumping them in
    // a refactor without realizing the production impact is the failure mode
    // this test guards against.
    await Assert.That(req.MaxStreams).IsEqualTo(1000);
    await Assert.That(req.PartitionCount).IsEqualTo(10000);
    await Assert.That(req.LeaseSeconds).IsEqualTo(300);
  }

  [Test]
  public async Task OptionalParameters_CanBeOverriddenAsync() {
    var req = new ClaimWorkRequest(
      InstanceId: Guid.NewGuid(),
      ServiceName: "S",
      HostName: "H",
      ProcessId: 1,
      MaxStreams: 50,
      PartitionCount: 100,
      LeaseSeconds: 60);

    await Assert.That(req.MaxStreams).IsEqualTo(50);
    await Assert.That(req.PartitionCount).IsEqualTo(100);
    await Assert.That(req.LeaseSeconds).IsEqualTo(60);
  }

  [Test]
  public async Task RecordValueEqualityAsync() {
    var iid = Guid.NewGuid();
    var a = new ClaimWorkRequest(iid, "S", "H", 1);
    var b = new ClaimWorkRequest(iid, "S", "H", 1);

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task WithExpression_OverridesIndividualMemberAsync() {
    var original = new ClaimWorkRequest(Guid.NewGuid(), "S", "H", 1);
    var updated = original with { MaxStreams = 5 };

    await Assert.That(updated.MaxStreams).IsEqualTo(5);
    await Assert.That(updated.InstanceId).IsEqualTo(original.InstanceId);
    await Assert.That(updated.LeaseSeconds).IsEqualTo(original.LeaseSeconds);
  }
}
