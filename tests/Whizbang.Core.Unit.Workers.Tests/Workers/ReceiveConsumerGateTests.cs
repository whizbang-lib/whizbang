// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The "no local consumer" decision both consumer workers share (#1776): the receptor registries first, then the
/// shared discard policy, which keeps an event in an absorbed namespace so it reaches the event store.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReceiveConsumerGate.cs</code-under-test>
public class ReceiveConsumerGateTests {
  private const string ABSORBED_TYPE = "Upstream.Contracts.Shipping.ShipmentNoted, Upstream.Contracts";
  private const string OTHER_TYPE = "Upstream.Contracts.Billing.InvoiceNoted, Upstream.Contracts";

  private static readonly string _absorbedEnvelope = EnvelopeTypeNameHelper.Format(ABSORBED_TYPE);
  private static readonly string _otherEnvelope = EnvelopeTypeNameHelper.Format(OTHER_TYPE);

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task NoEnvelopeType_KeepsTheMessageAsync(string? envelopeType) {
    await Assert.That(_gate(envelopeType)).IsNull()
      .Because("with no envelope type the gate cannot tell what the message is, so it keeps it");
  }

  [Test]
  public async Task BodyOffloadClaim_KeepsTheMessageAsync() {
    var claim = EnvelopeTypeNameHelper.Format("Whizbang.Core.Offloads.BodyClaimEnvelopePayload, Whizbang.Core");

    await Assert.That(_gate(claim)).IsNull()
      .Because("nothing consumes the claim type, but the real message is restored from it downstream");
  }

  [Test]
  public async Task EnvelopeTypeWithNoInnerType_KeepsTheMessageAsync() {
    await Assert.That(_gate(OTHER_TYPE)).IsNull()
      .Because("a name that is not a generic envelope names no inner type to judge");
  }

  [Test]
  public async Task CompileTimeConsumer_KeepsTheMessageAsync() {
    await Assert.That(_gate(_otherEnvelope, registry: new Registry(consumes: true))).IsNull();
  }

  [Test]
  public async Task RuntimeReceptor_KeepsTheMessageAsync() {
    await Assert.That(_gate(_otherEnvelope, runtime: new RuntimeRegistry(has: true))).IsNull();
  }

  [Test]
  public async Task Composite_KeepsTheMessageAsync() {
    var catalog = new CompositeCatalog("Upstream.Contracts.Billing.InvoiceNoted");

    await Assert.That(_gate(_otherEnvelope, markers: new EventMarkerResolver(catalog))).IsNull()
      .Because("nothing consumes a composite as such; its inner events are addressable only after fan-out");
  }

  [Test]
  public async Task NoConsumer_AndNoPolicy_IsUnconsumedAsync() {
    await Assert.That(_gate(_otherEnvelope)).IsEqualTo(OTHER_TYPE)
      .Because("a host without the worker pipeline keeps the decision it always had");
  }

  [Test]
  public async Task NoConsumer_InAnAbsorbedNamespace_KeepsTheMessageAsync() {
    using var meter = new Meter("Whizbang.Tests.ReceiveConsumerGateTests.Absorbed");

    await Assert.That(_gate(_absorbedEnvelope, policy: _policy(meter, "Upstream.Contracts.Shipping"))).IsNull()
      .Because("an absorbed event is stored live so the event-store write captures it");
  }

  [Test]
  public async Task NoConsumer_OutsideTheAbsorbedNamespaces_IsUnconsumedAsync() {
    using var meter = new Meter("Whizbang.Tests.ReceiveConsumerGateTests.NotAbsorbed");

    await Assert.That(_gate(_otherEnvelope, policy: _policy(meter, "Upstream.Contracts.Shipping"))).IsEqualTo(OTHER_TYPE)
      .Because("absorbing is namespace-scoped");
  }

  // ===== Helpers =====

  private static string? _gate(
      string? envelopeType,
      IReceptorRegistryQuery? registry = null,
      IReceptorRegistry? runtime = null,
      IEventMarkerResolver? markers = null,
      IMessageDiscardPolicy? policy = null) =>
    ReceiveConsumerGate.UnconsumedMessageType(
      envelopeType,
      registry ?? new Registry(consumes: false),
      runtime ?? new RuntimeRegistry(has: false),
      markers ?? new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      policy);

  private static MessageDiscardPolicy _policy(Meter meter, params string[] absorb) =>
    new(new Registry(consumes: false), NullLogger<MessageDiscardPolicy>.Instance, meter,
      Options.Create(new RoutingOptions().AbsorbNamespaces(absorb)), new EventMarkerResolver(NullMessageTypeCatalog.Instance));

  private sealed class Registry(bool consumes) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => consumes;
    public bool HasInboxHandler(string messageType) => consumes;
    public bool HasAnyConsumer(string messageType) => consumes;
  }

  private sealed class RuntimeRegistry(bool has) : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException();
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool HasAnyRuntimeReceptors(string messageType) => has;
  }

  /// <summary>A catalog naming one wire type as a composite.</summary>
  private sealed class CompositeCatalog(string wireTypeName) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(object), wireTypeName, "event", null) { IsComposite = true },
    ];
  }
}
