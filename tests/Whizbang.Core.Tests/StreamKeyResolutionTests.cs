using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests;

// Test events with stream key properties
public record OrderCreated([StreamId] string OrderId, string CustomerName) : IEvent;
public record OrderShipped([StreamId] string OrderId, string TrackingNumber) : IEvent;
public record UserRegistered([StreamId] Guid UserId, string Email) : IEvent;

// Event without stream key (should fail resolution)
// Intentionally missing StreamId to test error handling
#pragma warning disable WHIZ009
public record InvalidEvent(string Data) : IEvent;
#pragma warning restore WHIZ009

/// <summary>
/// Tests for stream key resolution from events.
/// Stream keys identify which stream (aggregate) an event belongs to.
/// Uses source-generated resolvers for zero-reflection performance.
/// </summary>
[Category("Core")]
[Category("StreamId")]
public class StreamIdResolutionTests {

  [Test]
  public async Task ResolveStreamId_WithStringProperty_ReturnsValueAsync() {
    // Arrange
    var evt = new OrderCreated("ORD-123", "John Doe");

    // Act
    var streamKey = StreamIdExtractors.Resolve(evt);

    // Assert
    await Assert.That(streamKey).IsEqualTo("ORD-123");
  }

  [Test]
  public async Task ResolveStreamId_WithGuidProperty_ReturnsStringValueAsync() {
    // Arrange
    var userId = Guid.NewGuid();
    var evt = new UserRegistered(userId, "user@example.com");

    // Act
    var streamKey = StreamIdExtractors.Resolve(evt);

    // Assert
    await Assert.That(streamKey).IsEqualTo(userId.ToString());
  }

  [Test]
  public async Task ResolveStreamId_WithNoStreamIdAttribute_ThrowsAsync() {
    // Arrange
    var evt = new InvalidEvent("test");

    // Act & Assert
    var exception = await Assert.That(() => StreamIdExtractors.Resolve(evt))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(exception!.Message).Contains("No stream ID extractor found");
  }

  [Test]
  public async Task ResolveStreamId_WithNullValue_ThrowsAsync() {
    // Arrange
    var evt = new OrderCreated(null!, "John Doe");

    // Act & Assert
    var exception = await Assert.That(() => StreamIdExtractors.Resolve(evt))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(exception!.Message).Contains("Stream ID 'OrderId' on OrderCreated cannot be null");
  }

  [Test]
  public async Task ResolveStreamId_WithEmptyString_ThrowsAsync() {
    // Arrange
    var evt = new OrderCreated("", "John Doe");

    // Act & Assert
    var exception = await Assert.That(() => StreamIdExtractors.Resolve(evt))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(exception!.Message).Contains("Stream ID 'OrderId' on OrderCreated cannot be empty");
  }

  [Test]
  public async Task ResolveStreamId_WithWhitespaceString_ThrowsAsync() {
    // Arrange
    var evt = new OrderCreated("   ", "John Doe");

    // Act & Assert
    var exception = await Assert.That(() => StreamIdExtractors.Resolve(evt))
      .ThrowsExactly<InvalidOperationException>();
    await Assert.That(exception!.Message).Contains("Stream ID 'OrderId' on OrderCreated cannot be empty");
  }

  [Test]
  public async Task ResolveStreamId_DifferentEventsForSameStream_ReturnsSameKeyAsync() {
    // Arrange
    var created = new OrderCreated("ORD-123", "John Doe");
    var shipped = new OrderShipped("ORD-123", "TRACK-456");

    // Act
    var key1 = StreamIdExtractors.Resolve(created);
    var key2 = StreamIdExtractors.Resolve(shipped);

    // Assert
    await Assert.That(key1).IsEqualTo(key2);
    await Assert.That(key1).IsEqualTo("ORD-123");
  }

  [Test]
  public async Task ResolveStreamId_WithConstructorParameter_ReturnsValueAsync() {
    // Arrange - Event defined with parameter attribute (record constructor)
    var inventoryId = Guid.NewGuid();
    var evt = new InventoryAdjusted(inventoryId, 10);

    // Act - Should resolve from constructor parameter attribute
    var streamKey = StreamIdExtractors.Resolve(evt);

    // Assert
    await Assert.That(streamKey).IsEqualTo(inventoryId.ToString());
  }
}

/// <summary>
/// Test event with [StreamId] on constructor parameter (record style).
/// This tests the constructor parameter resolution path in StreamIdResolver.
/// </summary>
public record InventoryAdjusted(
  [StreamId] Guid InventoryId,
  int Quantity
) : IEvent;
