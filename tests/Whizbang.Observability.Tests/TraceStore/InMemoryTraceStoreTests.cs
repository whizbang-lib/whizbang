using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests.TraceStore;

/// <summary>
/// Tests for InMemoryTraceStore implementation.
/// Inherits all contract tests from TraceStoreContractTests.
/// </summary>
[Category("Observability")]
[InheritsTests]
public class InMemoryTraceStoreTests : TraceStoreContractTests {
  protected override ITraceStore CreateTraceStore() {
    return new InMemoryTraceStore();
  }

  // Test message types
  private sealed record TestCommand(string Id, string Data);

  /// <summary>
  /// Helper to create a test envelope with specified properties.
  /// </summary>
  private static MessageEnvelope<TestCommand> _createEnvelope(
    MessageId? messageId = null,
    CorrelationId? correlationId = null,
    MessageId? causationId = null,
    DateTimeOffset? timestamp = null
  ) {
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = messageId ?? MessageId.New(),
      Payload = new TestCommand($"test-{Guid.NewGuid()}", "data"),
      Hops = []
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Current,
      Timestamp = timestamp ?? DateTimeOffset.UtcNow,
      CorrelationId = correlationId,
      CausationId = causationId
    });

    return envelope;
  }

  // ========================================
  // NULL ARGUMENT VALIDATION TESTS
  // ========================================

  [Test]
  public async Task StoreAsync_WithNullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var store = new InMemoryTraceStore();

    // Act & Assert
    var exception = await Assert.That(async () => await store.StoreAsync(null!))
      .ThrowsExactly<ArgumentNullException>();

    await Assert.That(exception!.ParamName).IsEqualTo("envelope");
  }

  // ========================================
  // GETBYCORRELATION EDGE CASES
  // ========================================

  [Test]
  public async Task GetByCorrelationAsync_WithNullCorrelationIdsInStore_FiltersThemOutAsync() {
    // Arrange
    var store = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();

    // Create envelope with matching correlation ID
    var envelope1 = _createEnvelope(correlationId: correlationId);

    // Create envelope with null correlation ID (should be filtered out)
    var envelope2 = _createEnvelope(correlationId: null);

    await store.StoreAsync(envelope1);
    await store.StoreAsync(envelope2);

    // Act
    var results = await store.GetByCorrelationAsync(correlationId);

    // Assert - Should only return envelope1
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].MessageId).IsEqualTo(envelope1.MessageId);
  }

  // ========================================
  // GETCAUSALCHAIN COMPLEX SCENARIOS
  // ========================================

  [Test]
  public async Task GetCausalChainAsync_WithCircularReference_ProtectsAgainstInfiniteLoopAsync() {
    // Arrange
    var store = new InMemoryTraceStore();

    // Create circular reference: message1 -> message2 -> message1
    var messageId1 = MessageId.New();
    var messageId2 = MessageId.New();

    var envelope1 = _createEnvelope(
      messageId: messageId1,
      causationId: messageId2, // Points to message2
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-2)
    );

    var envelope2 = _createEnvelope(
      messageId: messageId2,
      causationId: messageId1, // Points back to message1 (circular)
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-1)
    );

    await store.StoreAsync(envelope1);
    await store.StoreAsync(envelope2);

    // Act
    var chain = await store.GetCausalChainAsync(messageId1);

    // Assert - Should not infinite loop, should return both messages
    await Assert.That(chain).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(chain).Count().IsLessThanOrEqualTo(2); // Max 2 due to circular protection
  }

  [Test]
  public async Task GetCausalChainAsync_WithMissingParent_StopsWalkingUpChainAsync() {
    // Arrange
    var store = new InMemoryTraceStore();
    var missingParentId = MessageId.New();

    // Create message with causation to non-existent parent
    var envelope = _createEnvelope(
      causationId: missingParentId // Parent doesn't exist in store
    );

    await store.StoreAsync(envelope);

    // Act
    var chain = await store.GetCausalChainAsync(envelope.MessageId);

    // Assert - Should return just the message itself (parent walk stops at missing parent)
    await Assert.That(chain).Count().IsEqualTo(1);
    await Assert.That(chain[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task GetCausalChainAsync_WithChildren_IncludesChildMessagesAsync() {
    // Arrange
    var store = new InMemoryTraceStore();

    // Create parent-child chain:
    // parent -> child1, child2
    var parent = _createEnvelope(timestamp: DateTimeOffset.UtcNow.AddSeconds(-3));

    var child1 = _createEnvelope(
      causationId: MessageId.From(parent.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-2)
    );

    var child2 = _createEnvelope(
      causationId: MessageId.From(parent.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-1)
    );

    await store.StoreAsync(parent);
    await store.StoreAsync(child1);
    await store.StoreAsync(child2);

    // Act
    var chain = await store.GetCausalChainAsync(parent.MessageId);

    // Assert - Should include parent and both children
    await Assert.That(chain).Count().IsGreaterThanOrEqualTo(3);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(parent.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(child1.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(child2.MessageId);
  }

  [Test]
  public async Task GetCausalChainAsync_WithMultiGenerationChildren_IncludesAllDescendantsAsync() {
    // Arrange
    var store = new InMemoryTraceStore();

    // Create multi-generation tree:
    // grandparent -> parent -> child -> grandchild
    var grandparent = _createEnvelope(timestamp: DateTimeOffset.UtcNow.AddSeconds(-4));
    var parent = _createEnvelope(
      causationId: MessageId.From(grandparent.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-3)
    );
    var child = _createEnvelope(
      causationId: MessageId.From(parent.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-2)
    );
    var grandchild = _createEnvelope(
      causationId: MessageId.From(child.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-1)
    );

    await store.StoreAsync(grandparent);
    await store.StoreAsync(parent);
    await store.StoreAsync(child);
    await store.StoreAsync(grandchild);

    // Act - Query from middle of the chain
    var chain = await store.GetCausalChainAsync(parent.MessageId);

    // Assert - Should include grandparent (ancestor), parent (self), child, and grandchild (descendants)
    await Assert.That(chain).Count().IsEqualTo(4);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(grandparent.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(parent.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(child.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(grandchild.MessageId);
  }

  [Test]
  public async Task GetCausalChainAsync_WithEmptyCausationId_StopsWalkingAsync() {
    // Arrange
    var store = new InMemoryTraceStore();

    // Create message with empty Guid causation (root message)
    var envelope = _createEnvelope(
      causationId: MessageId.From(Guid.Empty) // Empty causation ID
    );

    await store.StoreAsync(envelope);

    // Act
    var chain = await store.GetCausalChainAsync(envelope.MessageId);

    // Assert - Should return just the message (no parent walk with empty causation)
    await Assert.That(chain).Count().IsEqualTo(1);
    await Assert.That(chain[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task GetCausalChainAsync_SortsResultsByTimestampAsync() {
    // Arrange
    var store = new InMemoryTraceStore();
    var now = DateTimeOffset.UtcNow;

    // Create chain with varying timestamps (store out of order)
    var message1 = _createEnvelope(timestamp: now.AddSeconds(-10));
    var message2 = _createEnvelope(
      causationId: MessageId.From(message1.MessageId.Value),
      timestamp: now.AddSeconds(-5)
    );
    var message3 = _createEnvelope(
      causationId: MessageId.From(message2.MessageId.Value),
      timestamp: now
    );

    // Store in reverse order
    await store.StoreAsync(message3);
    await store.StoreAsync(message1);
    await store.StoreAsync(message2);

    // Act
    var chain = await store.GetCausalChainAsync(message3.MessageId);

    // Assert - Should return in chronological order by timestamp
    await Assert.That(chain).Count().IsEqualTo(3);
    await Assert.That(chain[0].MessageId).IsEqualTo(message1.MessageId);
    await Assert.That(chain[1].MessageId).IsEqualTo(message2.MessageId);
    await Assert.That(chain[2].MessageId).IsEqualTo(message3.MessageId);
  }

  // ========================================
  // GETBYTIMERANGE EDGE CASES
  // ========================================

  [Test]
  public async Task GetByTimeRangeAsync_WithEnvelopesWithoutCurrentHop_UsesMinValueTimestampAsync() {
    // Arrange
    var store = new InMemoryTraceStore();
    var now = DateTimeOffset.UtcNow;

    // Create envelope with only Causation hop (no Current hop)
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = new TestCommand("test", "data"),
      Hops = []
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Causation, // Not Current!
      Timestamp = now
    });

    await store.StoreAsync(envelope);

    // Act - Query for recent messages
    var results = await store.GetByTimeRangeAsync(now.AddSeconds(-10), now.AddSeconds(10));

    // Assert - Should not include envelope (timestamp defaults to DateTimeOffset.MinValue for missing Current hop)
    await Assert.That(results).IsEmpty();
  }

  [Test]
  public async Task GetByTimeRangeAsync_WithNoHops_UsesMinValueTimestampAsync() {
    // Arrange
    var store = new InMemoryTraceStore();
    var now = DateTimeOffset.UtcNow;

    // Create envelope with no hops at all
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = new TestCommand("test", "data"),
      Hops = []
    };

    await store.StoreAsync(envelope);

    // Act - Query for recent messages
    var results = await store.GetByTimeRangeAsync(now.AddSeconds(-10), now.AddSeconds(10));

    // Assert - Should not include envelope (no hops = DateTimeOffset.MinValue)
    await Assert.That(results).IsEmpty();
  }

  [Test]
  public async Task GetCausalChainAsync_WithCircularReferenceInChildrenTree_ProtectsAgainstInfiniteLoopAsync() {
    // Arrange
    var store = new InMemoryTraceStore();

    // Create circular reference in children tree:
    // parent -> child1 -> child2 -> child1 (circular back to child1)
    var parent = _createEnvelope(timestamp: DateTimeOffset.UtcNow.AddSeconds(-4));

    var child1Id = MessageId.New();
    var child2Id = MessageId.New();

    var child1 = _createEnvelope(
      messageId: child1Id,
      causationId: MessageId.From(parent.MessageId.Value),
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-3)
    );

    var child2 = _createEnvelope(
      messageId: child2Id,
      causationId: child1Id,
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-2)
    );

    // Create a child that points back to child1 (circular reference)
    var circularChild = _createEnvelope(
      causationId: child1Id, // Points back to child1, creating a circular reference
      timestamp: DateTimeOffset.UtcNow.AddSeconds(-1)
    );

    await store.StoreAsync(parent);
    await store.StoreAsync(child1);
    await store.StoreAsync(child2);
    await store.StoreAsync(circularChild);

    // Act - Query from parent should walk down children without infinite loop
    var chain = await store.GetCausalChainAsync(parent.MessageId);

    // Assert - Should not infinite loop, should return all non-circular messages
    await Assert.That(chain).Count().IsGreaterThanOrEqualTo(3);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(parent.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(child1.MessageId);
    await Assert.That(chain.Select(e => e.MessageId)).Contains(child2.MessageId);
  }
}
