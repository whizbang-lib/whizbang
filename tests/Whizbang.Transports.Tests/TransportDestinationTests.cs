using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for TransportDestination record.
/// Represents where a message should be sent (address, routing keys, metadata).
/// </summary>
public class TransportDestinationTests {
  // ============================================================================
  // Constructor Tests with Parameterization
  // ============================================================================

  [Test]
  [Arguments("test-topic")]
  [Arguments("orders.events")]
  [Arguments("user-notifications")]
  [Arguments("system.logs")]
  public async Task TransportDestination_WithAddress_SetsAddressAsync(string address) {
    // Arrange & Act
    var destination = new TransportDestination(address);

    // Assert
    await Assert.That(destination.Address).IsEqualTo(address);
    await Assert.That(destination.RoutingKey).IsNull();
    // Note: Metadata is intentionally nullable - testing that it IS null
    await Assert.That(destination.Metadata!).IsNull();
  }

  [Test]
  [Arguments("test-topic", "orders.created")]
  [Arguments("events", "user.registered")]
  [Arguments("notifications", "email.sent")]
  public async Task TransportDestination_WithAddressAndRoutingKey_SetsPropertiesAsync(
    string address,
    string routingKey) {
    // Arrange & Act
    var destination = new TransportDestination(address, RoutingKey: routingKey);

    // Assert
    await Assert.That(destination.Address).IsEqualTo(address);
    await Assert.That(destination.RoutingKey).IsEqualTo(routingKey);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task TransportDestination_WithMetadata_SetsMetadataAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      ["priority"] = JsonSerializer.SerializeToElement("high"),
      ["retry-count"] = JsonSerializer.SerializeToElement(3),
      ["timeout-ms"] = JsonSerializer.SerializeToElement(5000)
    };

    // Act
    var destination = new TransportDestination(
      "test-topic",
      Metadata: metadata
    );

    // Assert
    // Defensive null check before assertion
    var destinationMetadata = destination.Metadata;
    if (destinationMetadata == null) {
      throw new InvalidOperationException("Test failed: Expected metadata to be non-null");
    }

    await Assert.That(destinationMetadata).IsNotNull();
    await Assert.That(destinationMetadata["priority"].GetString()).IsEqualTo("high");
    await Assert.That(destinationMetadata["retry-count"].GetInt32()).IsEqualTo(3);
    await Assert.That(destinationMetadata["timeout-ms"].GetInt32()).IsEqualTo(5000);
  }

  [Test]
  public async Task TransportDestination_WithNullMetadata_AllowedAsync() {
    // Arrange & Act
    var destination = new TransportDestination("test-topic", Metadata: null);

    // Assert
    await Assert.That(destination.Address).IsEqualTo("test-topic");
    // Note: Metadata is intentionally nullable - testing that it IS null
    await Assert.That(destination.Metadata!).IsNull();
  }

  // ============================================================================
  // Record Equality Tests
  // ============================================================================

  public static IEnumerable<Func<(TransportDestination d1, TransportDestination d2, bool shouldBeEqual)>> GetEqualityTestCases() {
    // Same address and routing key
    yield return () => (
      new TransportDestination("topic", RoutingKey: "key1"),
      new TransportDestination("topic", RoutingKey: "key1"),
      true
    );

    // Same address, different routing key
    yield return () => (
      new TransportDestination("topic", RoutingKey: "key1"),
      new TransportDestination("topic", RoutingKey: "key2"),
      false
    );

    // Different address, same routing key
    yield return () => (
      new TransportDestination("topic1", RoutingKey: "key"),
      new TransportDestination("topic2", RoutingKey: "key"),
      false
    );

    // Same address, one with routing key, one without
    yield return () => (
      new TransportDestination("topic", RoutingKey: "key"),
      new TransportDestination("topic"),
      false
    );

    // Both without routing key
    yield return () => (
      new TransportDestination("topic"),
      new TransportDestination("topic"),
      true
    );
  }

  [Test]
  [MethodDataSource(nameof(GetEqualityTestCases))]
  public async Task TransportDestination_RecordEquality_BehavesCorrectlyAsync(
    TransportDestination d1,
    TransportDestination d2,
    bool shouldBeEqual) {
    // Act & Assert
    if (shouldBeEqual) {
      await Assert.That(d1).IsEqualTo(d2);
      await Assert.That(d1.GetHashCode()).IsEqualTo(d2.GetHashCode());
    } else {
      await Assert.That(d1).IsNotEqualTo(d2);
    }
  }

  // ============================================================================
  // Validation Tests
  // ============================================================================

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task TransportDestination_EmptyOrWhitespaceAddress_ThrowsArgumentExceptionAsync(string address) {
    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(() => {
      var destination = new TransportDestination(address);
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task TransportDestination_NullAddress_ThrowsArgumentExceptionAsync() {
    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(() => {
      var destination = new TransportDestination(null!);
      return Task.CompletedTask;
    });
  }

  // ============================================================================
  // Metadata Tests
  // ============================================================================

  [Test]
  public async Task TransportDestination_WithEmptyMetadata_PreservesEmptyDictionaryAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement>();

    // Act
    var destination = new TransportDestination("topic", Metadata: metadata);

    // Assert
    // Defensive null check before assertion
    var destinationMetadata = destination.Metadata;
    if (destinationMetadata == null) {
      throw new InvalidOperationException("Test failed: Expected metadata to be non-null");
    }

    await Assert.That(destinationMetadata).IsNotNull();
    await Assert.That(destinationMetadata.Count).IsEqualTo(0);
  }

  [Test]
  [RequiresDynamicCode("")]
  public async Task TransportDestination_MetadataIsReadOnly_CannotModifyAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      ["key"] = JsonSerializer.SerializeToElement("value")
    };
    var destination = new TransportDestination("topic", Metadata: metadata);

    // Act & Assert - Metadata should be IReadOnlyDictionary
    // Note: Metadata is nullable, but we're testing type when non-null
    await Assert.That(destination.Metadata!).IsTypeOf<IReadOnlyDictionary<string, JsonElement>>();
  }
}
