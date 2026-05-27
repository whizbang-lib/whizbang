using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the contract of <see cref="StreamAffinityWorkCoordinatorStrategy"/> — slice 10 of
/// pump-then-process Half B. The strategy decorates an inner <see cref="IWorkCoordinatorStrategy"/>
/// for inbox/completion/failure flow but routes ALL outbox writes through an
/// <see cref="IOutboxBatchStrategy"/> for per-stream sliding-window batching.
/// </summary>
public class StreamAffinityWorkCoordinatorStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // ===== async path: outbox routes through IOutboxBatchStrategy =====

  [Test]
  public async Task QueueOutboxMessageAsync_RoutesThroughOutboxBatchStrategyAsync() {
    var captured = new List<OutboxMessage[]>();
    var flushed = new TaskCompletionSource();
    var streamId = _idProvider.NewGuid();

    await using var batch = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushed.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    var inner = new RecordingInnerStrategy();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);

    var msg = _outboxMessage(streamId);
    await sut.QueueOutboxMessageAsync(msg);

    await flushed.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // Outbox was routed through the batcher, NOT the inner strategy
    await Assert.That(inner.QueueOutboxCallCount).IsEqualTo(0);
    await Assert.That(captured.Count).IsEqualTo(1);
    await Assert.That(captured[0][0].MessageId).IsEqualTo(msg.MessageId);
  }

  // ===== sync path: throws (forces callers to migrate to async) =====

  [Test]
  public async Task QueueOutboxMessage_SyncCall_ThrowsAsync() {
    await using var batch = new SlidingWindowOutboxBatchStrategy(flush: (_, _) => Task.CompletedTask);
    var inner = new RecordingInnerStrategy();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);

    var msg = _outboxMessage(_idProvider.NewGuid());

    // Sync call must fail loud — the strategy is async-first by design. Callers MUST migrate
    // to QueueOutboxMessageAsync. The default-implemented interface method on
    // IWorkCoordinatorStrategy delegates async → sync; this strategy reverses the relationship
    // and fails the sync entry to surface migration gaps.
    await Assert.That(() => sut.QueueOutboxMessage(msg)).Throws<InvalidOperationException>();
  }

  // ===== inbox / completion / failure delegate to inner =====

  [Test]
  public async Task QueueInboxMessage_DelegatesToInnerStrategyAsync() {
    await using var batch = new SlidingWindowOutboxBatchStrategy(flush: (_, _) => Task.CompletedTask);
    var inner = new RecordingInnerStrategy();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);

    sut.QueueInboxMessage(_inboxMessage(_idProvider.NewGuid()));
    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Stored);
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "test");
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "test");

    await Assert.That(inner.QueueInboxCallCount).IsEqualTo(1);
    await Assert.That(inner.QueueInboxCompletionCount).IsEqualTo(1);
    await Assert.That(inner.QueueInboxFailureCount).IsEqualTo(1);
    await Assert.That(inner.QueueOutboxCompletionCount).IsEqualTo(1);
    await Assert.That(inner.QueueOutboxFailureCount).IsEqualTo(1);
  }

  // ===== same-stream batching: many outbox emits → single batch =====

  [Test]
  public async Task QueueOutboxMessageAsync_ManySameStreamEmits_SingleSortedBatchAsync() {
    var captured = new List<OutboxMessage[]>();
    var flushed = new TaskCompletionSource();
    var streamId = _idProvider.NewGuid();

    await using var batch = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        captured.Add(msgs);
        flushed.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    var inner = new RecordingInnerStrategy();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);

    var m1 = _outboxMessage(streamId);
    var m2 = _outboxMessage(streamId);
    var m3 = _outboxMessage(streamId);

    // Emit out-of-order; verify the batch arrives ordered
    await sut.QueueOutboxMessageAsync(m3);
    await sut.QueueOutboxMessageAsync(m1);
    await sut.QueueOutboxMessageAsync(m2);

    await flushed.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(captured.Count).IsEqualTo(1);
    var arr = captured[0];
    await Assert.That(arr.Length).IsEqualTo(3);
    await Assert.That(arr[0].MessageId).IsEqualTo(m1.MessageId);
    await Assert.That(arr[1].MessageId).IsEqualTo(m2.MessageId);
    await Assert.That(arr[2].MessageId).IsEqualTo(m3.MessageId);
  }

  // ===== helpers =====

  private OutboxMessage _outboxMessage(Guid streamId) {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new OutboxMessage {
      MessageId = messageId,
      StreamId = streamId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = [],
      },
    };
  }

  private InboxMessage _inboxMessage(Guid streamId) {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      StreamId = streamId,
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      HandlerName = "test",
    };
  }

  /// <summary>
  /// Test double recording calls to the IWorkCoordinatorStrategy methods that the
  /// stream-affinity strategy delegates through (everything except outbox queuing).
  /// </summary>
  private sealed class RecordingInnerStrategy : IWorkCoordinatorStrategy {
    public int QueueOutboxCallCount;
    public int QueueInboxCallCount;
    public int QueueInboxCompletionCount;
    public int QueueInboxFailureCount;
    public int QueueOutboxCompletionCount;
    public int QueueOutboxFailureCount;

    public void QueueOutboxMessage(OutboxMessage message) {
      Interlocked.Increment(ref QueueOutboxCallCount);
    }
    public void QueueInboxMessage(InboxMessage message) {
      Interlocked.Increment(ref QueueInboxCallCount);
    }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
      Interlocked.Increment(ref QueueOutboxCompletionCount);
    }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
      Interlocked.Increment(ref QueueInboxCompletionCount);
    }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
      Interlocked.Increment(ref QueueOutboxFailureCount);
    }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
      Interlocked.Increment(ref QueueInboxFailureCount);
    }
    public Task FlushAsync(WorkBatchOptions options = WorkBatchOptions.None, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions options = WorkBatchOptions.None, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }
}
