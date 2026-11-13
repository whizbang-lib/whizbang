// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The decorators that put every outbox and inbox batch strategy behind the batch buffer (#2220): an append takes room
/// sized by its payload before the inner strategy sees it, gives it back if the inner strategy refuses it, and stopping
/// the strategy stops the buffer.
/// </summary>
public class BoundedBatchStrategyTests {
  private const string PAYLOAD = """{"orderId":"0193f5a2","lines":[1,2,3]}""";

  private static BatchBuffer _buffer(int messages) => new(
    BatchBufferLimits.Resolve(new BatchBufferOptions { MaxBufferedMessages = messages, MaxBufferedBytes = 0 }, 1_000_000, 0, 0),
    new FakeLogger<BatchBuffer>(), null, new FakeTimeProvider());

  internal static OutboxMessage Outbox(string payload = PAYLOAD) {
    var id = (Guid)TrackedGuid.New();
    return new OutboxMessage {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonElement.Parse(payload), []),
      EnvelopeType = "Envelope",
      MessageType = "Orders.OrderPlaced",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
    };
  }

  internal static InboxMessage Inbox() {
    var id = (Guid)TrackedGuid.New();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "Handler",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.From(id), JsonElement.Parse(PAYLOAD), []),
      EnvelopeType = "Envelope",
      MessageType = "Orders.OrderPlaced",
    };
  }

  private sealed class RecordingOutbox(Exception? refuse = null) : IOutboxBatchStrategy {
    public List<OutboxMessage> Appended { get; } = [];
    public int Stops { get; private set; }
    public int Disposals { get; private set; }

    public ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
      if (refuse is not null) {
        throw refuse;
      }
      Appended.Add(message);
      return ValueTask.CompletedTask;
    }

    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
      Stops++;
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
      Disposals++;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingInbox(Exception? refuse = null) : IInboxBatchStrategy {
    public List<InboxMessage> Appended { get; } = [];
    public int Stops { get; private set; }
    public int Disposals { get; private set; }

    public ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default) {
      if (refuse is not null) {
        throw refuse;
      }
      Appended.Add(message);
      return ValueTask.CompletedTask;
    }

    public Task FlushAndStopAsync(CancellationToken cancellationToken = default) {
      Stops++;
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
      Disposals++;
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task AnOutboxAppend_TakesRoomSizedByItsPayload_AndReachesTheInnerStrategyAsync() {
    var buffer = _buffer(10);
    var inner = new RecordingOutbox();
    var bounded = new BoundedOutboxBatchStrategy(inner, buffer);
    var message = Outbox();

    await bounded.AppendAsync(message);

    await Assert.That(inner.Appended).IsEquivalentTo([message]);
    await Assert.That(bounded.Inner).IsSameReferenceAs(inner);
    await Assert.That(buffer.BufferedBytes).IsEqualTo(System.Text.Encoding.UTF8.GetByteCount(PAYLOAD));
    buffer.Release([message]);
    await Assert.That(buffer.BufferedMessages).IsEqualTo(0);
  }

  [Test]
  public async Task AnOutboxAppend_WaitsWhileTheBufferIsFull_AndPassesItsContextAsync() {
    var buffer = _buffer(1);
    var bounded = new BoundedOutboxBatchStrategy(new RecordingOutbox(), buffer);
    var first = Outbox();
    await bounded.AppendAsync(first);
    var asked = false;

    var second = bounded.AppendAsync(Outbox(), new BatchAppendContext("Publishing", () => { asked = true; return false; })).AsTask();

    await Assert.That(second.IsCompleted).IsFalse();
    await Assert.That(asked).IsTrue();
    buffer.Release([first]);
    await second;
  }

  [Test]
  public async Task AnInteractiveAppend_UsesTheReserveAsync() {
    var buffer = new BatchBuffer(
      BatchBufferLimits.Resolve(new BatchBufferOptions { MaxBufferedMessages = 10, MaxBufferedBytes = 0 }, 1_000_000, 10, 5),
      new FakeLogger<BatchBuffer>(), null, new FakeTimeProvider());
    var bounded = new BoundedOutboxBatchStrategy(new RecordingOutbox(), buffer);
    for (var i = 0; i < 5; i++) {
      await bounded.AppendAsync(Outbox());
    }

    var standard = _appendAsTask(bounded);
    Task interactive;
    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      interactive = _appendAsTask(bounded);
    }

    await Assert.That(standard.IsCompleted).IsFalse();
    await Assert.That(interactive.IsCompletedSuccessfully).IsTrue();
  }

  private static Task _appendAsTask(BoundedOutboxBatchStrategy bounded) => bounded.AppendAsync(Outbox()).AsTask();

  [Test]
  public async Task AnOutboxAppendTheInnerStrategyRefuses_GivesItsRoomBackAsync() {
    var buffer = _buffer(10);
    var bounded = new BoundedOutboxBatchStrategy(new RecordingOutbox(new ObjectDisposedException("inner")), buffer);

    await Assert.That(async () => await bounded.AppendAsync(Outbox())).Throws<ObjectDisposedException>();

    await Assert.That(buffer.BufferedMessages).IsEqualTo(0);
  }

  [Test]
  public async Task StoppingOrDisposingTheOutboxStrategy_StopsTheBuffer_AndTheInnerStrategyAsync() {
    var buffer = _buffer(1);
    var inner = new RecordingOutbox();
    var bounded = new BoundedOutboxBatchStrategy(inner, buffer);
    await bounded.AppendAsync(Outbox());
    var waiting = bounded.AppendAsync(Outbox()).AsTask();

    await bounded.FlushAndStopAsync();
    await bounded.DisposeAsync();

    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await Assert.That(inner.Stops).IsEqualTo(1);
    await Assert.That(inner.Disposals).IsEqualTo(1);
  }

  [Test]
  public async Task AnInboxAppend_TakesRoom_WaitsWhenFull_AndGivesRoomBackWhenRefusedAsync() {
    var buffer = _buffer(1);
    var inner = new RecordingInbox();
    var bounded = new BoundedInboxBatchStrategy(inner, buffer);
    var first = Inbox();
    await bounded.AppendAsync(first);

    var second = bounded.AppendAsync(Inbox()).AsTask();

    await Assert.That(bounded.Inner).IsSameReferenceAs(inner);
    await Assert.That(second.IsCompleted).IsFalse();
    buffer.Release([first]);
    await second;
    await Assert.That(inner.Appended.Count).IsEqualTo(2);

    var refusing = new BoundedInboxBatchStrategy(new RecordingInbox(new InvalidOperationException("no")), _buffer(1));
    await Assert.That(async () => await refusing.AppendAsync(Inbox())).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task StoppingOrDisposingTheInboxStrategy_StopsTheBuffer_AndTheInnerStrategyAsync() {
    var buffer = _buffer(1);
    var inner = new RecordingInbox();
    var bounded = new BoundedInboxBatchStrategy(inner, buffer);
    await bounded.AppendAsync(Inbox());
    var waiting = bounded.AppendAsync(Inbox()).AsTask();

    await bounded.FlushAndStopAsync();
    await bounded.DisposeAsync();

    await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
    await Assert.That(inner.Stops).IsEqualTo(1);
    await Assert.That(inner.Disposals).IsEqualTo(1);
  }

  [Test]
  public async Task AMessageWithNoPayload_TakesNoBytesAsync() {
    var envelope = new MessageEnvelope<JsonElement>(MessageId.New(), default, []);

    await Assert.That(BatchMessageSize.Of(envelope)).IsEqualTo(0);
  }

  [Test]
  public async Task TheDecorators_RefuseMissingArgumentsAsync() {
    var buffer = _buffer(1);
    await Assert.That(() => new BoundedOutboxBatchStrategy(null!, buffer)).Throws<ArgumentNullException>();
    await Assert.That(() => new BoundedOutboxBatchStrategy(new RecordingOutbox(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new BoundedInboxBatchStrategy(null!, buffer)).Throws<ArgumentNullException>();
    await Assert.That(() => new BoundedInboxBatchStrategy(new RecordingInbox(), null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new BoundedOutboxBatchStrategy(new RecordingOutbox(), buffer).AppendAsync(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new BoundedInboxBatchStrategy(new RecordingInbox(), buffer).AppendAsync(null!)).Throws<ArgumentNullException>();
  }
}
