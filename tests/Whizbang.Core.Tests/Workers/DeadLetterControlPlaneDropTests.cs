using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Control-plane traffic must never be durably dead-lettered. Checkpoints, manifests and
/// re-delivery requests are periodic and re-emitted by design, so a stored copy is worthless by the
/// time an operator reads it — and it is not inert, because the recovery worker feeds stored rows
/// back into the inbox on a later boot. Observed live: tens of thousands of control-plane rows per
/// service, surviving repeated queue purges because they sat in the dead-letter table, and
/// re-entering the inbox every time a pod restarted.
/// <para>
/// A failed control-plane message is therefore DROPPED at the dead-letter boundary — logged and
/// metered, but not stored. Domain messages keep the dead-letter contract unchanged: that is what
/// the queue is for.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDispatchWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
public class DeadLetterControlPlaneDropTests {

  [Test]
  public async Task ControlPlaneMessage_IsDroppedNotStoredAsync() {
    await Assert.That(DeadLetterDropPolicy.ShouldDropInsteadOfStore(
        TypeNameFormatter.Format(typeof(IntegrityCheckpoint)))).IsTrue()
      .Because("a stale checkpoint has no forensic value and re-entering the inbox on the next " +
               "boot is how a burst of failures became a permanent, self-reviving backlog");
  }

  [Test]
  public async Task RedeliveryRequest_IsDroppedAsync() {
    await Assert.That(DeadLetterDropPolicy.ShouldDropInsteadOfStore(
        typeof(RequestRedeliveryCommand).AssemblyQualifiedName!)).IsTrue()
      .Because("repair requests are re-issued by the audit on its own cadence — storing a failed " +
               "one buys nothing and costs a resurrection");
  }

  [Test]
  public async Task DomainMessage_IsStillDeadLetteredAsync() {
    await Assert.That(DeadLetterDropPolicy.ShouldDropInsteadOfStore(
        "Contracts.Orders.OrderPlacedEvent, Contracts")).IsFalse()
      .Because("a failed domain event is exactly what the dead-letter queue exists to preserve — " +
               "this fix must not widen into silent business-data loss");
  }

  [Test]
  public async Task UnreadableTypeName_IsStillDeadLetteredAsync() {
    await Assert.That(DeadLetterDropPolicy.ShouldDropInsteadOfStore("")).IsFalse();
    await Assert.That(DeadLetterDropPolicy.ShouldDropInsteadOfStore(null!)).IsFalse()
      .Because("fail SAFE: if the type cannot be identified, keep the row rather than drop it");
  }
}
