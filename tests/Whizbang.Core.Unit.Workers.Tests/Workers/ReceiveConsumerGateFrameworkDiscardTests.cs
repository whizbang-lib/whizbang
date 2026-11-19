// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2091: framework control or minted traffic a service cannot use is discarded on receipt with no row and no work,
/// whatever entity it arrived on: when it is addressed to another service, or when nothing here consumes it. Domain
/// traffic and body-offload claims are never decided here.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReceiveConsumerGate.cs</code-under-test>
public class ReceiveConsumerGateFrameworkDiscardTests {
  private const string THIS_SERVICE = "orders";
  private const string OTHER_SERVICE = "reporting";

  private static readonly string _composite =
    EnvelopeTypeNameHelper.Format("Whizbang.Core.Minting.RedeliveryComposite, Whizbang.Core");
  private static readonly string _domain =
    EnvelopeTypeNameHelper.Format("MyApp.Orders.Commands.CreateOrder, MyApp.Contracts");

  [Test]
  public async Task FrameworkMessage_AddressedElsewhere_IsDiscardedAsForeignTargetAsync() {
    await Assert.That(ReceiveConsumerGate.FrameworkDiscardOnReceipt(_composite, OTHER_SERVICE, THIS_SERVICE, unconsumed: false))
      .IsEqualTo(InboxReceiveDiscard.FOREIGN_TARGET);
  }

  [Test]
  public async Task FrameworkMessage_AddressedHere_IsKeptAsync() {
    await Assert.That(ReceiveConsumerGate.FrameworkDiscardOnReceipt(_composite, THIS_SERVICE, THIS_SERVICE, unconsumed: false))
      .IsNull();
  }

  [Test]
  public async Task FrameworkMessage_Broadcast_Consumed_IsKeptAsync() {
    await Assert.That(ReceiveConsumerGate.FrameworkDiscardOnReceipt(_composite, null, THIS_SERVICE, unconsumed: false))
      .IsNull();
  }

  [Test]
  public async Task FrameworkMessage_Unconsumed_IsDiscardedAsNoLocalConsumerAsync() {
    await Assert.That(ReceiveConsumerGate.FrameworkDiscardOnReceipt(_composite, null, THIS_SERVICE, unconsumed: true))
      .IsEqualTo(InboxReceiveDiscard.NO_LOCAL_CONSUMER);
  }

  [Test]
  public async Task FrameworkMessage_UnknownIdentity_FailsOpenOnTheTargetAsync() {
    await Assert.That(ReceiveConsumerGate.FrameworkDiscardOnReceipt(_composite, OTHER_SERVICE, null, unconsumed: false))
      .IsNull()
      .Because("a service that cannot name itself cannot tell a foreign target from its own");
  }

  [Test]
  public async Task DomainMessage_IsNeverDecidedHereAsync() {
    await Assert.That(ReceiveConsumerGate.FrameworkDiscardOnReceipt(_domain, OTHER_SERVICE, THIS_SERVICE, unconsumed: true))
      .IsNull()
      .Because("domain traffic keeps the durable settled row of #1720");
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("  ")]
  [Arguments("Whizbang.Core.Minting.RedeliveryComposite")]
  [Arguments("Whizbang.Core.Observability.MessageEnvelope`1[[RedeliveryComposite, Whizbang.Core]], Whizbang.Core")]
  [Arguments("Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.Events.Shipped]], Whizbang.Core")]
  [Arguments("Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.Workers.Probe, Whizbang.Core.Tests]], Whizbang.Core")]
  public async Task NotAFrameworkPayload_IsNotFrameworkTrafficAsync(string? envelopeType) {
    await Assert.That(ReceiveConsumerGate.IsFrameworkMessage(envelopeType)).IsFalse();
  }

  [Test]
  public async Task FrameworkPayloadWithFullAssemblyIdentity_IsFrameworkTrafficAsync() {
    await Assert.That(ReceiveConsumerGate.IsFrameworkMessage(
      "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Minting.RedeliveryComposite, Whizbang.Core, "
      + "Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Whizbang.Core")).IsTrue();
  }

  [Test]
  public async Task FrameworkPayloadWithoutAssemblyQualifier_IsFrameworkTrafficAsync() {
    await Assert.That(ReceiveConsumerGate.IsFrameworkMessage(
      "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.RequestRedeliveryCommand]], Whizbang.Core"))
      .IsTrue();
  }

  [Test]
  public async Task BodyOffloadClaim_IsNotFrameworkTrafficAsync() {
    var claim = EnvelopeTypeNameHelper.Format("Whizbang.Core.Offloads.BodyClaimEnvelopePayload, Whizbang.Core");
    await Assert.That(ReceiveConsumerGate.IsFrameworkMessage(claim)).IsFalse()
      .Because("a claim stands in for a message of any type, restored downstream");
  }

  [Test]
  public async Task KnownServiceName_NoProviderOrUnknownIdentity_IsNullAsync() {
    await Assert.That(ReceiveConsumerGate.KnownServiceName(null)).IsNull();
    await Assert.That(ReceiveConsumerGate.KnownServiceName(UnknownServiceInstanceProvider.Instance)).IsNull();
  }

  [Test]
  public async Task KnownServiceName_NamedProvider_IsItsNameAsync() {
    await Assert.That(ReceiveConsumerGate.KnownServiceName(new Named())).IsEqualTo(THIS_SERVICE);
  }

  private sealed class Named : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.Empty;
    public string ServiceName => THIS_SERVICE;
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { ServiceName = THIS_SERVICE, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }
}
