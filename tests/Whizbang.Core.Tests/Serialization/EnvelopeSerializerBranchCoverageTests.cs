// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Branch coverage for <see cref="EnvelopeSerializer.SerializeEnvelope{TMessage}"/> with a null
/// payload: there is no runtime type to read, so the wire type names come from the envelope's type
/// parameter, and the payload serializes as JSON null instead of failing on a null dereference.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EnvelopeSerializer.cs</code-under-test>
[Category("Core")]
[Category("Serialization")]
public class EnvelopeSerializerBranchCoverageTests {

  private sealed record NullPayloadProbeEvent(string Name) : IEvent;

  [Test]
  public async Task SerializeEnvelope_NullPayload_TakesTheTypeNamesFromTheTypeParameterAsync() {
    // A resolver is supplied because a bare JsonSerializerOptions has none; what is under test is
    // the serializer's choice of payload type, not the host's resolution strategy.
    var serializer = new EnvelopeSerializer(
      new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
    var envelope = new MessageEnvelope<NullPayloadProbeEvent> {
      MessageId = MessageId.New(),
      Payload = null!,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    var serialized = serializer.SerializeEnvelope(envelope);

    await Assert.That(serialized.MessageType).Contains(nameof(NullPayloadProbeEvent))
      .Because("with no payload instance, the declared message type is the only type there is to name");
    await Assert.That(serialized.EnvelopeType).Contains(nameof(NullPayloadProbeEvent));
    await Assert.That(serialized.JsonEnvelope.Payload.ValueKind).IsEqualTo(JsonValueKind.Null)
      .Because("a null payload is carried as JSON null, not dropped or replaced");
  }
}
