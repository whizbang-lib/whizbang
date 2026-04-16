using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests that PublishAsync runs the outbox concurrently with the local receptor.
/// The outbox task starts before the receptor and QueueOutboxMessage is called synchronously
/// before the first await (FlushAsync), guaranteeing FIFO ordering via C# async semantics.
/// </summary>
[NotInParallel]
public class DispatcherConcurrentOutboxTests {
  // ========================================
  // TEST MESSAGES
  // ========================================

  public record BlockingTestEvent([property: StreamId] Guid Id) : IEvent;
  public record ThrowingTestEvent([property: StreamId] Guid Id) : IEvent;
  public record HappyPathTestEvent([property: StreamId] Guid Id) : IEvent;

  // ========================================
  // RECEPTORS (static gate/exception for test control)
  // ========================================

  public class BlockingTestEventReceptor : IReceptor<BlockingTestEvent> {
    internal static TaskCompletionSource? Gate;
    internal static bool WasInvoked;

    public async ValueTask HandleAsync(BlockingTestEvent message, CancellationToken cancellationToken = default) {
      WasInvoked = true;
      if (Gate != null) {
        await Gate.Task;
      }
    }
  }

  public class ThrowingTestEventReceptor : IReceptor<ThrowingTestEvent> {
    internal static Exception? ExceptionToThrow;

    public ValueTask HandleAsync(ThrowingTestEvent message, CancellationToken cancellationToken = default) {
      if (ExceptionToThrow != null) {
        throw ExceptionToThrow;
      }
      return ValueTask.CompletedTask;
    }
  }

  // HappyPathTestEvent has no receptor — falls back to no-op publisher via generated code

  // ========================================
  // STUBS
  // ========================================

  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public List<InboxMessage> QueuedInboxMessages { get; } = [];
    public int FlushCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) => QueuedInboxMessages.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class ThrowingFlushStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public Exception? FlushException { get; set; }

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      if (FlushException != null) {
        throw FlushException;
      }
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
      throw new NotImplementedException("Not needed for concurrent outbox tests");
    }
  }

  // ========================================
  // SETUP
  // ========================================

  [Before(Test)]
  public void ResetStaticState() {
    BlockingTestEventReceptor.Gate = null;
    BlockingTestEventReceptor.WasInvoked = false;
    ThrowingTestEventReceptor.ExceptionToThrow = null;
  }

  // ========================================
  // TESTS
  // ========================================

  /// <summary>
  /// Proves concurrency: the outbox message is queued BEFORE the receptor completes.
  /// PublishToOutboxAsync runs synchronously through QueueOutboxMessage before its first await (FlushAsync).
  /// Because it starts before the publisher, the message is in the queue while the receptor is still blocked.
  /// If the code were sequential, QueuedOutboxMessages would be empty while the receptor is blocked.
  /// </summary>
  [Test]
  public async Task PublishAsync_QueuesOutboxBeforeReceptorCompletesAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    BlockingTestEventReceptor.Gate = new TaskCompletionSource();
    var @event = new BlockingTestEvent(Guid.NewGuid());

    // Act - start publish without awaiting (receptor will block)
    var publishTask = dispatcher.PublishAsync(@event);

    // Assert - outbox message was queued while receptor is still blocked
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(BlockingTestEventReceptor.WasInvoked).IsTrue();

    // Release the receptor and await completion
    BlockingTestEventReceptor.Gate.SetResult();
    var receipt = await publishTask;

    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  /// <summary>
  /// Same concurrency proof for the DispatchOptions overload.
  /// </summary>
  [Test]
  public async Task PublishAsync_WithOptions_QueuesOutboxBeforeReceptorCompletesAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    BlockingTestEventReceptor.Gate = new TaskCompletionSource();
    var @event = new BlockingTestEvent(Guid.NewGuid());
    var options = new DispatchOptions();

    // Act - start publish without awaiting
    var publishTask = dispatcher.PublishAsync(@event, options);

    // Assert - outbox queued while receptor blocked
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(BlockingTestEventReceptor.WasInvoked).IsTrue();

    // Release and await
    BlockingTestEventReceptor.Gate.SetResult();
    var receipt = await publishTask;

    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  /// <summary>
  /// When the receptor throws, the exception propagates from PublishAsync
  /// even though the outbox runs concurrently.
  /// </summary>
  [Test]
  public async Task PublishAsync_WhenReceptorThrows_PropagatesReceptorExceptionAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    ThrowingTestEventReceptor.ExceptionToThrow = new InvalidOperationException("receptor failed");
    var @event = new ThrowingTestEvent(Guid.NewGuid());

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync(@event))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessage("receptor failed");
  }

  /// <summary>
  /// When FlushAsync throws but the receptor succeeds, the outbox exception propagates.
  /// </summary>
  [Test]
  public async Task PublishAsync_WhenFlushThrows_PropagatesOutboxExceptionAsync() {
    // Arrange
    var strategy = new ThrowingFlushStrategy {
      FlushException = new IOException("flush failed")
    };
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new HappyPathTestEvent(Guid.NewGuid());

    // Act & Assert
    await Assert.That(async () => await dispatcher.PublishAsync(@event))
      .ThrowsExactly<IOException>()
      .WithMessage("flush failed");
  }

  /// <summary>
  /// When both receptor and outbox throw, the receptor exception takes priority
  /// because the outbox exception is caught and swallowed in the catch block.
  /// </summary>
  [Test]
  public async Task PublishAsync_WhenBothThrow_PropagatesReceptorExceptionAsync() {
    // Arrange
    var strategy = new ThrowingFlushStrategy {
      FlushException = new IOException("flush failed")
    };
    var dispatcher = _createDispatcherWithStrategy(strategy);
    ThrowingTestEventReceptor.ExceptionToThrow = new InvalidOperationException("receptor failed");
    var @event = new ThrowingTestEvent(Guid.NewGuid());

    // Act & Assert - receptor exception wins
    await Assert.That(async () => await dispatcher.PublishAsync(@event))
      .ThrowsExactly<InvalidOperationException>()
      .WithMessage("receptor failed");
  }

  /// <summary>
  /// Happy path: both outbox and receptor succeed, delivery receipt is returned.
  /// </summary>
  [Test]
  public async Task PublishAsync_HappyPath_ReturnsDeliveryReceiptAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var eventId = Guid.NewGuid();
    var @event = new HappyPathTestEvent(eventId);

    // Act
    var receipt = await dispatcher.PublishAsync(@event);

    // Assert
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.FlushCount).IsEqualTo(1);
  }

  // ========================================
  // FACTORY
  // ========================================

  private static IDispatcher _createDispatcherWithStrategy(IWorkCoordinatorStrategy strategy) {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
