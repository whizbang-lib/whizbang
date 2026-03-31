using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="ImmediateInboxBatchStrategy"/> — the no-batching fallback
/// that flushes each message immediately (current behavior).
/// </summary>
/// <tests>src/Whizbang.Core/Workers/ImmediateInboxBatchStrategy.cs</tests>
/// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
public class ImmediateInboxBatchStrategyTests {

  private static InboxMessage _createInboxMessage(Guid? messageId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "TestHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static (IServiceScopeFactory ScopeFactory, ImmediateBatchFakeStrategy Strategy) _createScopeFactory() {
    var strategy = new ImmediateBatchFakeStrategy();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    var sp = services.BuildServiceProvider();
    return (sp.GetRequiredService<IServiceScopeFactory>(), strategy);
  }

  [Test]
  public async Task EnqueueAndWaitAsync_FlushesImmediatelyPerMessageAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    await using var sut = new ImmediateInboxBatchStrategy(scopeFactory);

    // Act
    var msg = _createInboxMessage();
    var result = await sut.EnqueueAndWaitAsync(msg, CancellationToken.None);

    // Assert — one flush per message
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(1);
    await Assert.That(fakeStrategy.QueuedInboxMessages).Count().IsEqualTo(1);
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task EnqueueAndWaitAsync_TwoMessages_TwoSeparateFlushesAsync() {
    // Arrange
    var (scopeFactory, fakeStrategy) = _createScopeFactory();
    await using var sut = new ImmediateInboxBatchStrategy(scopeFactory);

    // Act
    await sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);
    await sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None);

    // Assert — each message gets its own flush
    await Assert.That(fakeStrategy.FlushCallCount).IsEqualTo(2);
    await Assert.That(fakeStrategy.QueuedInboxMessages).Count().IsEqualTo(2);
  }

  [Test]
  public async Task DisposeAsync_IsNoOpAsync() {
    // Arrange
    var (scopeFactory, _) = _createScopeFactory();
    var sut = new ImmediateInboxBatchStrategy(scopeFactory);

    // Act — dispose should not throw
    await sut.DisposeAsync();
    await sut.DisposeAsync(); // idempotent
  }

  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new ImmediateInboxBatchStrategy(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task EnqueueAndWaitAsync_FlushThrows_PropagatesExceptionAsync() {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => new ImmediateBatchThrowingStrategy());
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    await using var sut = new ImmediateInboxBatchStrategy(scopeFactory);

    await Assert.That(async () => await sut.EnqueueAndWaitAsync(_createInboxMessage(), CancellationToken.None))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task EnqueueAndWaitAsync_CancellationToken_PropagatedToFlushAsync() {
    var cancelCheckStrategy = new ImmediateBatchCancelCheckStrategy();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => cancelCheckStrategy);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    await using var sut = new ImmediateInboxBatchStrategy(scopeFactory);
    using var cts = new CancellationTokenSource();

    await sut.EnqueueAndWaitAsync(_createInboxMessage(), cts.Token);

    await Assert.That(cancelCheckStrategy.ReceivedCancellationToken).IsTrue();
  }
}

internal sealed class ImmediateBatchThrowingStrategy : IWorkCoordinatorStrategy {
  public void QueueInboxMessage(InboxMessage message) { }
  public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode, CancellationToken ct)
    => throw new InvalidOperationException("Simulated flush failure");
  public void QueueOutboxMessage(OutboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
}

internal sealed class ImmediateBatchCancelCheckStrategy : IWorkCoordinatorStrategy {
  public bool ReceivedCancellationToken { get; private set; }
  public void QueueInboxMessage(InboxMessage message) { }
  public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode, CancellationToken ct) {
    ReceivedCancellationToken = ct.CanBeCanceled;
    return Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }
  public void QueueOutboxMessage(OutboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
}

/// <summary>
/// Fake strategy for ImmediateInboxBatchStrategy tests.
/// </summary>
internal sealed class ImmediateBatchFakeStrategy : IWorkCoordinatorStrategy {
  private readonly List<InboxMessage> _queuedInbox = [];
  private int _flushCallCount;

  public IReadOnlyList<InboxMessage> QueuedInboxMessages => _queuedInbox;
  public int FlushCallCount => _flushCallCount;

  public void QueueInboxMessage(InboxMessage message) {
    _queuedInbox.Add(message);
  }

  public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
    Interlocked.Increment(ref _flushCallCount);

    var inboxWork = _queuedInbox.Select(msg => new InboxWork {
      MessageId = msg.MessageId,
      Envelope = msg.Envelope,
      MessageType = msg.MessageType,
      StreamId = msg.StreamId,
      Flags = WorkBatchOptions.None
    }).ToList();

    return Task.FromResult(new WorkBatch {
      InboxWork = inboxWork,
      OutboxWork = [],
      PerspectiveWork = []
    });
  }

  public void QueueOutboxMessage(OutboxMessage message) { }
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
}
