// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Workers;

/// <summary>
/// Builds the inbox row for a message received from a transport: the one place both consumer workers
/// (<see cref="TransportConsumerWorker"/> and <see cref="ServiceBusConsumerWorker"/>) turn a received
/// envelope into an <see cref="InboxMessage"/>, so the two cannot drift.
/// </summary>
/// <remarks>
/// The row names the service that produced the message: <see cref="InboxMessage.SourceServiceId"/> and
/// <see cref="InboxMessage.SourceCommitSequence"/> are copied from the envelope, and the store stamps this
/// service's own id only on a row whose envelope carries none. One of the two workers used to leave them
/// unset, so every row it stored was stamped with the consumer's id and the column could not tell producers
/// apart (#739). Flags and the ephemeral TTL derive name-first from the wire type, since the payload is
/// usually still a <see cref="JsonElement"/> here.
/// </remarks>
/// <docs>messaging/inbox-pattern#source-identity</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/ServiceBusConsumerSourceIdentityTests.cs</tests>
internal static class ReceivedInboxMessageBuilder {
  /// <summary>
  /// Classifies a received message's priority through the receive hooks registered in <paramref name="scope"/>
  /// (priority step 1); without a chain the declared number's effective value is used, so a row is never stored
  /// as zero.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Priority/ConsumerPriorityClassificationTests.cs</tests>
  internal static int Classify(IServiceProvider scope, IMessageEnvelope envelope, string messageTypeName) {
    var chain = scope.GetService<Whizbang.Core.Priority.PriorityHookChain>();
    return chain is null
      ? Whizbang.Core.Priority.WorkPriority.Effective(envelope.Priority)
      : chain.Classify(new Whizbang.Core.Priority.PriorityReceiveContext(envelope.Priority, envelope, messageTypeName));
  }

  /// <summary>
  /// Whether a received message is an event the inbox copies into the event store: the service's event catalog
  /// decides by the message type name, and only a host with no catalog falls back to the payload's own marker. The
  /// one classification both consumer workers and dead-letter recovery use, so a recovered message is classified
  /// exactly as its delivery was (#1664).
  /// </summary>
  /// <param name="scope">The scope the catalog is resolved from.</param>
  /// <param name="messageTypeName">The payload type name.</param>
  /// <param name="payload">The payload; only consulted when no catalog is registered.</param>
  /// <returns><see langword="true"/> for an event.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/ReceivedInboxMessageBuilderRedeliveryTests.cs</tests>
  internal static bool ClassifiesAsEvent(IServiceProvider scope, string messageTypeName, object? payload) {
    var eventTypeProvider = scope.GetRequiredService<IEventTypeProvider>();
    return eventTypeProvider.IsAvailable
      ? EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypeProvider.GetEventTypes())
      : payload is IEvent;
  }

  /// <summary>
  /// Builds the inbox row a dead-lettered message re-enters with: the row a delivery of the same envelope is built
  /// as (the same classification, stream, flags, scope, producer identity, priority and handler name), naming the
  /// dead letter it came from. Recovery stores it through the inbox's own arrival path, so nothing about the row can
  /// drift from a delivery (#1664).
  /// </summary>
  /// <param name="scope">The scope the catalog, resolvers, priority hooks and serializer options are resolved from.</param>
  /// <param name="redelivery">The dead letter's stored envelope and payload type.</param>
  /// <returns>The inbox row to store.</returns>
  /// <exception cref="InvalidOperationException">The stored envelope is empty or names another message.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Workers/ReceivedInboxMessageBuilderRedeliveryTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:StoredStream_IsTheStreamTheProducerStoredItOnAsync</tests>
  internal static InboxMessage BuildRedelivery(IServiceProvider scope, DeadLetterRedelivery redelivery) {
    var jsonOptions = scope.GetService<JsonSerializerOptions>() ?? Serialization.JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var envelope = JsonSerializer.Deserialize(redelivery.EnvelopeJson, typeInfo) as MessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException(
        $"Dead letter {redelivery.DeadLetterId} stores no envelope, so it cannot re-enter the inbox.");
    if (envelope.MessageId.Value != redelivery.SourceId) {
      throw new InvalidOperationException(
        $"Dead letter {redelivery.DeadLetterId} is for message {redelivery.SourceId}, but its stored envelope is "
        + $"message {envelope.MessageId.Value}; it is not re-entered under either id.");
    }
    // The payload type, whichever form the row recorded it in: a broker import made before #934 recorded the envelope.
    var messageTypeName = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(redelivery.MessageType);
    var row = Build(
      new ReceivedEnvelope(envelope, envelope, EnvelopeTypeNameHelper.Format(messageTypeName), messageTypeName,
        ClassifiesAsEvent(scope, messageTypeName, envelope.Payload)),
      Classify(scope, envelope, messageTypeName),
      "DeadLetterRecovery.Inbox",
      scope.GetService<IEventMarkerResolver>(),
      scope.GetService<IEphemeralModeResolver>());
    return row with { RecoveredFromDeadLetterId = redelivery.DeadLetterId };
  }

  /// <summary>
  /// What a consumer worker hands the builder: the envelope typed and in storage form, the envelope type name the
  /// transport carried (authoritative), the message type name the shared helper extracted from it, and whether the
  /// message is an event (catalog first, marker as the fallback).
  /// </summary>
  internal readonly record struct ReceivedEnvelope(
      IMessageEnvelope Envelope,
      IMessageEnvelope<JsonElement> JsonEnvelope,
      string EnvelopeTypeFromTransport,
      string MessageTypeName,
      bool IsEvent);

  /// <summary>Builds the row. The caller has already resolved the storage-form envelope and the message's classification.</summary>
  /// <param name="received">The envelope as received, in both forms, with the names and the event flag the consumer resolved.</param>
  /// <param name="priority">The effective priority this consumer classified (<see cref="Classify"/>).</param>
  /// <param name="guardSite">Names the caller in the empty-stream guard's message.</param>
  /// <param name="eventMarkerResolver">Catalog-backed flag stamp, when registered.</param>
  /// <param name="ephemeralModeResolver">Catalog-backed ephemeral stamp, when registered.</param>
  /// <param name="originMonitor">Counts received events with and without the producer's origin (#1029), when registered.</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:StoredStream_IsTheStreamTheProducerStoredItOnAsync</tests>
  internal static InboxMessage Build(
      ReceivedEnvelope received,
      int priority,
      string guardSite,
      IEventMarkerResolver? eventMarkerResolver,
      IEphemeralModeResolver? ephemeralModeResolver,
      ReceivedOriginStampMonitor? originMonitor = null) {
    var (envelope, jsonEnvelope, envelopeTypeFromTransport, messageTypeName, isEvent) = received;
    // The stream its producer stored it on, read the way every storing site reads it (#1798).
    var streamId = StreamIdentity.Of(envelope);
    if (isEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, envelope.MessageId.Value, guardSite, messageTypeName);
    }

    var payload = envelope.Payload;
    var row = new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = TypeNameFormatter.GetSimpleName(messageTypeName) + "Handler",
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,
      StreamId = streamId,
      IsEvent = isEvent,
      Flags = EventFlagsDeriver.Derive(payload, messageTypeName, eventMarkerResolver, ephemeralModeResolver),
      Scope = envelope.GetCurrentPerspectiveScope(),
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.CopyHops(),
        DispatchContext = envelope.DispatchContext,
        EphemeralTtlSeconds = EphemeralTtlDeriver.Derive(payload, messageTypeName, ephemeralModeResolver)
      },
      MessageType = messageTypeName,
      SourceServiceId = envelope.SourceServiceId,
      SourceCommitSequence = envelope.SourceCommitSequence,
      Priority = priority,
    };
    originMonitor?.Record(row);
    return row;
  }
}
