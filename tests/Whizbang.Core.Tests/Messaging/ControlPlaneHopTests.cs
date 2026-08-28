using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The shared creation hop for control-plane traffic that publishes straight to a transport.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ControlPlaneHop.cs</code-under-test>
[Category("Messaging")]
public class ControlPlaneHopTests {

  private sealed record _plainEvent : Whizbang.Core.IEvent;

  private sealed record _controlSignal : Whizbang.Core.IEvent, IControlPlaneMessage;

  private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task AControlPlaneHopIsMarkedSystemAsync() {
    var hop = ControlPlaneHop.Create(typeof(_controlSignal), instanceProvider: null, _at);

    await Assert.That(hop.Type).IsEqualTo(HopType.Current);
    await Assert.That(hop.Timestamp).IsEqualTo(_at);
    await Assert.That(hop.Scope).IsNotNull()
      .Because("these publishers bypass the dispatcher, so if the factory does not mark them the "
             + "marker covers only part of the control plane and the invariant has holes");
    await Assert.That(hop.Scope!.ApplyTo(null).Scope.IsSystem).IsTrue();
  }

  [Test]
  public async Task AnOrdinaryPayloadIsLeftUnscopedAsync() {
    var hop = ControlPlaneHop.Create(typeof(_plainEvent), instanceProvider: null, _at);

    await Assert.That(hop.Scope).IsNull()
      .Because("the factory must not mark whatever it is handed — marking a domain event would "
             + "exempt it from the very invariant the marker exists to enforce");
  }

  [Test]
  public async Task ACompositeIsLeftUnscopedEvenThoughItIsControlPlaneAsync() {
    var hop = ControlPlaneHop.Create(
      typeof(Whizbang.Core.Minting.RedeliveryComposite), instanceProvider: null, _at);

    await Assert.That(hop.Scope).IsNull()
      .Because("a composite's scope becomes its CHILDREN's scope at fan-out; marking the wrapper "
             + "would launder a system marker onto ordinary domain events");
  }

  [Test]
  public async Task AMissingInstanceProviderFallsBackRatherThanThrowingAsync() {
    // The fallback lives here now instead of at each call site. A publisher running without a
    // registered provider must still emit a well-formed hop -- losing the whole control-plane
    // message over missing telemetry identity would be a worse failure than an unknown instance.
    var hop = ControlPlaneHop.Create(typeof(_controlSignal), instanceProvider: null, _at);

    await Assert.That(hop.ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
    await Assert.That(hop.Scope).IsNotNull()
      .Because("an unknown publishing instance says nothing about whether the message is "
             + "control-plane; the marker must not depend on telemetry being wired up");
  }
}
