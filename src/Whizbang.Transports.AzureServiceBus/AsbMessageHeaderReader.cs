using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Extracts <see cref="MessageHeaders"/> from a <see cref="ServiceBusReceivedMessage"/> WITHOUT
/// deserializing the typed envelope body. The transport's job is to durably transfer bytes; this
/// reader produces just enough routing metadata for the inbox to store and dedupe the message,
/// leaving payload interpretation to the dispatcher.
/// </summary>
/// <remarks>
/// Reads three ApplicationProperties: <c>EnvelopeType</c> (required; the envelope wrapper's CLR
/// type name), <c>MessageId</c> (optional fast-path; falls back to a shallow JSON parse for
/// publishers that haven't been updated to lift it), and <c>MessageType</c> (optional; the inner
/// payload's CLR type name, used by slice 2's receptor-registry filter). The body bytes are
/// preserved exactly as delivered — the dispatcher records DeserializationFailed on the inbox
/// row at handler-invoke time if the payload turns out to be malformed.
/// </remarks>
/// <docs>fundamentals/transport/asb-receive</docs>
[SuppressMessage("Performance", "CA1822:Mark members as static",
  Justification = "Instance method enables DI registration as singleton; future revisions may inject ILogger / counters.")]
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
  /// Reads routing headers from <paramref name="message"/>. Returns null when the
  /// <c>EnvelopeType</c> ApplicationProperty is missing OR the envelope's MessageId cannot be
  /// determined from either the ApplicationProperty fast-path or a shallow JSON-body parse —
  /// both cases the caller should dead-letter (the broker has delivered something un-routable).
  /// </summary>
  public MessageHeaders? Read(ServiceBusReceivedMessage message) {
    ArgumentNullException.ThrowIfNull(message);

    if (!message.ApplicationProperties.TryGetValue(ENVELOPE_TYPE_PROPERTY_KEY, out var envelopeTypeObj)
        || envelopeTypeObj is not string envelopeTypeName
        || string.IsNullOrEmpty(envelopeTypeName)) {
      return null;
    }

    var payloadJson = message.Body.ToString();

    if (!_tryGetMessageId(message, payloadJson, out var messageIdGuid)) {
      return null;
    }

    var messageTypeName = _tryGetStringProperty(message, MESSAGE_TYPE_PROPERTY_KEY);
    var streamId = _tryGetGuidProperty(message, STREAM_ID_PROPERTY_KEY);
    var causationId = _tryGetStringProperty(message, CAUSATION_ID_PROPERTY_KEY);
    var correlationId = string.IsNullOrEmpty(message.CorrelationId) ? null : message.CorrelationId;

    return new MessageHeaders {
      MessageId = MessageId.From(messageIdGuid),
      EnvelopeTypeName = envelopeTypeName,
      MessageTypeName = messageTypeName,
      StreamId = streamId,
      CorrelationId = correlationId,
      CausationId = causationId,
      PayloadJson = payloadJson,
    };
  }

  private static bool _tryGetMessageId(ServiceBusReceivedMessage message, string payloadJson, out Guid messageId) {
    // Fast path — publisher lifted MessageId to ApplicationProperty.
    if (message.ApplicationProperties.TryGetValue(MESSAGE_ID_PROPERTY_KEY, out var idObj)
        && idObj is string idStr
        && Guid.TryParse(idStr, out messageId)) {
      return true;
    }

    // Backward-compat fallback — shallow JSON parse to extract envelope's "id" property without
    // binding the typed payload. Survives older publishers that didn't lift MessageId to a
    // header. Utf8JsonReader is allocation-light and bails as soon as it finds the property.
    try {
      var bodyBytes = Encoding.UTF8.GetBytes(payloadJson);
      var reader = new Utf8JsonReader(bodyBytes);
      while (reader.Read()) {
        if (reader.TokenType != JsonTokenType.PropertyName) {
          continue;
        }
        if (!reader.ValueTextEquals("id")) {
          continue;
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.String) {
          break;
        }
        var value = reader.GetString();
        if (value != null && Guid.TryParse(value, out messageId)) {
          return true;
        }
        break;
      }
    } catch (JsonException) {
      // Body isn't valid JSON. Without the lifted-header fast path, we cannot route this
      // message — caller will dead-letter on the null return.
    }

    messageId = default;
    return false;
  }

  private static string? _tryGetStringProperty(ServiceBusReceivedMessage message, string key) {
    if (message.ApplicationProperties.TryGetValue(key, out var obj) && obj is string str && !string.IsNullOrEmpty(str)) {
      return str;
    }
    return null;
  }

  private static Guid? _tryGetGuidProperty(ServiceBusReceivedMessage message, string key) {
    var str = _tryGetStringProperty(message, key);
    if (str != null && Guid.TryParse(str, out var guid)) {
      return guid;
    }
    return null;
  }
}
