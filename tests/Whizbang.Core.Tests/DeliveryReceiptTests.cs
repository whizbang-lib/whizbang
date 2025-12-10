using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for DeliveryReceipt implementation.
/// Ensures all factory methods and properties are properly tested.
/// </summary>
public class DeliveryReceiptTests {
  [Test]
  public async Task Accepted_CreatesReceiptWithAcceptedStatusAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "TestReceptor";
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    // Act
    var receipt = DeliveryReceipt.Accepted(messageId, destination, correlationId, causationId);

    // Assert
    await Assert.That(receipt.MessageId).IsEqualTo(messageId);
    await Assert.That(receipt.Destination).IsEqualTo(destination);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(receipt.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(receipt.CausationId).IsEqualTo(causationId);
    await Assert.That(receipt.Timestamp).IsNotEqualTo(default);
  }

  [Test]
  public async Task Queued_CreatesReceiptWithQueuedStatusAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "TestQueue";
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    // Act
    var receipt = DeliveryReceipt.Queued(messageId, destination, correlationId, causationId);

    // Assert
    await Assert.That(receipt.MessageId).IsEqualTo(messageId);
    await Assert.That(receipt.Destination).IsEqualTo(destination);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Queued);
    await Assert.That(receipt.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(receipt.CausationId).IsEqualTo(causationId);
    await Assert.That(receipt.Timestamp).IsNotEqualTo(default);
  }

  [Test]
  public async Task Delivered_CreatesReceiptWithDeliveredStatusAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "TestHandler";

    // Act
    var receipt = DeliveryReceipt.Delivered(messageId, destination);

    // Assert
    await Assert.That(receipt.MessageId).IsEqualTo(messageId);
    await Assert.That(receipt.Destination).IsEqualTo(destination);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task Failed_CreatesReceiptWithFailedStatusAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "FailedHandler";
    var exception = new InvalidOperationException("Test error");

    // Act
    var receipt = DeliveryReceipt.Failed(messageId, destination, null, null, exception);

    // Assert
    await Assert.That(receipt.MessageId).IsEqualTo(messageId);
    await Assert.That(receipt.Destination).IsEqualTo(destination);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Failed);
    await Assert.That(receipt.Metadata.ContainsKey("ExceptionType")).IsTrue();
    await Assert.That(receipt.Metadata.ContainsKey("ExceptionMessage")).IsTrue();
    await Assert.That(receipt.Metadata["ExceptionMessage"].GetString()).IsEqualTo("Test error");
  }

  [Test]
  public async Task Metadata_IsEmptyByDefault_WhenNoMetadataProvidedAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "TestHandler";

    // Act
    var receipt = DeliveryReceipt.Accepted(messageId, destination);

    // Assert
    await Assert.That(receipt.Metadata).IsNotNull();
    await Assert.That(receipt.Metadata.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Timestamp_IsSetToCurrentTime_WhenReceiptCreatedAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "TestHandler";
    var before = DateTimeOffset.UtcNow;

    // Act
    var receipt = DeliveryReceipt.Delivered(messageId, destination);
    var after = DateTimeOffset.UtcNow;

    // Assert
    await Assert.That(receipt.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(receipt.Timestamp).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task Constructor_ThrowsArgumentNullException_WhenDestinationIsNullAsync() {
    // Arrange
    var messageId = MessageId.New();

    // Act & Assert
    await Assert.That(() => new DeliveryReceipt(
      messageId,
      null!,
      DeliveryStatus.Accepted
    )).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task Constructor_WithMetadata_CopiesMetadataDictionaryAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "TestHandler";
    var originalMetadata = new Dictionary<string, JsonElement> {
      ["Key1"] = JsonSerializer.SerializeToElement("Value1"),
      ["Key2"] = JsonSerializer.SerializeToElement(42)
    };

    // Act
    var receipt = new DeliveryReceipt(
      messageId,
      destination,
      DeliveryStatus.Delivered,
      metadata: originalMetadata
    );

    // Assert - Metadata should be copied, not referenced
    await Assert.That(receipt.Metadata.Count).IsEqualTo(2);
    await Assert.That(receipt.Metadata["Key1"].GetString()).IsEqualTo("Value1");
    await Assert.That(receipt.Metadata["Key2"].GetInt32()).IsEqualTo(42);

    // Verify it's a copy by modifying original
    originalMetadata["Key3"] = JsonSerializer.SerializeToElement("Value3");
    await Assert.That(receipt.Metadata.Count).IsEqualTo(2); // Should still be 2
  }

  [Test]
  public async Task AllProperties_AreAccessible_ThroughInterfaceAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "TestHandler";
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    // Act
    IDeliveryReceipt receipt = DeliveryReceipt.Accepted(messageId, destination, correlationId, causationId);

    // Assert - Verify all properties are accessible through interface
    await Assert.That(receipt.MessageId).IsEqualTo(messageId);
    await Assert.That(receipt.Destination).IsEqualTo(destination);
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Accepted);
    await Assert.That(receipt.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(receipt.CausationId).IsEqualTo(causationId);
    await Assert.That(receipt.Timestamp).IsNotEqualTo(default);
    await Assert.That(receipt.Metadata).IsNotNull();
  }
}
