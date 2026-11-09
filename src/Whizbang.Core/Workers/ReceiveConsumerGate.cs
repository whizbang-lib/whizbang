// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Workers;

/// <summary>
/// The "no local consumer" decision both consumer workers make for a received message, in one place so
/// <see cref="TransportConsumerWorker"/> and <see cref="ServiceBusConsumerWorker"/> cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// A message nothing on this service consumes is stored settled with <see cref="InboxReceiveDiscard.NO_LOCAL_CONSUMER"/>
/// (#1720), so it is never claimed, copied into the event store or dispatched. The receptor registries alone cannot make
/// that call: an event in an absorbed namespace (<see cref="RoutingOptions.AbsorbedNamespaces"/>) has no consumer here by
/// design and must still reach the event store, so a perspective added later can rebuild from it (#1776). Those
/// exemptions belong to the shared <see cref="IMessageDiscardPolicy"/>, so the gate asks it rather than repeating its
/// namespace matching.
/// </para>
/// <para>
/// The registries are still asked first. The runtime registry is one the policy does not see, and the compile-time
/// registry and the composite check are the decision a host without the worker pipeline (and so without a policy)
/// has always had. The policy can only keep more: it is consulted when the registries find no consumer.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#owned-and-subscribed</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ReceiveConsumerGateTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerStoresBeforeSkippingTests.cs:AnUnconsumedEventInAnAbsorbedNamespace_IsStoredLive_SoTheEventStoreCapturesItAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/ServiceBusConsumerWorkerDropGateTests.cs:HandleMessage_UnconsumedEventInAnAbsorbedNamespace_IsStoredLive_SoTheEventStoreCapturesItAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AbsorbedEventReachesEventStoreTests.cs:AnUnconsumedEventInAnAbsorbedNamespace_IsCopiedIntoTheEventStoreAsync</tests>
internal static class ReceiveConsumerGate {
  /// <summary>The assembly framework control and minted payload types are defined in.</summary>
  private const string FRAMEWORK_ASSEMBLY = "Whizbang.Core";

  /// <summary>
  /// The inner message type of a received message nothing on this service consumes, or <see langword="null"/> when
  /// something does, or when the envelope type cannot say.
  /// </summary>
  /// <param name="envelopeType">The envelope type name the transport carried.</param>
  /// <param name="registry">The consumer registry.</param>
  /// <param name="runtimeRegistry">The runtime receptor registry (integration-test waits, dynamic registrations).</param>
  /// <param name="markerResolver">Recognizes composites, which nothing consumes as such.</param>
  /// <param name="discardPolicy">The shared discard policy, when the worker pipeline registered one.</param>
  /// <returns>The unconsumed inner type, or <see langword="null"/>.</returns>
  /// <remarks>
  /// Unknowable cases keep the message: no envelope type, a format the inner type cannot be read from, and a
  /// body-offload claim, whose wire type nothing consumes but whose real type is restored downstream.
  /// </remarks>
  internal static string? UnconsumedMessageType(
      string? envelopeType,
      IConsumerRegistry registry,
      IReceptorRegistry runtimeRegistry,
      IEventMarkerResolver markerResolver,
      IMessageDiscardPolicy? discardPolicy) {
    if (string.IsNullOrWhiteSpace(envelopeType) || EnvelopeTypeNameHelper.IsBodyClaimEnvelope(envelopeType)) {
      return null;
    }
    var innerMessageType = EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeType);
    if (innerMessageType is null || Keeps(innerMessageType, registry, runtimeRegistry, markerResolver, discardPolicy)) {
      return null;
    }
    return innerMessageType;
  }

  /// <summary>
  /// The reason a received FRAMEWORK control or minted message is discarded on receipt with no inbox row and no other
  /// work (#2091), or <see langword="null"/> when it is kept or is not framework traffic.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Framework traffic (payload types under the reserved <c>whizbang.core</c> namespace, see
  /// <see cref="CommandInboxNaming.IsFrameworkReserved"/>) is re-derived by its producer on the next cycle, so a copy
  /// this service cannot use is worth nothing stored. Storing it settled (#1720) still cost a row per copy, and a copy
  /// that reached dispatch cost real database work; under load either one became the retry and dead-letter loop
  /// #2091 measured. It is discarded here, before the control-class comparison, storage or fan-out, whatever entity
  /// it arrived on: when it is addressed to another service (its <see cref="IMessageEnvelope.Target"/>), or when
  /// nothing on this service consumes it.
  /// </para>
  /// <para>
  /// Domain traffic keeps #1720's durable settled row, and a body-offload claim keeps its message, whose real type is
  /// restored downstream. A service that cannot name itself fails open on the target, as the other target gates do.
  /// </para>
  /// </remarks>
  /// <param name="envelopeType">The envelope type name the transport carried.</param>
  /// <param name="target">The envelope's target service, or null for a broadcast.</param>
  /// <param name="serviceName">This service's name, or null when it cannot know it.</param>
  /// <param name="unconsumed">Whether <see cref="UnconsumedMessageType"/> found no consumer for the message.</param>
  /// <returns><see cref="InboxReceiveDiscard.FOREIGN_TARGET"/>, <see cref="InboxReceiveDiscard.NO_LOCAL_CONSUMER"/>, or
  /// <see langword="null"/>.</returns>
  /// <docs>messaging/transports/transport-consumer#framework-traffic-discarded-on-receipt</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/ReceiveConsumerGateTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerDirectedTargetTests.cs:ForeignTarget_FrameworkMintedMessage_IsDiscardedWithoutARowAsync</tests>
  internal static string? FrameworkDiscardOnReceipt(string? envelopeType, string? target, string? serviceName, bool unconsumed) {
    if (!IsFrameworkMessage(envelopeType)) {
      return null;
    }
    if (target is not null && serviceName is not null && !string.Equals(target, serviceName, StringComparison.Ordinal)) {
      return InboxReceiveDiscard.FOREIGN_TARGET;
    }
    return unconsumed ? InboxReceiveDiscard.NO_LOCAL_CONSUMER : null;
  }

  /// <summary>
  /// This service's name for the target gates, or <see langword="null"/> when it cannot know it: no instance provider,
  /// or the provider's unknown identity, which is not a name and must never make every targeted message look foreign.
  /// </summary>
  /// <param name="instanceProvider">The service instance provider, if one is registered.</param>
  /// <returns>The service name, or null.</returns>
  internal static string? KnownServiceName(IServiceInstanceProvider? instanceProvider) {
    var name = instanceProvider?.ServiceName;
    return string.Equals(name, ServiceInstanceInfo.Unknown.ServiceName, StringComparison.Ordinal) ? null : name;
  }

  /// <summary>
  /// True when the envelope carries a framework payload type (the reserved <c>whizbang.core</c> namespace, defined in
  /// the framework assembly when the name is assembly-qualified), excluding the body-offload claim, which stands in for
  /// a message of any type.
  /// </summary>
  /// <param name="envelopeType">The envelope type name the transport carried.</param>
  /// <returns>True for framework control or minted traffic.</returns>
  internal static bool IsFrameworkMessage(string? envelopeType) {
    if (string.IsNullOrWhiteSpace(envelopeType) || EnvelopeTypeNameHelper.IsBodyClaimEnvelope(envelopeType)) {
      return false;
    }
    var inner = EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeType);
    if (inner is null) {
      return false;
    }
    var comma = inner.IndexOf(',', StringComparison.Ordinal);
    var fullName = comma < 0 ? inner : inner[..comma];
    // An assembly-qualified name must name the framework assembly itself: a host's own types can sit under a namespace
    // that starts with the reserved one (a test assembly does), and those are domain traffic.
    if (comma >= 0) {
      var assembly = inner[(comma + 1)..];
      var nextComma = assembly.IndexOf(',', StringComparison.Ordinal);
      var assemblyName = (nextComma < 0 ? assembly : assembly[..nextComma]).Trim();
      if (!string.Equals(assemblyName, FRAMEWORK_ASSEMBLY, StringComparison.OrdinalIgnoreCase)) {
        return false;
      }
    }
    var lastDot = fullName.LastIndexOf('.');
    return lastDot > 0 && CommandInboxNaming.IsFrameworkReserved(fullName[..lastDot]);
  }

  /// <summary>
  /// Whether this service keeps a received message of the given payload type: something here consumes it, it is a
  /// composite (consumed by the fan-out), or the discard policy keeps it (an absorbed namespace, a tag this host acts
  /// on). The one decision the receive boundary and stream integrity's filters share (#2058).
  /// </summary>
  /// <param name="innerMessageType">The payload type name.</param>
  /// <param name="registry">The consumer registry; null keeps nothing on its account.</param>
  /// <param name="runtimeRegistry">The runtime receptor registry.</param>
  /// <param name="markerResolver">Recognizes composites; null recognizes none.</param>
  /// <param name="discardPolicy">The shared discard policy, when the worker pipeline registered one.</param>
  /// <returns>True when the message is kept.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ConsumedMessageTypesTests.cs</tests>
  internal static bool Keeps(
      string innerMessageType,
      IConsumerRegistry? registry,
      IReceptorRegistry runtimeRegistry,
      IEventMarkerResolver? markerResolver,
      IMessageDiscardPolicy? discardPolicy) =>
    registry?.HasAnyConsumer(innerMessageType) == true
      || runtimeRegistry.HasAnyRuntimeReceptors(innerMessageType)
      || CompositeInboxFanout.IsCompositeWireType(innerMessageType, markerResolver)
      // No policy (a host without the worker pipeline) keeps the registry-only decision. The receive gate's topic and
      // subscription are not part of the decision: the policy derives the namespace from the payload type, since a
      // transport's topic can be a queue name.
      || discardPolicy?.EvaluateReceive(innerMessageType, string.Empty, string.Empty).ShouldDiscard == false;
}
