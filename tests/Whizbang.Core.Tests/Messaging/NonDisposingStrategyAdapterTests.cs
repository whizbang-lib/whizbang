using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the delegate-everything-but-DisposeAsync contract on
/// <see cref="NonDisposingStrategyAdapter"/>. The class wraps a singleton
/// <see cref="IWorkCoordinatorStrategy"/> so scope disposal can't tear down
/// the shared instance — every method must forward to the inner, and
/// <see cref="System.IAsyncDisposable.DisposeAsync"/> must be a no-op so the
/// singleton survives the scope's lifetime.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
public class NonDisposingStrategyAdapterTests {

  private static MessageEnvelope<JsonElement> _envelope(Guid id) => new() {
    MessageId = MessageId.From(id),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static OutboxMessage _outbox(Guid? messageId = null, string destination = "test-topic") {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = destination,
      Envelope = _envelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
    };
  }

  private static InboxMessage _inbox(Guid? messageId = null, string handlerName = "TestHandler") {
    var id = messageId ?? Guid.CreateVersion7();
    return new InboxMessage {
      MessageId = id,
      HandlerName = handlerName,
      Envelope = _envelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
    };
  }

  [Test]
  public async Task QueueOutboxMessage_DelegatesToInnerAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);
    var msg = _outbox();

    adapter.QueueOutboxMessage(msg);

    await Assert.That(inner.QueuedOutbox.Count).IsEqualTo(1);
    await Assert.That(inner.QueuedOutbox[0]).IsSameReferenceAs(msg);
  }

  [Test]
  public async Task QueueInboxMessage_DelegatesToInnerAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);
    var msg = _inbox();

    adapter.QueueInboxMessage(msg);

    await Assert.That(inner.QueuedInbox.Count).IsEqualTo(1);
    await Assert.That(inner.QueuedInbox[0]).IsSameReferenceAs(msg);
  }

  [Test]
  public async Task QueueOutboxCompletion_DelegatesToInnerAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);
    var id = Guid.NewGuid();

    adapter.QueueOutboxCompletion(id, MessageProcessingStatus.Stored);

    await Assert.That(inner.OutboxCompletions.Count).IsEqualTo(1);
    await Assert.That(inner.OutboxCompletions[0].MessageId).IsEqualTo(id);
    await Assert.That(inner.OutboxCompletions[0].Status).IsEqualTo(MessageProcessingStatus.Stored);
  }

  [Test]
  public async Task QueueInboxCompletion_DelegatesToInnerAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);
    var id = Guid.NewGuid();

    adapter.QueueInboxCompletion(id, MessageProcessingStatus.Stored);

    await Assert.That(inner.InboxCompletions.Count).IsEqualTo(1);
    await Assert.That(inner.InboxCompletions[0].MessageId).IsEqualTo(id);
  }

  [Test]
  public async Task QueueOutboxFailure_DelegatesToInnerAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);
    var id = Guid.NewGuid();

    adapter.QueueOutboxFailure(id, MessageProcessingStatus.None, "boom");

    await Assert.That(inner.OutboxFailures.Count).IsEqualTo(1);
    await Assert.That(inner.OutboxFailures[0].MessageId).IsEqualTo(id);
    await Assert.That(inner.OutboxFailures[0].Error).IsEqualTo("boom");
  }

  [Test]
  public async Task QueueInboxFailure_DelegatesToInnerAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);
    var id = Guid.NewGuid();

    adapter.QueueInboxFailure(id, MessageProcessingStatus.None, "boom");

    await Assert.That(inner.InboxFailures.Count).IsEqualTo(1);
    await Assert.That(inner.InboxFailures[0].MessageId).IsEqualTo(id);
    await Assert.That(inner.InboxFailures[0].Error).IsEqualTo("boom");
  }

  [Test]
  public async Task FlushAsync_DelegatesAndReturnsInnerResultAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);

    await adapter.FlushAsync(WorkBatchOptions.None);

    await Assert.That(inner.FlushAsyncCalls).IsEqualTo(1);
    await Assert.That(inner.LastFlushFlags).IsEqualTo(WorkBatchOptions.None);
  }

  [Test]
  public async Task FlushAndGetBatchAsync_DelegatesAndReturnsInnerBatchAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);

    var batch = await adapter.FlushAndGetBatchAsync(WorkBatchOptions.SkipInboxClaiming);

    await Assert.That(inner.FlushAndGetBatchCalls).IsEqualTo(1);
    await Assert.That(inner.LastFlushFlags).IsEqualTo(WorkBatchOptions.SkipInboxClaiming);
    await Assert.That(batch).IsSameReferenceAs(inner.BatchToReturn);
  }

  /// <summary>
  /// The IWorkFlusher.FlushAsync explicit-interface impl routes through
  /// FlushAndGetBatchAsync with SkipInboxClaiming — the inner is what
  /// receives the call, the adapter ignores the returned batch.
  /// </summary>
  [Test]
  public async Task IWorkFlusher_FlushAsync_RoutesThroughInnerFlushAndGetBatchAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);
    IWorkFlusher flusher = adapter;

    await flusher.FlushAsync(CancellationToken.None);

    await Assert.That(inner.FlushAndGetBatchCalls).IsEqualTo(1);
    await Assert.That(inner.LastFlushFlags).IsEqualTo(WorkBatchOptions.SkipInboxClaiming);
  }

  [Test]
  public async Task DisposeAsync_DoesNotDisposeInner_ReturnsCompletedAsync() {
    var inner = new _RecordingStrategy();
    var adapter = new NonDisposingStrategyAdapter(inner);

    await adapter.DisposeAsync();

    // The contract: scope disposal must NOT propagate to the singleton inner.
    await Assert.That(inner.DisposeCalled).IsFalse();
  }

  /// <summary>
  /// Stub <see cref="IWorkCoordinatorStrategy"/> that records every call so
  /// adapter forwarding can be asserted.
  /// </summary>
  private sealed class _RecordingStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutbox { get; } = [];
    public List<InboxMessage> QueuedInbox { get; } = [];
    public List<(Guid MessageId, MessageProcessingStatus Status)> OutboxCompletions { get; } = [];
    public List<(Guid MessageId, MessageProcessingStatus Status)> InboxCompletions { get; } = [];
    public List<(Guid MessageId, MessageProcessingStatus Status, string Error)> OutboxFailures { get; } = [];
    public List<(Guid MessageId, MessageProcessingStatus Status, string Error)> InboxFailures { get; } = [];
    public int FlushAsyncCalls { get; private set; }
    public int FlushAndGetBatchCalls { get; private set; }
    public WorkBatchOptions LastFlushFlags { get; private set; }
    public bool DisposeCalled { get; private set; }
    public WorkBatch BatchToReturn { get; } = new() {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
    };

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutbox.Add(message);
    public void QueueInboxMessage(InboxMessage message) => QueuedInbox.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) =>
      OutboxCompletions.Add((messageId, completedStatus));
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) =>
      InboxCompletions.Add((messageId, completedStatus));
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      OutboxFailures.Add((messageId, completedStatus, errorMessage));
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      InboxFailures.Add((messageId, completedStatus, errorMessage));

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushAsyncCalls++;
      LastFlushFlags = flags;
      return Task.CompletedTask;
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushAndGetBatchCalls++;
      LastFlushFlags = flags;
      return Task.FromResult(BatchToReturn);
    }
  }
}
