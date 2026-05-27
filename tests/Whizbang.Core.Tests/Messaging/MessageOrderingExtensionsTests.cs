using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for the ordering invariant — components handling more than one message MUST sort by
/// MessageId (UUIDv7 = chronological) before processing. Locks the canonical
/// <see cref="MessageOrderingExtensions.OrderByMessageId{T}(System.Collections.Generic.IEnumerable{T})"/>
/// behavior so a future refactor can't silently regress it.
/// </summary>
public class MessageOrderingExtensionsTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // ===== IMessageEnvelope overload =====

  [Test]
  public async Task OrderByMessageId_OnEnvelopes_ShuffledInput_ReturnsByMessageIdAscAsync() {
    var e1 = _envelope(_idProvider.NewGuid());
    var e2 = _envelope(_idProvider.NewGuid());
    var e3 = _envelope(_idProvider.NewGuid());
    var shuffled = new[] { e3, e1, e2 };

    var ordered = shuffled.OrderByMessageId().ToList();

    await Assert.That(ordered.Count).IsEqualTo(3);
    await Assert.That(ordered[0].MessageId).IsEqualTo(e1.MessageId);
    await Assert.That(ordered[1].MessageId).IsEqualTo(e2.MessageId);
    await Assert.That(ordered[2].MessageId).IsEqualTo(e3.MessageId);
  }

  [Test]
  public async Task OrderByMessageId_OnEnvelopes_AlreadySorted_PreservesOrderAsync() {
    var e1 = _envelope(_idProvider.NewGuid());
    var e2 = _envelope(_idProvider.NewGuid());
    var sorted = new[] { e1, e2 };

    var result = sorted.OrderByMessageId().ToList();

    await Assert.That(result[0].MessageId).IsEqualTo(e1.MessageId);
    await Assert.That(result[1].MessageId).IsEqualTo(e2.MessageId);
  }

  [Test]
  public async Task OrderByMessageId_OnEnvelopes_EmptyInput_ReturnsEmptyAsync() {
    var empty = Array.Empty<MessageEnvelope<JsonElement>>();

    var result = empty.OrderByMessageId().ToList();

    await Assert.That(result).IsEmpty();
  }

  // ===== IHasMessageIdAndStatus overload =====

  [Test]
  public async Task OrderByMessageId_OnHasMessageIdAndStatus_ShuffledInput_ReturnsByMessageIdAscAsync() {
    var streamId = _idProvider.NewGuid();
    var w1 = _inboxWork(streamId);
    var w2 = _inboxWork(streamId);
    var w3 = _inboxWork(streamId);
    var shuffled = new[] { w3, w1, w2 };

    var ordered = shuffled.OrderByMessageId().ToList();

    await Assert.That(ordered.Count).IsEqualTo(3);
    await Assert.That(ordered[0].MessageId).IsEqualTo(w1.MessageId);
    await Assert.That(ordered[1].MessageId).IsEqualTo(w2.MessageId);
    await Assert.That(ordered[2].MessageId).IsEqualTo(w3.MessageId);
  }

  [Test]
  public async Task OrderByMessageId_OnOutboxWork_ShuffledInput_ReturnsByMessageIdAscAsync() {
    var streamId = _idProvider.NewGuid();
    var o1 = _outboxWork(streamId);
    var o2 = _outboxWork(streamId);
    var o3 = _outboxWork(streamId);
    var shuffled = new[] { o2, o3, o1 };

    var ordered = shuffled.OrderByMessageId().ToList();

    await Assert.That(ordered[0].MessageId).IsEqualTo(o1.MessageId);
    await Assert.That(ordered[1].MessageId).IsEqualTo(o2.MessageId);
    await Assert.That(ordered[2].MessageId).IsEqualTo(o3.MessageId);
  }

  [Test]
  public async Task OrderByMessageId_OnHasMessageIdAndStatus_EmptyInput_ReturnsEmptyAsync() {
    var empty = Array.Empty<InboxWork>();

    var result = empty.OrderByMessageId().ToList();

    await Assert.That(result).IsEmpty();
  }

  // ===== OutboxBatchRow / InboxBatchRow overloads =====

  [Test]
  public async Task OrderByMessageId_OnOutboxBatchRow_ShuffledInput_ReturnsByMessageIdAscAsync() {
    var streamId = _idProvider.NewGuid();
    var r1 = _outboxBatchRow(streamId);
    var r2 = _outboxBatchRow(streamId);
    var r3 = _outboxBatchRow(streamId);
    var shuffled = new[] { r3, r1, r2 };

    var ordered = shuffled.OrderByMessageId().ToList();

    await Assert.That(ordered[0].MessageId).IsEqualTo(r1.MessageId);
    await Assert.That(ordered[1].MessageId).IsEqualTo(r2.MessageId);
    await Assert.That(ordered[2].MessageId).IsEqualTo(r3.MessageId);
  }

  [Test]
  public async Task OrderByMessageId_OnInboxBatchRow_ShuffledInput_ReturnsByMessageIdAscAsync() {
    var streamId = _idProvider.NewGuid();
    var r1 = _inboxBatchRow(streamId);
    var r2 = _inboxBatchRow(streamId);
    var r3 = _inboxBatchRow(streamId);
    var shuffled = new[] { r2, r3, r1 };

    var ordered = shuffled.OrderByMessageId().ToList();

    await Assert.That(ordered[0].MessageId).IsEqualTo(r1.MessageId);
    await Assert.That(ordered[1].MessageId).IsEqualTo(r2.MessageId);
    await Assert.That(ordered[2].MessageId).IsEqualTo(r3.MessageId);
  }

  private OutboxBatchRow _outboxBatchRow(Guid streamId) => new() {
    MessageId = _idProvider.NewGuid(),
    StreamId = streamId,
    Destination = "test-topic",
    MessageType = "TestMessage",
    EventData = "{}",
    Metadata = "{}",
  };

  private InboxBatchRow _inboxBatchRow(Guid streamId) => new() {
    MessageId = _idProvider.NewGuid(),
    StreamId = streamId,
    HandlerName = "TestHandler",
    MessageType = "TestMessage",
    EventData = "{}",
    Metadata = "{}",
  };

  // ===== Helpers =====

  private static MessageEnvelope<JsonElement> _envelope(Guid messageId) =>
    new(MessageId.From(messageId), JsonDocument.Parse("{}").RootElement, []);

  private InboxWork _inboxWork(Guid streamId) {
    var messageId = _idProvider.NewGuid();
    return new InboxWork {
      MessageId = messageId,
      Envelope = _envelope(messageId),
      MessageType = "Whizbang.Core.Tests.Messaging.TestMessage, Whizbang.Core.Tests",
      StreamId = streamId,
      PartitionNumber = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

  private OutboxWork _outboxWork(Guid streamId) {
    var messageId = _idProvider.NewGuid();
    return new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = _envelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      PartitionNumber = 0,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

}
