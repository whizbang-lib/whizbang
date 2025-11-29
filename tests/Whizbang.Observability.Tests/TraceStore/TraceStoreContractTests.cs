using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests.TraceStore;

/// <summary>
/// Contract tests that all ITraceStore implementations must pass.
/// Inherit from this class and implement CreateTraceStore() to test your implementation.
/// </summary>
[Category("Observability")]
public abstract class TraceStoreContractTests {
  // Test messages
  private record OrderCommand(string OrderId, decimal Amount);
  private record PaymentCommand(string PaymentId, decimal Amount);

  /// <summary>
  /// Factory method for creating the trace store implementation under test.
  /// </summary>
  protected abstract ITraceStore CreateTraceStore();

  /// <summary>
  /// Helper to create a test envelope with specified IDs and timing.
  /// </summary>
  private static IMessageEnvelope CreateTestEnvelope<TMessage>(
    TMessage payload,
    MessageId? messageId = null,
    CorrelationId? correlationId = null,
    MessageId? causationId = null,
    DateTimeOffset? timestamp = null
  ) {
    var envelope = new Whizbang.Core.Observability.MessageEnvelope<TMessage> {
      MessageId = messageId ?? MessageId.New(),
      Payload = payload,
      Hops = []
    };

    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "Test",
      Timestamp = timestamp ?? DateTimeOffset.UtcNow,
      CorrelationId = correlationId ?? CorrelationId.New(),
      CausationId = causationId
    });

    return envelope;
  }

  [Test]
  public async Task TraceStore_StoreAndRetrieve_ShouldStoreAndRetrieveEnvelopeAsync() {
    // Arrange
    var store = CreateTraceStore();
    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);

    // Act
    await store.StoreAsync(envelope);
    var retrieved = await store.GetByMessageIdAsync(envelope.MessageId);

    // Assert
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.MessageId).IsEqualTo(envelope.MessageId);
    await Assert.That(retrieved.GetCorrelationId()).IsEqualTo(envelope.GetCorrelationId());
  }

  [Test]
  public async Task TraceStore_GetByMessageId_ShouldReturnNullForNonExistentTraceAsync() {
    // Arrange
    var store = CreateTraceStore();
    var nonExistentId = MessageId.New();

    // Act
    var result = await store.GetByMessageIdAsync(nonExistentId);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TraceStore_GetByCorrelation_ShouldReturnAllMessagesWithSameCorrelationIdAsync() {
    // Arrange
    var store = CreateTraceStore();
    var correlationId = CorrelationId.New();

    var message1 = new OrderCommand("order-1", 100m);
    var message2 = new OrderCommand("order-2", 200m);
    var message3 = new OrderCommand("order-3", 300m);

    var envelope1 = CreateTestEnvelope(message1, correlationId: correlationId, timestamp: DateTimeOffset.UtcNow.AddSeconds(-2));
    var envelope2 = CreateTestEnvelope(message2, correlationId: correlationId, timestamp: DateTimeOffset.UtcNow.AddSeconds(-1));
    var envelope3 = CreateTestEnvelope(message3, correlationId: correlationId, timestamp: DateTimeOffset.UtcNow);

    await store.StoreAsync(envelope1);
    await store.StoreAsync(envelope2);
    await store.StoreAsync(envelope3);

    // Act
    var results = await store.GetByCorrelationAsync(correlationId);

    // Assert
    await Assert.That(results).HasCount().EqualTo(3);
    await Assert.That(results[0].MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(results[1].MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(results[2].MessageId).IsEqualTo(envelope3.MessageId);
  }

  [Test]
  public async Task TraceStore_GetByCorrelation_ShouldReturnEmptyListWhenNoMatchesAsync() {
    // Arrange
    var store = CreateTraceStore();
    var nonExistentCorrelationId = CorrelationId.New();

    // Act
    var results = await store.GetByCorrelationAsync(nonExistentCorrelationId);

    // Assert
    await Assert.That(results).IsEmpty();
  }

  [Test]
  public async Task TraceStore_GetCausalChain_ShouldReturnMessageAndParentsAsync() {
    // Arrange
    var store = CreateTraceStore();
    var correlationId = CorrelationId.New();

    // Create causal chain: message1 -> message2 -> message3
    var message1 = new OrderCommand("order-1", 100m);
    var envelope1 = CreateTestEnvelope(
      message1,
      correlationId: correlationId,
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-2)
    );

    var message2 = new OrderCommand("order-2", 200m);
    var envelope2 = CreateTestEnvelope(
      message2,
      correlationId: correlationId,
      causationId: MessageId.From(envelope1.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-1)
    );

    var message3 = new OrderCommand("order-3", 300m);
    var envelope3 = CreateTestEnvelope(
      message3,
      correlationId: correlationId,
      causationId: MessageId.From(envelope2.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow
    );

    await store.StoreAsync(envelope1);
    await store.StoreAsync(envelope2);
    await store.StoreAsync(envelope3);

    // Act
    var chain = await store.GetCausalChainAsync(envelope3.MessageId);

    // Assert - Should include message3, message2, and message1
    await Assert.That(chain).HasCount().GreaterThanOrEqualTo(3);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(envelope1.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(envelope2.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(envelope3.MessageId);
  }

  [Test]
  public async Task TraceStore_GetCausalChain_ShouldReturnJustMessageWhenNoParentsAsync() {
    // Arrange
    var store = CreateTraceStore();
    var message = new OrderCommand("order-1", 100m);
    var envelope = CreateTestEnvelope(message);

    await store.StoreAsync(envelope);

    // Act
    var chain = await store.GetCausalChainAsync(envelope.MessageId);

    // Assert - Should include only the message itself
    await Assert.That(chain).HasCount().EqualTo(1);
    await Assert.That(chain[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task TraceStore_GetCausalChain_ShouldReturnEmptyWhenMessageNotFoundAsync() {
    // Arrange
    var store = CreateTraceStore();
    var nonExistentId = MessageId.New();

    // Act
    var chain = await store.GetCausalChainAsync(nonExistentId);

    // Assert
    await Assert.That(chain).IsEmpty();
  }

  [Test]
  public async Task TraceStore_GetByTimeRange_ShouldReturnMessagesInRangeAsync() {
    // Arrange
    var store = CreateTraceStore();
    var now = DateTimeOffset.UtcNow;

    var message1 = new OrderCommand("order-1", 100m);
    var message2 = new OrderCommand("order-2", 200m);
    var message3 = new OrderCommand("order-3", 300m);

    var envelope1 = CreateTestEnvelope(message1, timestamp: now.AddSeconds(-10));
    var envelope2 = CreateTestEnvelope(message2, timestamp: now.AddSeconds(-5));
    var envelope3 = CreateTestEnvelope(message3, timestamp: now);

    await store.StoreAsync(envelope1);
    await store.StoreAsync(envelope2);
    await store.StoreAsync(envelope3);

    // Act - Query for messages from -7 seconds to -3 seconds
    var results = await store.GetByTimeRangeAsync(now.AddSeconds(-7), now.AddSeconds(-3));

    // Assert - Should only return envelope2
    await Assert.That(results).HasCount().EqualTo(1);
    await Assert.That(results[0].MessageId).IsEqualTo(envelope2.MessageId);
  }

  [Test]
  public async Task TraceStore_GetByTimeRange_ShouldReturnEmptyWhenNoMatchesAsync() {
    // Arrange
    var store = CreateTraceStore();
    var now = DateTimeOffset.UtcNow;

    var message = new OrderCommand("order-1", 100m);
    var envelope = CreateTestEnvelope(message, timestamp: now);

    await store.StoreAsync(envelope);

    // Act - Query for time range before the message
    var results = await store.GetByTimeRangeAsync(now.AddSeconds(-10), now.AddSeconds(-5));

    // Assert
    await Assert.That(results).IsEmpty();
  }

  [Test]
  public async Task TraceStore_GetByTimeRange_ShouldReturnMessagesInChronologicalOrderAsync() {
    // Arrange
    var store = CreateTraceStore();
    var now = DateTimeOffset.UtcNow;

    var message1 = new OrderCommand("order-1", 100m);
    var message2 = new OrderCommand("order-2", 200m);
    var message3 = new OrderCommand("order-3", 300m);

    var envelope1 = CreateTestEnvelope(message1, timestamp: now.AddSeconds(-10));
    var envelope2 = CreateTestEnvelope(message2, timestamp: now.AddSeconds(-5));
    var envelope3 = CreateTestEnvelope(message3, timestamp: now);

    // Store in random order
    await store.StoreAsync(envelope2);
    await store.StoreAsync(envelope1);
    await store.StoreAsync(envelope3);

    // Act
    var results = await store.GetByTimeRangeAsync(now.AddSeconds(-15), now.AddSeconds(5));

    // Assert - Should return in chronological order
    await Assert.That(results).HasCount().EqualTo(3);
    await Assert.That(results[0].MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(results[1].MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(results[2].MessageId).IsEqualTo(envelope3.MessageId);
  }

  [Test]
  public async Task TraceStore_ConcurrentStores_ShouldHandleConcurrencyAsync() {
    // Arrange
    var store = CreateTraceStore();
    var tasks = new List<Task>();

    // Act - Store 100 messages concurrently
    for (int i = 0; i < 100; i++) {
      var message = new OrderCommand($"order-{i}", i * 10m);
      var envelope = CreateTestEnvelope(message);
      tasks.Add(store.StoreAsync(envelope));
    }

    await Task.WhenAll(tasks);

    // Assert - Verify we can query by time range and get all messages
    var results = await store.GetByTimeRangeAsync(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    await Assert.That(results).HasCount().GreaterThanOrEqualTo(100);
  }
}
