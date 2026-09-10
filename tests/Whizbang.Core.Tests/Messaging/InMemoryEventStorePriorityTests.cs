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
/// Priority step 1 at an append-time wrap: the in-memory store wraps a raw message into the envelope it keeps and
/// later hands back to every reader, so a raw append made while handling other work carries that work's number
/// (the ambient parent), one made outside any handling stays undeclared, and an envelope appended as such keeps
/// its own number.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Core/Messaging/InMemoryEventStore.cs</code-under-test>
public sealed class InMemoryEventStorePriorityTests {
  private sealed record StorePriorityProbe(string Value) : IEvent;

  private static async Task<MessageEnvelope<StorePriorityProbe>> _firstAsync(InMemoryEventStore store, Guid streamId) {
    await foreach (var envelope in store.ReadAsync<StorePriorityProbe>(streamId, 0)) {
      return envelope;
    }
    throw new InvalidOperationException("Test setup: the stream is empty.");
  }

  [Test]
  public async Task AppendAsync_WithMessage_WhileHandlingBackgroundWork_TheStoredEnvelopeCarriesTheAmbientParentAsync() {
    var store = new InMemoryEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await store.AppendAsync(streamId, new StorePriorityProbe("a"));
    }

    var stored = await _firstAsync(store, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the wrap is the only place this envelope is built; every reader of the stream sees the number it declared");
  }

  [Test]
  public async Task AppendAsync_WithMessage_OutsideAnyHandling_TheStoredEnvelopeStaysUndeclaredAsync() {
    var store = new InMemoryEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await store.AppendAsync(streamId, new StorePriorityProbe("b"));

    var stored = await _firstAsync(store, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("nothing to inherit means nobody said; the store must not invent a band");
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_KeepsTheEnvelopesOwnNumberAsync() {
    var store = new InMemoryEventStore();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var envelope = new MessageEnvelope<StorePriorityProbe> {
      MessageId = MessageId.New(),
      Payload = new StorePriorityProbe("c"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Priority = WorkPriority.INTERACTIVE,
    };

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await store.AppendAsync(streamId, envelope);
    }

    var stored = await _firstAsync(store, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("an envelope the caller built is stored as built; the ambient parent only fills a blank on a raw append");
  }
}
