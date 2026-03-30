using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for dispatcher's deferred publishing behavior.
/// When no IWorkCoordinatorStrategy is available, events should be queued to IDeferredOutboxChannel.
/// </summary>
/// <docs>core-concepts/dispatcher#deferred-publishing</docs>
public class DeferredDispatchTests {
  // Test event for deferred dispatch
  public record TestDeferredEvent([property: StreamId] Guid Id) : IEvent;

  [Test]
  public async Task PublishAsync_NoStrategy_QueuesToDeferredChannelAsync() {
    // Arrange: No IWorkCoordinatorStrategy registered, but IDeferredOutboxChannel IS registered
    var deferredChannel = new DeferredOutboxChannel();
    var dispatcher = _createDispatcherWithDeferredChannel(deferredChannel);
    var @event = new TestDeferredEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert: Event should be queued to deferred channel
    await Assert.That(deferredChannel.HasPending).IsTrue();
    var drained = deferredChannel.DrainAll();
    await Assert.That(drained).Count().IsEqualTo(1);
    await Assert.That(drained[0].MessageType).Contains("TestDeferredEvent");
  }

  [Test]
  public async Task PublishAsync_WithActiveStrategy_ProcessesImmediately_NoDeferralAsync() {
    // Arrange: IWorkCoordinatorStrategy IS registered
    var deferredChannel = new DeferredOutboxChannel();
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithBoth(strategy, deferredChannel);
    var @event = new TestDeferredEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert: Event should NOT be in deferred channel
    await Assert.That(deferredChannel.HasPending).IsFalse();
    // Event should be processed through strategy
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].MessageType).Contains("TestDeferredEvent");
  }

  [Test]
  public async Task PublishAsync_NoStrategy_MultipleCalls_AllQueuedToDeferredChannelAsync() {
    // Arrange
    var deferredChannel = new DeferredOutboxChannel();
    var dispatcher = _createDispatcherWithDeferredChannel(deferredChannel);

    // Act - publish multiple events
    await dispatcher.PublishAsync(new TestDeferredEvent(Guid.NewGuid()));
    await dispatcher.PublishAsync(new TestDeferredEvent(Guid.NewGuid()));
    await dispatcher.PublishAsync(new TestDeferredEvent(Guid.NewGuid()));

    // Assert: All events should be queued
    await Assert.That(deferredChannel.HasPending).IsTrue();
    var drained = deferredChannel.DrainAll();
    await Assert.That(drained).Count().IsEqualTo(3);
  }

  [Test]
  public async Task PublishAsync_NoStrategy_PreservesMessageIdAsync() {
    // Arrange
    var deferredChannel = new DeferredOutboxChannel();
    var dispatcher = _createDispatcherWithDeferredChannel(deferredChannel);
    var eventId = Guid.NewGuid();
    var @event = new TestDeferredEvent(eventId);

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert: Message should have a valid message ID
    var drained = deferredChannel.DrainAll();
    await Assert.That(drained).Count().IsEqualTo(1);
    await Assert.That(drained[0].MessageId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task PublishAsync_NoStrategy_SetsIsEventTrueAsync() {
    // Arrange
    var deferredChannel = new DeferredOutboxChannel();
    var dispatcher = _createDispatcherWithDeferredChannel(deferredChannel);
    var @event = new TestDeferredEvent(Guid.NewGuid());

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert: IsEvent should be true
    var drained = deferredChannel.DrainAll();
    await Assert.That(drained).Count().IsEqualTo(1);
    await Assert.That(drained[0].IsEvent).IsTrue();
  }

  // ========================================
  // STUB IMPLEMENTATIONS
  // ========================================

  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public List<InboxMessage> QueuedInboxMessages { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) => QueuedInboxMessages.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!
      );
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) {
      throw new NotImplementedException("Not needed for deferred dispatch tests");
    }
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  /// <summary>
  /// Creates a dispatcher WITH IDeferredOutboxChannel but WITHOUT IWorkCoordinatorStrategy.
  /// This simulates the PostPerspective scenario where no transaction context exists.
  /// </summary>
  private static IDispatcher _createDispatcherWithDeferredChannel(IDeferredOutboxChannel deferredChannel) {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();

    // Register deferred channel (simulating singleton registration)
    services.AddSingleton(deferredChannel);

    // NO IWorkCoordinatorStrategy registered - this triggers deferred behavior
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  /// <summary>
  /// Creates a dispatcher with BOTH IDeferredOutboxChannel AND IWorkCoordinatorStrategy.
  /// This simulates the normal scenario where a transaction context exists.
  /// </summary>
  private static IDispatcher _createDispatcherWithBoth(
    IWorkCoordinatorStrategy strategy,
    IDeferredOutboxChannel deferredChannel) {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();

    // Register deferred channel
    services.AddSingleton(deferredChannel);

    // Register work coordinator strategy (scoped to match typical usage)
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
