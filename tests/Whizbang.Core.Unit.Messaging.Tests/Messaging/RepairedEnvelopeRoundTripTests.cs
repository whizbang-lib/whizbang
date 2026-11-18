// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2273: the fan-out marks a repaired child that may run its opted-in receptors on the child's stored envelope, so the
/// marker must survive the stored form the inbox row keeps, and is absent from every other envelope.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/MessageEnvelope.cs</code-under-test>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
public class RepairedEnvelopeRoundTripTests {
  private static MessageEnvelope<JsonElement> _stored(bool repaired) => new() {
    MessageId = MessageId.From(TrackedGuid.New()),
    Payload = JsonElement.Parse("{}"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
    Repaired = repaired,
  };

  [Test]
  public async Task ARepairedChild_KeepsTheMarker_ThroughTheStoredFormAsync() {
    var json = JsonSerializer.Serialize(_stored(repaired: true), InfrastructureJsonContext.Default.MessageEnvelopeJsonElement);

    var back = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.MessageEnvelopeJsonElement)!;

    await Assert.That(json).Contains("\"rep\":true");
    await Assert.That(back.Repaired).IsTrue();
  }

  [Test]
  public async Task AnOrdinaryEnvelope_WritesNoMarkerAsync() {
    var json = JsonSerializer.Serialize(_stored(repaired: false), InfrastructureJsonContext.Default.MessageEnvelopeJsonElement);

    await Assert.That(json).DoesNotContain("\"rep\"")
      .Because("the overwhelmingly common delivery pays nothing on the wire");
  }

  [Test]
  public async Task ARetypedEnvelope_KeepsTheMarkerAsync() {
    var untyped = new MessageEnvelope<object> {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = new object(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      Repaired = true,
    };

    var typed = MessageEnvelope<string>.RetypedFrom(untyped, "payload");

    await Assert.That(typed.Repaired).IsTrue();
  }
}
