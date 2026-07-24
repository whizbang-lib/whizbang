using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// RED-first regression locks for slice 1 of resilient-transport-and-deserialization. The reader
/// must surface enough headers to durably store the message in <c>wh_inbox</c> WITHOUT touching
/// the typed payload. If a future refactor reintroduces a typed deserialize at receive time,
/// these tests fail.
/// </summary>
public class AsbMessageHeaderReaderTests {
  private static ServiceBusReceivedMessage _build(
      string envelopeJson,
      string? envelopeTypeName,
      Guid? messageIdFromHeader = null,
      string? messageTypeName = null,
      Guid? streamId = null,
      string? correlationId = null,
      string? causationId = null) {
    var props = new Dictionary<string, object>();
    if (envelopeTypeName != null) {
      props[AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = envelopeTypeName;
    }
    if (messageIdFromHeader.HasValue) {
      props[AsbMessageHeaderReader.MESSAGE_ID_PROPERTY_KEY] = messageIdFromHeader.Value.ToString();
    }
    if (messageTypeName != null) {
      props[AsbMessageHeaderReader.MESSAGE_TYPE_PROPERTY_KEY] = messageTypeName;
    }
    if (streamId.HasValue) {
      props[AsbMessageHeaderReader.STREAM_ID_PROPERTY_KEY] = streamId.Value.ToString();
    }
    if (causationId != null) {
      props[AsbMessageHeaderReader.CAUSATION_ID_PROPERTY_KEY] = causationId;
    }

    return ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString(envelopeJson),
      properties: props,
      correlationId: correlationId
    );
  }

  [Test]
  public async Task Read_ValidMessageWithLiftedHeaders_ReturnsHeadersWithBodyBytesPreservedAsync() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var body = """{"id":"00000000-0000-0000-0000-000000000000","p":{},"h":[],"v":2}""";
    var message = _build(
      envelopeJson: body,
      envelopeTypeName: "Whizbang.Core.Observability.MessageEnvelope`1[[MyEvent, MyContracts]]",
      messageIdFromHeader: msgId,
      messageTypeName: "MyEvent, MyContracts",
      streamId: streamId,
      correlationId: "trace-abc",
      causationId: "11111111-1111-1111-1111-111111111111"
    );

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNotNull();
    await Assert.That(headers!.MessageId).IsEqualTo(MessageId.From(msgId));
    await Assert.That(headers.EnvelopeTypeName).IsEqualTo("Whizbang.Core.Observability.MessageEnvelope`1[[MyEvent, MyContracts]]");
    await Assert.That(headers.MessageTypeName).IsEqualTo("MyEvent, MyContracts");
    await Assert.That(headers.StreamId).IsEqualTo(streamId);
    await Assert.That(headers.CorrelationId).IsEqualTo("trace-abc");
    await Assert.That(headers.CausationId).IsEqualTo("11111111-1111-1111-1111-111111111111");
    await Assert.That(headers.PayloadJson).IsEqualTo(body);
  }

  [Test]
  public async Task Read_MissingEnvelopeTypeProperty_ReturnsNullAsync() {
    var message = _build(
      envelopeJson: """{"id":"00000000-0000-0000-0000-000000000001"}""",
      envelopeTypeName: null,  // no EnvelopeType property
      messageIdFromHeader: Guid.NewGuid()
    );

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNull();
  }

  [Test]
  public async Task Read_MalformedJsonBody_ButLiftedHeaders_StillReturnsHeadersAsync() {
    // Proves slice 1's invariant: malformed payloads do NOT block storage. The header reader
    // never touches the body when MessageId is available from the ApplicationProperty fast path.
    var msgId = (Guid)TrackedGuid.NewMedo();
    var garbage = "not even close to JSON{{{{{{";
    var message = _build(
      envelopeJson: garbage,
      envelopeTypeName: "Whizbang.Core.Observability.MessageEnvelope`1[[Anything]]",
      messageIdFromHeader: msgId
    );

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNotNull();
    await Assert.That(headers!.MessageId).IsEqualTo(MessageId.From(msgId));
    await Assert.That(headers.PayloadJson).IsEqualTo(garbage);
  }

  [Test]
  public async Task Read_NoLiftedMessageIdHeader_FallsBackToShallowJsonParseAsync() {
    // Backward compat with publishers that haven't been updated to lift MessageId to a header.
    // Reader extracts the envelope's "id" property via Utf8JsonReader without binding the typed
    // payload — important because the publisher's contracts assembly may not be loadable here.
    var envelopeId = (Guid)TrackedGuid.NewMedo();
    var body = $$"""{"v":2,"id":"{{envelopeId}}","p":{"unknownField":"value"},"h":[]}""";
    var message = _build(
      envelopeJson: body,
      envelopeTypeName: "Whizbang.Core.Observability.MessageEnvelope`1[[Foo]]",
      messageIdFromHeader: null  // forcing the fallback path
    );

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNotNull();
    await Assert.That(headers!.MessageId).IsEqualTo(MessageId.From(envelopeId));
  }

  [Test]
  public async Task Read_NoMessageIdAnywhere_ReturnsNullAsync() {
    // Body has no "id" property AND no header. Reader cannot route this message; null signals
    // the caller to dead-letter (legitimate — the publisher produced something un-routable).
    var message = _build(
      envelopeJson: """{"v":2,"p":{},"h":[]}""",  // no "id"
      envelopeTypeName: "Whizbang.Core.Observability.MessageEnvelope`1[[Foo]]",
      messageIdFromHeader: null
    );

    var reader = new AsbMessageHeaderReader();
    var headers = reader.Read(message);

    await Assert.That(headers).IsNull();
  }
}
