// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// #2046: every coordination channel is a signal on the bus. A decision (a role released, a partition assignment
/// published) is durable, so an instance that missed the notification is still told; a doorbell (rows committed,
/// multi-stream work, orphaned work, a dead letter) is best-effort, because a poll backs each one up. The wire names are
/// the ones the database already sends, so a mixed fleet keeps hearing each other.
/// </summary>
[NotInParallel("SignalTypeRegistryStatic")]
public class CoordinationSignalsTests {
  [Test]
  public async Task Decisions_AreDurableBroadcastsAsync() {
    // A decision missed is a hand-off that waits a whole renewal, or a claimer that keeps a superseded assignment until
    // a claim is refused: each is recorded in the durable log and replayed to an instance that missed the notification.
    await Assert.That(RoleReleasedSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.Durable)
      .Because("a role released is a decision: an instance that missed the notification must still hear it");
    await Assert.That(RoleReleasedSignal.Targeting).IsEqualTo(SignalTargeting.Broadcast);
    await Assert.That(PartitionAssignmentPublishedSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.Durable)
      .Because("a published assignment is a decision: an instance that missed the notification must still hear it");
    await Assert.That(PartitionAssignmentPublishedSignal.Targeting).IsEqualTo(SignalTargeting.Broadcast);
  }

  [Test]
  public async Task Doorbells_AreBestEffortAsync() {
    // Each doorbell has a poll behind it, and a durable row per commit would put a write on the hot path.
    await Assert.That(RowsCommittedSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort)
      .Because("the commit doorbell rings per commit; the stamper's poll is its backstop");
    await Assert.That(RowsCommittedSignal.Targeting).IsEqualTo(SignalTargeting.Broadcast);
    await Assert.That(MultiStreamWorkSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(MultiStreamWorkSignal.Targeting).IsEqualTo(SignalTargeting.Broadcast);
    await Assert.That(WorkOrphanedSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(WorkOrphanedSignal.Targeting).IsEqualTo(SignalTargeting.Targeted)
      .Because("the reap wakes each live instance on its own channel");
    await Assert.That(DeadLetterReadySignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(DeadLetterReadySignal.Targeting).IsEqualTo(SignalTargeting.Targeted);
  }

  [Test]
  public async Task PayloadSignals_CarryTheirPayloadBothWaysAsync() {
    var released = RoleReleasedSignal.FromPayload("whizbang:maintainer");
    await Assert.That(released.Role).IsEqualTo("whizbang:maintainer");
    await Assert.That(((IPayloadSignal)released).Payload).IsEqualTo("whizbang:maintainer");

    var published = PartitionAssignmentPublishedSignal.FromPayload("7:3");
    await Assert.That(published.Version).IsEqualTo("7:3");
    await Assert.That(((IPayloadSignal)published).Payload).IsEqualTo("7:3");
  }

  [Test]
  public async Task WireNames_AreRegisteredAsync() {
    var all = SignalTypeRegistry.GetAll();
    (Type Type, string Wire)[] expected = [
      (typeof(RoleReleasedSignal), "role-released"),
      (typeof(PartitionAssignmentPublishedSignal), "partition-assignment-published"),
      (typeof(RowsCommittedSignal), "committed"),
      (typeof(MultiStreamWorkSignal), "multi-stream-work"),
      // The two payloads the work channel already carries: the transport routes them by these names.
      (typeof(WorkOrphanedSignal), "orphan"),
      (typeof(DeadLetterReadySignal), "deadletter"),
    ];
    foreach (var (type, wire) in expected) {
      var entry = all.SingleOrDefault(e => e.SignalType == type);
      await Assert.That(entry).IsNotNull().Because($"{type.Name} is discovered by the signal registry");
      await Assert.That(entry!.WireName).IsEqualTo(wire);
    }
  }
}
