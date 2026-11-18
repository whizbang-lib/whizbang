// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The flags on an outbox or inbox message reach the store functions as JSON written with the work
/// coordinator's options. A host can add <see cref="JsonStringEnumConverter"/> to those options, and the
/// flags must still be written as the number, so every bit reaches the row's <c>flags</c> column (#1612).
/// </summary>
/// <docs>fundamentals/messaging/composite-events#treatment-flags</docs>
[Category("Unit")]
public class EventFlagsWireFormTests {
  private const EventFlags EVERY_FLAG = EventFlags.Collective | EventFlags.Composite | EventFlags.NoRebroadcast
    | EventFlags.Ephemeral | EventFlags.Compacted | EventFlags.AtMostOnce;

  [Test]
  public async Task OutboxMessage_OptionsWithAStringEnumConverter_WritesFlagsAsTheNumberAsync() {
    var options = _optionsWithStringEnums();
    OutboxMessage[] messages = [_newOutboxMessage() with { Flags = EVERY_FLAG }];

    var json = JsonSerializer.Serialize(messages, options.GetTypeInfo(typeof(OutboxMessage[])));

    var flags = _flagsOfFirst(json);
    await Assert.That(flags.ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("the store functions read the bits from this field; a name list lost every bit but Collective");
    await Assert.That(flags.GetInt32()).IsEqualTo((int)EVERY_FLAG);
  }

  [Test]
  public async Task InboxMessage_OptionsWithAStringEnumConverter_WritesFlagsAsTheNumberAsync() {
    var options = _optionsWithStringEnums();
    InboxMessage[] messages = [_newInboxMessage() with { Flags = EVERY_FLAG }];

    var json = JsonSerializer.Serialize(messages, options.GetTypeInfo(typeof(InboxMessage[])));

    var flags = _flagsOfFirst(json);
    await Assert.That(flags.ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(flags.GetInt32()).IsEqualTo((int)EVERY_FLAG);
  }

  [Test]
  public async Task OutboxMessage_DefaultOptions_WritesFlagsAsTheNumberAsync() {
    OutboxMessage[] messages = [_newOutboxMessage() with { Flags = EventFlags.Ephemeral | EventFlags.AtMostOnce }];

    var json = JsonSerializer.Serialize(messages, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(OutboxMessage[])));

    var flags = _flagsOfFirst(json);
    await Assert.That(flags.ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(flags.GetInt32()).IsEqualTo((int)(EventFlags.Ephemeral | EventFlags.AtMostOnce));
  }

  [Test]
  public async Task Converter_ReadsTheNumberAsync() {
    await Assert.That(_read("63")).IsEqualTo(EVERY_FLAG);
  }

  [Test]
  public async Task Converter_ReadsANameListAsync() {
    await Assert.That(_read("\"Collective, Ephemeral, AtMostOnce\"")).IsEqualTo(EventFlags.Collective | EventFlags.Ephemeral | EventFlags.AtMostOnce)
      .Because("a message written as names before the flags were pinned to the number still reads with every bit");
  }

  [Test]
  public async Task Converter_NameNoEventFlagsValueHas_ThrowsJsonExceptionAsync() {
    await Assert.That(() => _read("\"Ephemeral, Hibernating\"")).Throws<JsonException>();
  }

  [Test]
  public async Task Converter_NeitherANumberNorAString_ThrowsJsonExceptionAsync() {
    await Assert.That(() => _read("true")).Throws<JsonException>();
  }

  private static EventFlags _read(string json) {
    var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
    reader.Read();
    return new EventFlagsNumberJsonConverter().Read(ref reader, typeof(EventFlags), new JsonSerializerOptions());
  }

  private static JsonSerializerOptions _optionsWithStringEnums() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    options.Converters.Add(new JsonStringEnumConverter());
    return options;
  }

  private static JsonElement _flagsOfFirst(string json) {
    using var document = JsonDocument.Parse(json);
    return document.RootElement[0].GetProperty("Flags").Clone();
  }

  private static OutboxMessage _newOutboxMessage() => new() {
    MessageId = Guid.CreateVersion7(),
    Destination = "test",
    Envelope = _envelope(),
    Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement]], Whizbang.Core",
    MessageType = "System.Object, System.Private.CoreLib",
  };

  private static InboxMessage _newInboxMessage() => new() {
    MessageId = Guid.CreateVersion7(),
    HandlerName = "test-handler",
    Envelope = _envelope(),
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement]], Whizbang.Core",
    MessageType = "System.Object, System.Private.CoreLib",
  };

  private static MessageEnvelope<JsonElement> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonElement.Parse("{}"),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Hops = [],
  };
}
