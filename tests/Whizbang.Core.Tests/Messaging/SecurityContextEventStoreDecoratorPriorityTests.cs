using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Priority step 1 at an append-time wrap: the security decorator turns a raw message into the envelope the inner
/// store keeps, so it is the last place a number can be declared for an event appended outside the dispatcher. A
/// raw append made while handling other work carries that work's number (the ambient parent); one made outside any
/// handling stays undeclared; an envelope the caller already built keeps its own number. The in-memory store keeps
/// the envelope object it is handed, so reading the stream back reads exactly what the decorator built.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Core/Messaging/SecurityContextEventStoreDecorator.cs</code-under-test>
public sealed class SecurityContextEventStoreDecoratorPriorityTests {
  private sealed record DecoratorPriorityProbe(string Value) : IEvent;

  private static async Task<MessageEnvelope<DecoratorPriorityProbe>> _firstAsync(InMemoryEventStore inner, Guid streamId) {
    await foreach (var envelope in inner.ReadAsync<DecoratorPriorityProbe>(streamId, 0)) {
      return envelope;
    }
    throw new InvalidOperationException("Test setup: the stream is empty.");
  }

  [Test]
  public async Task AppendAsync_WithMessage_WhileHandlingBackgroundWork_TheEnvelopeCarriesTheAmbientParentAsync() {
    var inner = new InMemoryEventStore();
    var decorator = new SecurityContextEventStoreDecorator(inner);
    var streamId = (Guid)TrackedGuid.NewMedo();

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await decorator.AppendAsync(streamId, new DecoratorPriorityProbe("a"));
    }

    var stored = await _firstAsync(inner, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the decorator builds the envelope the store keeps; an append made while handling background work inherits it, the same rule the dispatcher applies");
  }

  [Test]
  public async Task AppendAsync_WithMessage_OutsideAnyHandling_TheEnvelopeStaysUndeclaredAsync() {
    var inner = new InMemoryEventStore();
    var decorator = new SecurityContextEventStoreDecorator(inner);
    var streamId = (Guid)TrackedGuid.NewMedo();

    await decorator.AppendAsync(streamId, new DecoratorPriorityProbe("b"));

    var stored = await _firstAsync(inner, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("nothing to inherit means nobody said; a wrap must not invent a band the consumer's rules would otherwise decide");
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_KeepsTheEnvelopesOwnNumber_WhateverTheHandlingAsync() {
    var inner = new InMemoryEventStore();
    var decorator = new SecurityContextEventStoreDecorator(inner);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<DecoratorPriorityProbe> {
      MessageId = MessageId.New(),
      Payload = new DecoratorPriorityProbe("c"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Priority = WorkPriority.INTERACTIVE,
    };

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await decorator.AppendAsync(streamId, envelope);
    }

    var stored = await _firstAsync(inner, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the ambient parent fills a blank; it never overrides a number the caller declared on an envelope it built itself");
  }
}
