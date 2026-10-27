// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Reads a received body as an envelope as received: <see cref="MessageEnvelope{TMessage}"/> of <see cref="JsonElement"/>,
/// whatever its payload type, so a transport can hand on to be stored a valid envelope it cannot read as its typed form
/// (an unknown type, or a body the typed read rejects) instead of settling it with the broker unstored (#1720).
/// </summary>
/// <docs>messaging/transports/transports#transport-message</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/ReceivedEnvelopeReaderTests.cs</tests>
public static class ReceivedEnvelopeReader {
  /// <summary>
  /// The body as an envelope as received, or <see langword="null"/> when it is not one: not JSON, JSON missing a
  /// member every envelope carries, no message id, or an id that is not a message id (MessageId validates it as it is
  /// read).
  /// </summary>
  /// <param name="body">The received body.</param>
  /// <param name="options">The transport's serializer options; the framework's own are used when they describe no
  /// envelope of <see cref="JsonElement"/>.</param>
  /// <returns>The envelope, or <see langword="null"/>.</returns>
  public static MessageEnvelope<JsonElement>? TryRead(string? body, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrWhiteSpace(body)) {
      return null;
    }
    var typeInfo = _ownTypeInfo(options)
      ?? Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    try {
      // An envelope missing its id reads with the empty id, which nothing stores or gives custody under.
      return JsonSerializer.Deserialize(body, typeInfo) is MessageEnvelope<JsonElement> envelope
             && envelope.MessageId.Value != Guid.Empty
        ? envelope
        : null;
    } catch (Exception ex) when (ex is JsonException or ArgumentException) {
      return null;
    }
  }

  /// <summary>
  /// The transport's own metadata for an envelope of <see cref="JsonElement"/>, or <see langword="null"/> when its
  /// options describe none: a resolver that returns nothing for the type, or one that throws or answers with metadata
  /// for another type (which the serializer reports as <see cref="InvalidOperationException"/>).
  /// </summary>
  private static JsonTypeInfo? _ownTypeInfo(JsonSerializerOptions options) {
    try {
      return options.TryGetTypeInfo(typeof(MessageEnvelope<JsonElement>), out var own) ? own : null;
    } catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) {
      return null;
    }
  }
}
