// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
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
  /// <summary>
  /// The inner message type of a received message nothing on this service consumes, or <see langword="null"/> when
  /// something does, or when the envelope type cannot say.
  /// </summary>
  /// <param name="envelopeType">The envelope type name the transport carried.</param>
  /// <param name="registry">The compile-time consumer registry.</param>
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
      IReceptorRegistryQuery registry,
      IReceptorRegistry runtimeRegistry,
      IEventMarkerResolver markerResolver,
      IMessageDiscardPolicy? discardPolicy) {
    if (string.IsNullOrWhiteSpace(envelopeType) || EnvelopeTypeNameHelper.IsBodyClaimEnvelope(envelopeType)) {
      return null;
    }
    var innerMessageType = EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeType);
    if (innerMessageType is null
        || registry.HasAnyConsumer(innerMessageType)
        || runtimeRegistry.HasAnyRuntimeReceptors(innerMessageType)
        || CompositeInboxFanout.IsCompositeWireType(innerMessageType, markerResolver)) {
      return null;
    }
    // No policy (a host without the worker pipeline) keeps the registry-only decision: unconsumed. The receive gate's
    // topic and subscription are not part of the decision: the policy derives the namespace from the payload type,
    // since a transport's topic can be a queue name.
    return discardPolicy?.EvaluateReceive(innerMessageType, string.Empty, string.Empty).ShouldDiscard != false
      ? innerMessageType
      : null;
  }
}
