// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default <see cref="IReceptorRegistryQuery"/> — delegates to the source-generated
/// <c>Whizbang.Core.Generated.WhizbangReceptorRegistryQuery</c> static class. AOT-safe;
/// no reflection.
/// </summary>
/// <remarks>Registered as a singleton by
/// <c>WorkerPipelineExtensions.AddWhizbangWorkers</c>.</remarks>
/// <docs>internals/receptor-registry-query</docs>
/// <param name="runtimeRegistry">The runtime receptor registry; when supplied, runtime-registered
/// receptors (the control-plane surface) count as consumers at the discard gates.</param>
public sealed class WhizbangReceptorRegistryQueryAdapter(IReceptorRegistry runtimeRegistry) : IReceptorRegistryQuery {
  /// <inheritdoc />
  public bool HasReceptors(LifecycleStage stage, string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasReceptors(stage, messageType);

  /// <inheritdoc />
  public bool HasInboxHandler(string messageType)
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasInboxHandler(messageType);

  /// <inheritdoc />
  /// <remarks>
  /// An envelope-wrapped name (<c>MessageEnvelope`1[[payload]]</c>) is answered for the payload it
  /// wraps. The envelope is a transport wrapper nothing consumes; a transport handing the gate its
  /// envelope type name, or a row recovered from dead-letter custody under it (#934), is asking
  /// about the payload.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WhizbangReceptorRegistryQueryAdapterTests.cs:HasAnyConsumer_EnvelopeWrappedName_OfAStaticallyConsumedPayload_CountsAsConsumerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WhizbangReceptorRegistryQueryAdapterTests.cs:HasAnyConsumer_EnvelopeWrappedName_OfARuntimeConsumedPayload_CountsAsConsumerAsync</tests>
  public bool HasAnyConsumer(string messageType) {
    var payloadType = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(messageType);
    return Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.HasAnyConsumer(payloadType)
       || runtimeRegistry.HasRuntimeConsumerFor(payloadType);
  }

  /// <inheritdoc />
  /// <remarks>Runtime-registered receptors (the control-plane surface) are NOT enumerated
  /// here — they register after startup and carry no compile-time namespace/kind metadata;
  /// the predicate surface (<see cref="HasAnyConsumer"/>) still covers them at the discard
  /// gates.</remarks>
  public IReadOnlyList<HandledMessageInfo> GetHandledMessages()
    => Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.GetHandledMessages();
}
