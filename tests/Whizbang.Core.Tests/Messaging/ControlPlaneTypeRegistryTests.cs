using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The dead-letter decision runs where only a message-type STRING is available — no CLR type — so
/// the <see cref="IControlPlaneMessage"/> marker cannot be tested there by assignability the way
/// the security path does it. This registry carries the same fact by name: the framework registers
/// its own control-plane types at module-init, and a consumer can register its own.
/// <para>
/// It exists because durably dead-lettering control-plane traffic is a contradiction that cost a
/// production slot: checkpoints, manifests and re-delivery requests are periodic and re-emitted, so
/// a stored copy is worthless by the time anyone looks — yet tens of thousands of them accumulated
/// per service, and the recovery worker fed them back into the inbox on every boot.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ControlPlaneTypeRegistry.cs</code-under-test>
[Category("Messaging")]
public class ControlPlaneTypeRegistryTests {

  [Test]
  public async Task FrameworkControlPlaneTypes_AreRegisteredByDefaultAsync() {
    // The framework's own signals must be recognized without any consumer opt-in — a consumer
    // should never have to know W!'s internal message list to avoid the DLQ trap.
    foreach (var type in new[] {
      typeof(IntegrityCheckpoint), typeof(IntegrityManifest), typeof(RequestIntegrityManifest),
      typeof(RequestRedeliveryCommand), typeof(IntegrityDivergenceDetected),
      typeof(IntegrityGapDetected), typeof(PerspectiveCoverageGapDetected),
    }) {
      await Assert.That(ControlPlaneTypeRegistry.IsControlPlane(TypeNameFormatter.Format(type))).IsTrue()
        .Because($"{type.Name} is framework control-plane traffic and must never reach the DLQ");
    }
  }

  [Test]
  public async Task DomainMessageType_IsNotControlPlaneAsync() {
    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane("Contracts.Orders.OrderPlacedEvent, Contracts")).IsFalse()
      .Because("a domain event is exactly what the DLQ is FOR — this must stay opt-in per type");
  }

  [Test]
  public async Task Matches_RegardlessOfAssemblyVersionDecorationAsync() {
    // Stored rows carry fully assembly-qualified names with Version/Culture/PublicKeyToken; the
    // registry is seeded from the short wire form. A mismatch here silently re-opens the trap.
    var decorated = typeof(IntegrityCheckpoint).AssemblyQualifiedName!;

    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane(decorated)).IsTrue()
      .Because("the persisted message_type is version-decorated — matching must normalize it");
  }

  [Test]
  public async Task Register_IsIdempotentAndAcceptsConsumerTypesAsync() {
    ControlPlaneTypeRegistry.Register("Consumer.Ops.HeartbeatSignal, Consumer.Ops");
    ControlPlaneTypeRegistry.Register("Consumer.Ops.HeartbeatSignal, Consumer.Ops");

    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane("Consumer.Ops.HeartbeatSignal, Consumer.Ops")).IsTrue()
      .Because("consumers may mark their own infrastructure signals, exactly as the marker interface allows");
  }

  [Test]
  public async Task UnknownOrEmptyName_IsNotControlPlaneAsync() {
    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane("")).IsFalse();
    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane(null!)).IsFalse()
      .Because("an unreadable type name must fail SAFE — keep the row rather than silently drop it");
  }
}
