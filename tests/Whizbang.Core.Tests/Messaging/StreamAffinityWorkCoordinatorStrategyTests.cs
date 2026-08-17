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

  // ===== audit generation through the wrapper (issue #500) =====
  // The wrapper bypasses the inner strategy for outbox writes, and audit generation
  // (AuditOutboxMessageBuilder via AddOutboxMessage) lived only on the inner path — so
  // wrapping ANY strategy silently killed the audit trail. The wrapper must build the
  // EventAudited message itself and ride it on the same batch.

  [Test]
  public async Task QueueOutboxMessageAsync_AuditedEvent_AppendsEventAuditedToSameBatchAsync() {
    var captured = new List<OutboxMessage>();
    var twoSeen = new TaskCompletionSource();

    await using var batch = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        lock (captured) {
          captured.AddRange(msgs);
          if (captured.Count >= 2) { twoSeen.TrySetResult(); }
        }
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    var inner = new RecordingInnerStrategy();
    var options = new Whizbang.Core.SystemEvents.SystemEventOptions();
    options.EnableEventAudit();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch, options);

    var msg = _eventOutboxMessage(_idProvider.NewGuid());
    await sut.QueueOutboxMessageAsync(msg);

    await twoSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

    List<OutboxMessage> snapshot;
    lock (captured) { snapshot = [.. captured]; }
    await Assert.That(snapshot.Count).IsEqualTo(2)
      .Because("an audited domain event must yield exactly two batched messages: the event itself and its EventAudited companion. The inner strategy is bypassed on this path, so the wrapper must generate the audit — otherwise auditing dies silently for every consumer with outbox batching enabled (issue #500).");
    await Assert.That(snapshot.Any(m => m.MessageId == msg.MessageId)).IsTrue()
      .Because("the domain event still rides the batch.");
    var audit = snapshot.FirstOrDefault(m => m.MessageId != msg.MessageId);
    await Assert.That(audit).IsNotNull();
    await Assert.That(audit!.Destination).IsEqualTo(Whizbang.Core.SystemEvents.AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION)
      .Because("the companion must be the audit relay message bound for the audit topic.");
    await Assert.That(audit.MessageType).Contains("EventAudited");
    await Assert.That(inner.QueueOutboxCallCount).IsEqualTo(0)
      .Because("audit generation must not reintroduce inner-strategy delegation — the per-stream batching win stays intact.");
  }

  [Test]
  public async Task QueueOutboxMessageAsync_AuditDisabled_OnlyDomainMessageBatchedAsync() {
    var captured = new List<OutboxMessage>();
    var flushed = new TaskCompletionSource();

    await using var batch = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        lock (captured) { captured.AddRange(msgs); }
        flushed.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    var inner = new RecordingInnerStrategy();
    // No SystemEventOptions at all — the pre-audit wiring shape. Nothing extra may be batched.
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);

    await sut.QueueOutboxMessageAsync(_eventOutboxMessage(_idProvider.NewGuid()));
    await flushed.Task.WaitAsync(TimeSpan.FromSeconds(2));

    List<OutboxMessage> snapshot;
    lock (captured) { snapshot = [.. captured]; }
    await Assert.That(snapshot.Count).IsEqualTo(1)
      .Because("without EnableEventAudit the wrapper must batch only the domain event — audit generation is strictly opt-in.");
  }

  [Test]
  public async Task QueueOutboxMessageAsync_NonEventMessage_NoAuditCompanionAsync() {
    var captured = new List<OutboxMessage>();
    var flushed = new TaskCompletionSource();

    await using var batch = new SlidingWindowOutboxBatchStrategy(
      flush: (msgs, ct) => {
        lock (captured) { captured.AddRange(msgs); }
        flushed.TrySetResult();
        return Task.CompletedTask;
      },
      options: new SlidingWindowOutboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(30),
        MaxWait = TimeSpan.FromMilliseconds(200),
        MaxSize = 100,
      });

    var inner = new RecordingInnerStrategy();
    var options = new Whizbang.Core.SystemEvents.SystemEventOptions();
    options.EnableEventAudit();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch, options);

    // IsEvent = false (commands, non-event messages) — never audited, mirroring AddOutboxMessage.
    await sut.QueueOutboxMessageAsync(_outboxMessage(_idProvider.NewGuid()));
    await flushed.Task.WaitAsync(TimeSpan.FromSeconds(2));

    List<OutboxMessage> snapshot;
    lock (captured) { snapshot = [.. captured]; }
    await Assert.That(snapshot.Count).IsEqualTo(1)
      .Because("only events are audited; non-event messages must not grow an audit companion.");
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

  private OutboxMessage _eventOutboxMessage(Guid streamId) {
    var msg = _outboxMessage(streamId);
    return msg with { IsEvent = true };
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
