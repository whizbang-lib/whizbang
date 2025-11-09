using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for TransportAutoDiscovery and NamespacePattern.
/// Following TDD: These tests are written BEFORE implementing auto-discovery.
///
/// Phase 3: Auto-discovery of receptors and namespace pattern matching
/// </summary>
public class TransportAutoDiscoveryTests {
  // ========================================
  // PHASE 3: NamespacePattern Tests
  // ========================================

  [Test]
  public async Task NamespacePattern_ExactMatch_ShouldMatchAsync() {
    // Arrange
    var pattern = new NamespacePattern("MyApp.Orders.OrderCreated");
    var messageType = typeof(MyApp.Orders.OrderCreated);

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsTrue();
  }

  [Test]
  public async Task NamespacePattern_WildcardSuffix_ShouldMatchAsync() {
    // Arrange
    var pattern = new NamespacePattern("MyApp.Orders.*");
    var messageType = typeof(MyApp.Orders.OrderCreated);

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsTrue();
  }

  [Test]
  public async Task NamespacePattern_WildcardSuffix_ShouldNotMatchDifferentNamespaceAsync() {
    // Arrange
    var pattern = new NamespacePattern("MyApp.Orders.*");
    var messageType = typeof(MyApp.Payments.PaymentProcessed);

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsFalse();
  }

  [Test]
  public async Task NamespacePattern_WildcardPrefix_ShouldMatchAsync() {
    // Arrange
    var pattern = new NamespacePattern("*.Events.*");
    var messageType = typeof(MyApp.Orders.Events.OrderCreatedEvent);

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsTrue();
  }

  [Test]
  public async Task NamespacePattern_DoubleWildcard_ShouldMatchAsync() {
    // Arrange
    var pattern = new NamespacePattern("MyApp.*.*");
    var messageType = typeof(MyApp.Orders.OrderCreated);

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsTrue();
  }

  [Test]
  public async Task NamespacePattern_DoubleWildcard_ShouldMatchNestedAsync() {
    // Arrange
    var pattern = new NamespacePattern("MyApp.*.*");
    var messageType = typeof(MyApp.Orders.Events.OrderCreatedEvent);

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsTrue();
  }

  [Test]
  public async Task NamespacePattern_ShouldNotMatchWhenInsufficientSegmentsAsync() {
    // Arrange
    var pattern = new NamespacePattern("MyApp.Orders.*");
    var messageType = typeof(MyApp.Payments.PaymentReceived); // Only "MyApp.Payments"

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsFalse();
  }

  [Test]
  public async Task NamespacePattern_ShouldHandleNullNamespaceAsync() {
    // Arrange
    var pattern = new NamespacePattern("*.Events.*");
    var messageType = typeof(NoNamespaceMessage); // No namespace

    // Act
    var matches = pattern.Matches(messageType);

    // Assert
    await Assert.That(matches).IsFalse();
  }

  // ========================================
  // PHASE 3: TransportAutoDiscovery Tests
  // ========================================

  [Test]
  public async Task TransportAutoDiscovery_SubscribeToNamespace_ShouldStorePatternAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();

    // Act
    discovery.SubscribeToNamespace("MyApp.Orders.*");

    // Assert
    await Assert.That(discovery.GetNamespacePatterns()).HasCount().EqualTo(1);
  }

  [Test]
  public async Task TransportAutoDiscovery_Subscribe_ShouldStoreExplicitTypeAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();

    // Act
    discovery.Subscribe<MyApp.Orders.OrderCreated>();

    // Assert
    await Assert.That(discovery.GetExplicitTypes()).HasCount().EqualTo(1);
    await Assert.That(discovery.GetExplicitTypes()[0]).IsEqualTo(typeof(MyApp.Orders.OrderCreated));
  }

  [Test]
  public async Task TransportAutoDiscovery_ShouldSubscribe_WhenExplicitTypeAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();
    discovery.Subscribe<MyApp.Orders.OrderCreated>();

    // Act
    var shouldSubscribe = discovery.ShouldSubscribe(typeof(MyApp.Orders.OrderCreated));

    // Assert
    await Assert.That(shouldSubscribe).IsTrue();
  }

  [Test]
  public async Task TransportAutoDiscovery_ShouldNotSubscribe_WhenTypeNotAddedAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();

    // Act
    var shouldSubscribe = discovery.ShouldSubscribe(typeof(MyApp.Orders.OrderCreated));

    // Assert
    await Assert.That(shouldSubscribe).IsFalse();
  }

  [Test]
  public async Task TransportAutoDiscovery_ShouldSubscribe_WhenMatchesPatternAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();
    discovery.SubscribeToNamespace("MyApp.Orders.*");

    // Act
    var shouldSubscribe = discovery.ShouldSubscribe(typeof(MyApp.Orders.OrderCreated));

    // Assert
    await Assert.That(shouldSubscribe).IsTrue();
  }

  [Test]
  public async Task TransportAutoDiscovery_ShouldNotSubscribe_WhenDoesNotMatchPatternAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();
    discovery.SubscribeToNamespace("MyApp.Orders.*");

    // Act
    var shouldSubscribe = discovery.ShouldSubscribe(typeof(MyApp.Payments.PaymentProcessed));

    // Assert
    await Assert.That(shouldSubscribe).IsFalse();
  }

  [Test]
  public async Task TransportAutoDiscovery_ShouldSubscribe_WhenMatchesAnyPatternAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();
    discovery.SubscribeToNamespace("MyApp.Orders.*");
    discovery.SubscribeToNamespace("MyApp.Payments.*");

    // Act
    var shouldSubscribeOrders = discovery.ShouldSubscribe(typeof(MyApp.Orders.OrderCreated));
    var shouldSubscribePayments = discovery.ShouldSubscribe(typeof(MyApp.Payments.PaymentProcessed));

    // Assert
    await Assert.That(shouldSubscribeOrders).IsTrue();
    await Assert.That(shouldSubscribePayments).IsTrue();
  }

  [Test]
  public async Task TransportAutoDiscovery_ShouldSubscribe_WhenBothExplicitAndPatternMatchAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();
    discovery.Subscribe<MyApp.Orders.OrderCreated>();
    discovery.SubscribeToNamespace("MyApp.Orders.*");

    // Act
    var shouldSubscribe = discovery.ShouldSubscribe(typeof(MyApp.Orders.OrderCreated));

    // Assert
    await Assert.That(shouldSubscribe).IsTrue();
  }

  [Test]
  public async Task TransportAutoDiscovery_GetMessageTypesToSubscribe_ShouldReturnExplicitTypesAsync() {
    // Arrange
    var discovery = new TransportAutoDiscovery();
    discovery.Subscribe<MyApp.Orders.OrderCreated>();
    discovery.Subscribe<MyApp.Payments.PaymentProcessed>();

    // Act
    var types = discovery.GetMessageTypesToSubscribe();

    // Assert
    await Assert.That(types).HasCount().EqualTo(2);
    await Assert.That(types).Contains(typeof(MyApp.Orders.OrderCreated));
    await Assert.That(types).Contains(typeof(MyApp.Payments.PaymentProcessed));
  }

}
