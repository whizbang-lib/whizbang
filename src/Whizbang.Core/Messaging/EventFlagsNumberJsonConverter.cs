// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Writes <see cref="EventFlags"/> as its number whatever converters the serializer options carry, so a host whose
/// options include <see cref="JsonStringEnumConverter"/> still hands the store functions every bit (#1612). Applied
/// to <see cref="OutboxMessage.Flags"/> and <see cref="InboxMessage.Flags"/>; a property-level converter outranks
/// the options' converters.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventFlagsWireFormTests.cs</tests>
public sealed class EventFlagsNumberJsonConverter : JsonConverter<EventFlags> {
  /// <inheritdoc />
  public override EventFlags Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType == JsonTokenType.Number) {
      return (EventFlags)reader.GetInt32();
    }
    throw new JsonException($"Event flags must be a number; read {reader.TokenType}.");
  }

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, EventFlags value, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(writer);
    writer.WriteNumberValue((int)value);
  }
}
