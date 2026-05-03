using System;
using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;
using Whizbang.Core.Messaging;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Extracts <see cref="MessageHeaders"/> from a <see cref="ServiceBusReceivedMessage"/> WITHOUT
/// deserializing the typed envelope body. The transport's job is to durably transfer bytes; this
/// reader produces just enough routing metadata for the inbox to store and dedupe the message,
/// leaving payload interpretation to the dispatcher.
/// </summary>
/// <docs>fundamentals/transport/asb-receive</docs>
[SuppressMessage("Performance", "CA1822:Mark members as static",
  Justification = "Instance method enables DI registration as singleton; full impl in GREEN commit will use injected logger.")]
public sealed class AsbMessageHeaderReader {
  /// <summary>ApplicationProperty key for the envelope wrapper's CLR type name.</summary>
  internal const string ENVELOPE_TYPE_PROPERTY_KEY = "EnvelopeType";

  /// <summary>ApplicationProperty key for the envelope's MessageId (fast-path; falls back to JSON parse).</summary>
  internal const string MESSAGE_ID_PROPERTY_KEY = "MessageId";

  /// <summary>ApplicationProperty key for the inner payload's CLR type name.</summary>
  internal const string MESSAGE_TYPE_PROPERTY_KEY = "MessageType";

  /// <summary>ApplicationProperty key for the optional stream identifier.</summary>
  internal const string STREAM_ID_PROPERTY_KEY = "StreamId";

  /// <summary>ApplicationProperty key for the optional causation identifier.</summary>
  internal const string CAUSATION_ID_PROPERTY_KEY = "CausationId";

  /// <summary>
  /// Reads routing headers from <paramref name="message"/>. Returns null when the headers cannot
  /// be extracted; the caller should dead-letter (the broker has delivered something we cannot
  /// route). Stub — implementation lands in the GREEN commit.
  /// </summary>
  public MessageHeaders? Read(ServiceBusReceivedMessage message) {
    ArgumentNullException.ThrowIfNull(message);
    return null;
  }
}
