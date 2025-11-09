using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for TransportManager.
/// Following TDD: These tests are written BEFORE implementing TransportManager.
///
/// Phase 4: Transport Manager - manages multiple transport instances
/// </summary>
public class TransportManagerTests {
  // ========================================
  // PHASE 4: AddTransport Tests
  // ========================================

  [Test]
  public async Task AddTransport_ShouldStoreTransportAsync() {
    // Arrange
    var manager = new TransportManager();
    var transport = new InProcessTransport();

    // Act
    manager.AddTransport(TransportType.InProcess, transport);

    // Assert
    await Assert.That(manager.HasTransport(TransportType.InProcess)).IsTrue();
  }

  [Test]
  public async Task AddTransport_WithNullTransport_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager();

    // Act & Assert
    await Assert.That(() => manager.AddTransport(TransportType.InProcess, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AddTransport_ShouldReplaceExistingTransportAsync() {
    // Arrange
    var manager = new TransportManager();
    var transport1 = new InProcessTransport();
    var transport2 = new InProcessTransport();

    // Act
    manager.AddTransport(TransportType.InProcess, transport1);
    manager.AddTransport(TransportType.InProcess, transport2);
    var retrieved = manager.GetTransport(TransportType.InProcess);

    // Assert
    await Assert.That(retrieved).IsSameReferenceAs(transport2);
  }

  [Test]
  public async Task AddTransport_ShouldStoreDifferentTypesAsync() {
    // Arrange
    var manager = new TransportManager();
    var inProcessTransport = new InProcessTransport();
    var kafkaTransport = new InProcessTransport(); // Mock for now

    // Act
    manager.AddTransport(TransportType.InProcess, inProcessTransport);
    manager.AddTransport(TransportType.Kafka, kafkaTransport);

    // Assert
    await Assert.That(manager.HasTransport(TransportType.InProcess)).IsTrue();
    await Assert.That(manager.HasTransport(TransportType.Kafka)).IsTrue();
  }

  // ========================================
  // PHASE 4: GetTransport Tests
  // ========================================

  [Test]
  public async Task GetTransport_WhenExists_ShouldReturnTransportAsync() {
    // Arrange
    var manager = new TransportManager();
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    // Act
    var retrieved = manager.GetTransport(TransportType.InProcess);

    // Assert
    await Assert.That(retrieved).IsSameReferenceAs(transport);
  }

  [Test]
  public async Task GetTransport_WhenNotExists_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager();

    // Act & Assert
    var exception = await Assert.That(() => manager.GetTransport(TransportType.Kafka))
      .ThrowsExactly<InvalidOperationException>();

    await Assert.That(exception?.Message).Contains("not registered");
  }

  // ========================================
  // PHASE 4: HasTransport Tests
  // ========================================

  [Test]
  public async Task HasTransport_WhenExists_ShouldReturnTrueAsync() {
    // Arrange
    var manager = new TransportManager();
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    // Act
    var exists = manager.HasTransport(TransportType.InProcess);

    // Assert
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task HasTransport_WhenNotExists_ShouldReturnFalseAsync() {
    // Arrange
    var manager = new TransportManager();

    // Act
    var exists = manager.HasTransport(TransportType.Kafka);

    // Assert
    await Assert.That(exists).IsFalse();
  }

  // ========================================
  // PHASE 4: PublishToTargetsAsync Tests
  // ========================================

  [Test]
  public async Task PublishToTargetsAsync_WithEmptyTargets_ShouldNotThrowAsync() {
    // Arrange
    var manager = new TransportManager();
    var message = new TestMessage { Content = "test", Value = 42 };
    var targets = new List<PublishTarget>();

    // Act & Assert - Should not throw
    await manager.PublishToTargetsAsync(message, targets);
  }

  [Test]
  public async Task PublishToTargetsAsync_WithNullMessage_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager();
    var targets = new List<PublishTarget>();

    // Act & Assert
    await Assert.That(() => manager.PublishToTargetsAsync<TestMessage>(null!, targets))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task PublishToTargetsAsync_WithNullTargets_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager();
    var message = new TestMessage { Content = "test", Value = 42 };

    // Act & Assert
    await Assert.That(() => manager.PublishToTargetsAsync(message, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // PHASE 4: SubscribeFromTargetsAsync Tests
  // ========================================

  [Test]
  public async Task SubscribeFromTargetsAsync_WithEmptyTargets_ShouldReturnEmptyListAsync() {
    // Arrange
    var manager = new TransportManager();
    var targets = new List<SubscriptionTarget>();

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(0);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithNullTargets_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager();

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act & Assert
    await Assert.That(() => manager.SubscribeFromTargetsAsync(null!, handler))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithNullHandler_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager();
    var targets = new List<SubscriptionTarget>();

    // Act & Assert
    await Assert.That(() => manager.SubscribeFromTargetsAsync(targets, null!))
      .ThrowsExactly<ArgumentNullException>();
  }
}

// Test message for Phase 4 tests
public record TestMessage {
  public required string Content { get; init; }
  public required int Value { get; init; }
}
