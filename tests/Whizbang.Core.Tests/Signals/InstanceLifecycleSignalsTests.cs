using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Locks the metadata contract for the three instance-lifecycle signal types. InstanceDied MUST
/// be Durable — orphan takeover depends on it being replayable. Joined/Leaving are best-effort;
/// missing either only costs bounded latency until the heartbeat scan reconciles.
/// </summary>
public class InstanceLifecycleSignalsTests {
  [Test]
  public async Task InstanceJoined_MetadataAsync() {
    await Assert.That(InstanceJoinedSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(InstanceJoinedSignal.Targeting).IsEqualTo(SignalTargeting.Broadcast);
  }

  [Test]
  public async Task InstanceLeaving_MetadataAsync() {
    await Assert.That(InstanceLeavingSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(InstanceLeavingSignal.Targeting).IsEqualTo(SignalTargeting.Broadcast);
  }

  [Test]
  public async Task InstanceDied_IsDurableAndBroadcastAsync() {
    // Locks the plan's must-not-miss contract: orphan takeover depends on this signal being
    // Durable. A change to BestEffort would silently break failover under NOTIFY drops.
    await Assert.That(InstanceDiedSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.Durable)
      .Because("InstanceDied drives orphan takeover — a lost notify without the durable log leaves work stranded on the dead pod");
    await Assert.That(InstanceDiedSignal.Targeting).IsEqualTo(SignalTargeting.Broadcast);
  }

  [Test]
  public async Task WireNames_MatchCatalogAsync() {
    var all = SignalTypeRegistry.GetAll();

    var joined = all.SingleOrDefault(e => e.SignalType == typeof(InstanceJoinedSignal));
    await Assert.That(joined).IsNotNull();
    await Assert.That(joined!.WireName).IsEqualTo("instance-joined");

    var leaving = all.SingleOrDefault(e => e.SignalType == typeof(InstanceLeavingSignal));
    await Assert.That(leaving).IsNotNull();
    await Assert.That(leaving!.WireName).IsEqualTo("instance-leaving");

    var died = all.SingleOrDefault(e => e.SignalType == typeof(InstanceDiedSignal));
    await Assert.That(died).IsNotNull();
    await Assert.That(died!.WireName).IsEqualTo("instance-died");
  }
}
