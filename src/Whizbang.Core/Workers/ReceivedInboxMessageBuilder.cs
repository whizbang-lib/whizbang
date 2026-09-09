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
/// <tests>tests/Whizbang.Core.Tests/Workers/ServiceBusConsumerSourceIdentityTests.cs</tests>
internal static class ReceivedInboxMessageBuilder {
  /// <summary>
  /// The stream a received message belongs to: the first hop's <c>AggregateId</c> (the key is historical),
  /// or the message id when there is none, so every message has a stream.
  /// </summary>
  internal static Guid ExtractStreamId(IMessageEnvelope envelope) {
    var firstHop = envelope.Hops?.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var streamIdElem) &&
        streamIdElem.ValueKind == JsonValueKind.String) {
      var streamIdStr = streamIdElem.GetString();
      if (streamIdStr != null && Guid.TryParse(streamIdStr, out var parsedStreamId)) {
        return parsedStreamId;
      }
    }
    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Classifies a received message's priority through the receive hooks registered in <paramref name="scope"/>
  /// (priority step 1); without a chain the declared number's effective value is used, so a row is never stored
  /// as zero.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Priority/ConsumerPriorityClassificationTests.cs</tests>
  internal static int Classify(IServiceProvider scope, IMessageEnvelope envelope, string messageTypeName) {
    var chain = scope.GetService<Whizbang.Core.Priority.PriorityHookChain>();
    return chain is null
      ? Whizbang.Core.Priority.WorkPriority.Effective(envelope.Priority)
      : chain.Classify(new Whizbang.Core.Priority.PriorityReceiveContext(envelope.Priority, envelope, messageTypeName));
  }

  /// <summary>Builds the row. The caller has already resolved the storage-form envelope and the message's classification.</summary>
  /// <param name="envelope">The received envelope, typed or already in storage form.</param>
  /// <param name="jsonEnvelope">The envelope in storage form.</param>
  /// <param name="envelopeTypeFromTransport">The envelope type name the transport carried; authoritative.</param>
  /// <param name="messageTypeName">The message type name extracted from it by the shared helper.</param>
  /// <param name="isEvent">Whether the message is an event (catalog first, marker as the fallback).</param>
  /// <param name="priority">The effective priority this consumer classified (<see cref="Classify"/>).</param>
  /// <param name="guardSite">Names the caller in the empty-stream guard's message.</param>
  /// <param name="eventMarkerResolver">Catalog-backed flag stamp, when registered.</param>
  /// <param name="ephemeralModeResolver">Catalog-backed ephemeral stamp, when registered.</param>
  internal static InboxMessage Build(
      IMessageEnvelope envelope,
      IMessageEnvelope<JsonElement> jsonEnvelope,
      string envelopeTypeFromTransport,
      string messageTypeName,
      bool isEvent,
      int priority,
      string guardSite,
      IEventMarkerResolver? eventMarkerResolver,
      IEphemeralModeResolver? ephemeralModeResolver) {
    var streamId = ExtractStreamId(envelope);
    if (isEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, envelope.MessageId.Value, guardSite, messageTypeName);
    }

    var payload = envelope.Payload;
    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = TypeNameFormatter.GetSimpleName(messageTypeName) + "Handler",
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,
      StreamId = streamId,
      IsEvent = isEvent,
      Flags = EventFlagsDeriver.Derive(payload, messageTypeName, eventMarkerResolver, ephemeralModeResolver),
      Scope = envelope.GetCurrentScope()?.Scope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = envelope.DispatchContext,
        EphemeralTtlSeconds = EphemeralTtlDeriver.Derive(payload, messageTypeName, ephemeralModeResolver)
      },
      MessageType = messageTypeName,
      SourceServiceId = envelope.SourceServiceId,
      SourceCommitSequence = envelope.SourceCommitSequence,
      Priority = priority,
    };
  }
}
