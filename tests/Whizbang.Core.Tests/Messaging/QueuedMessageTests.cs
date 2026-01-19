using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for QueuedMessage record.
/// Verifies value equality, serialization, and property behavior.
/// </summary>
[Category("Messaging")]
[Category("ValueTypes")]
public class QueuedMessageTests {

  [Test]
  public async Task QueuedMessage_WithAllProperties_CreatesInstanceAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var eventType = "TestEvent";
    var eventData = "{\"test\":\"data\"}";
    var metadata = "{\"meta\":\"data\"}";

    // Act
    var message = new QueuedMessage {
      MessageId = messageId,
      EventType = eventType,
      EventData = eventData,
      Metadata = metadata
    };

    // Assert
    await Assert.That(message.MessageId).IsEqualTo(messageId);
    await Assert.That(message.EventType).IsEqualTo(eventType);
    await Assert.That(message.EventData).IsEqualTo(eventData);
    await Assert.That(message.Metadata).IsEqualTo(metadata);
  }

  [Test]
  public async Task QueuedMessage_WithoutMetadata_AllowsNullAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var eventType = "TestEvent";
    var eventData = "{\"test\":\"data\"}";

    // Act
    var message = new QueuedMessage {
      MessageId = messageId,
      EventType = eventType,
      EventData = eventData,
      Metadata = null
    };

    // Assert
    await Assert.That(message.Metadata).IsNull();
    await Assert.That(message.MessageId).IsEqualTo(messageId);
    await Assert.That(message.EventType).IsEqualTo(eventType);
    await Assert.That(message.EventData).IsEqualTo(eventData);
  }

  [Test]
  public async Task QueuedMessage_ValueEquality_ComparesByValueAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var message1 = new QueuedMessage {
      MessageId = messageId,
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = null
    };

    var message2 = new QueuedMessage {
      MessageId = messageId,
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = null
    };

    // Act & Assert
    await Assert.That(message1).IsEqualTo(message2);
    await Assert.That(message1.GetHashCode()).IsEqualTo(message2.GetHashCode());
  }

  [Test]
  public async Task QueuedMessage_Serialization_RoundTripsCorrectlyAsync() {
    // Arrange
    var message = new QueuedMessage {
      MessageId = Guid.NewGuid(),
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{\"meta\":\"data\"}"
    };

    // Act
    var json = JsonSerializer.Serialize(message);
    var deserialized = JsonSerializer.Deserialize<QueuedMessage>(json);

    // Assert
    await Assert.That(deserialized).IsEqualTo(message);
  }
}
