// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The envelope a receptor looks up for the message it handles: found by the message's own type
/// whether the dispatcher built it typed or for an object payload (#1410 follow-up), and not found
/// under a type it was not built for.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/EnvelopeRegistry.cs</code-under-test>
public class EnvelopeRegistryTests {
  public record BaseNote(string Text);
  public record DerivedNote(string Text) : BaseNote(Text);

  [Test]
  public async Task TryGetEnvelope_RegisteredTyped_ReturnsTheSameEnvelopeAsync() {
    var registry = new EnvelopeRegistry();
    var note = new BaseNote("typed");
    var envelope = _envelope(note);
    registry.Register(envelope);

    await Assert.That(registry.TryGetEnvelope(note)).IsSameReferenceAs(envelope);
  }

  [Test]
  public async Task TryGetEnvelope_RegisteredForAnObjectPayload_ReturnsATypedViewWithTheSameIdentityAsync() {
    var registry = new EnvelopeRegistry();
    var note = new BaseNote("object");
    var envelope = _envelope<object>(note);
    registry.Register(envelope);

    var found = registry.TryGetEnvelope(note);

    await Assert.That(found).IsNotNull();
    await Assert.That(found!.MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(found.Payload).IsSameReferenceAs(note);
  }

  [Test]
  public async Task TryGetEnvelope_RegisteredUnderAnotherType_IsNotFoundAsync() {
    var registry = new EnvelopeRegistry();
    var note = new DerivedNote("base-typed");
    registry.Register(_envelope<BaseNote>(note));

    await Assert.That(registry.TryGetEnvelope(note)).IsNull()
      .Because("an envelope built for the base type is neither the derived type's nor an object payload's");
  }

  [Test]
  public async Task TryGetEnvelope_NotRegisteredOrUnregistered_IsNotFoundAsync() {
    var registry = new EnvelopeRegistry();
    var note = new BaseNote("gone");
    await Assert.That(registry.TryGetEnvelope(note)).IsNull();

    registry.Register(_envelope(note));
    registry.Unregister(note);

    await Assert.That(registry.TryGetEnvelope(note)).IsNull();
  }

  private static MessageEnvelope<T> _envelope<T>(T payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };
}
