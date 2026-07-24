using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Locks the wire-name / targeting / delivery-class contract for the three work-available signals.
/// The wire-names must match the SQL payload strings ("outbox"/"inbox"/"perspective") that
/// <c>notify_instance_owners</c> already emits — a rename would break the unify-now migration.
/// </summary>
public class WorkAvailableSignalsTests {
  [Test]
  public async Task WorkOutbox_MetadataAsync() {
    await Assert.That(WorkOutboxAvailableSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(WorkOutboxAvailableSignal.Targeting).IsEqualTo(SignalTargeting.Targeted);
  }

  [Test]
  public async Task WorkInbox_MetadataAsync() {
    await Assert.That(WorkInboxAvailableSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(WorkInboxAvailableSignal.Targeting).IsEqualTo(SignalTargeting.Targeted);
  }

  [Test]
  public async Task WorkPerspective_MetadataAsync() {
    await Assert.That(WorkPerspectiveAvailableSignal.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(WorkPerspectiveAvailableSignal.Targeting).IsEqualTo(SignalTargeting.Targeted);
  }

  [Test]
  public async Task WireNames_MatchLegacySqlPayloadsAsync() {
    // The generator + [WireName] combination must emit these exact strings so the transport
    // registry maps existing SQL notifies (notify_instance_owners) to the typed signals. If any
    // of these assertions fail, unify-now would break the payload contract on the hot path.
    var all = SignalTypeRegistry.GetAll();

    var outbox = all.SingleOrDefault(e => e.SignalType == typeof(WorkOutboxAvailableSignal));
    await Assert.That(outbox).IsNotNull();
    await Assert.That(outbox!.WireName).IsEqualTo("outbox");

    var inbox = all.SingleOrDefault(e => e.SignalType == typeof(WorkInboxAvailableSignal));
    await Assert.That(inbox).IsNotNull();
    await Assert.That(inbox!.WireName).IsEqualTo("inbox");

    var perspective = all.SingleOrDefault(e => e.SignalType == typeof(WorkPerspectiveAvailableSignal));
    await Assert.That(perspective).IsNotNull();
    await Assert.That(perspective!.WireName).IsEqualTo("perspective");
  }
}
