using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for OutboxMessage record equality and hashcode.
/// </summary>
[Category("Messaging")]
public class OutboxMessageTests {
  [Test]
  public async Task OutboxMessage_Equals_WithNull_ShouldReturnFalseAsync() {
    // Arrange
    var message = CreateTestMessage();

    // Act
    var result = message.Equals(null);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithSameReference_ShouldReturnTrueAsync() {
    // Arrange
    var message = CreateTestMessage();

    // Act
    var result = message.Equals(message);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithEqualValues_ShouldReturnTrueAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var eventType = "TestEvent";
    var eventData = "{\"value\": 123}";
    var metadata = "{\"hops\": []}";
    var scope = null as string;
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, eventType, eventData, metadata, scope, createdAt);
    var message2 = new OutboxMessage(messageId, destination, eventType, eventData, metadata, scope, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentMessageId_ShouldReturnFalseAsync() {
    // Arrange
    var destination = "test-destination";
    var eventType = "TestEvent";
    var eventData = "{\"value\": 123}";
    var metadata = "{\"hops\": []}";
    var scope = null as string;
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(MessageId.New(), destination, eventType, eventData, metadata, scope, createdAt);
    var message2 = new OutboxMessage(MessageId.New(), destination, eventType, eventData, metadata, scope, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentDestination_ShouldReturnFalseAsync() {
    // Arrange
    var messageId = MessageId.New();
    var eventType = "TestEvent";
    var eventData = "{\"value\": 123}";
    var metadata = "{\"hops\": []}";
    var scope = null as string;
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, "destination1", eventType, eventData, metadata, scope, createdAt);
    var message2 = new OutboxMessage(messageId, "destination2", eventType, eventData, metadata, scope, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentEventData_ShouldReturnFalseAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var eventType = "TestEvent";
    var metadata = "{\"hops\": []}";
    var scope = null as string;
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, eventType, "{\"value\": 123}", metadata, scope, createdAt);
    var message2 = new OutboxMessage(messageId, destination, eventType, "{\"value\": 456}", metadata, scope, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentMetadata_ShouldReturnFalseAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var eventType = "TestEvent";
    var eventData = "{\"value\": 123}";
    var scope = null as string;
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, eventType, eventData, "{\"hops\": []}", scope, createdAt);
    var message2 = new OutboxMessage(messageId, destination, eventType, eventData, "{\"hops\": [{\"type\": \"current\"}]}", scope, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentCreatedAt_ShouldReturnFalseAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var eventType = "TestEvent";
    var eventData = "{\"value\": 123}";
    var metadata = "{\"hops\": []}";
    var scope = null as string;

    var message1 = new OutboxMessage(messageId, destination, eventType, eventData, metadata, scope, DateTimeOffset.UtcNow);
    await Task.Delay(10); // Ensure different timestamps
    var message2 = new OutboxMessage(messageId, destination, eventType, eventData, metadata, scope, DateTimeOffset.UtcNow);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_GetHashCode_WithSameValues_ShouldReturnSameHashAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var eventType = "TestEvent";
    var eventData = "{\"value\": 123}";
    var metadata = "{\"hops\": []}";
    var scope = null as string;
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, eventType, eventData, metadata, scope, createdAt);
    var message2 = new OutboxMessage(messageId, destination, eventType, eventData, metadata, scope, createdAt);

    // Act
    var hash1 = message1.GetHashCode();
    var hash2 = message2.GetHashCode();

    // Assert
    await Assert.That(hash1).IsEqualTo(hash2);
  }

  [Test]
  public async Task OutboxMessage_GetHashCode_WithDifferentValues_ShouldReturnDifferentHashAsync() {
    // Arrange
    var message1 = CreateTestMessage();
    var message2 = CreateTestMessage();

    // Act
    var hash1 = message1.GetHashCode();
    var hash2 = message2.GetHashCode();

    // Assert
    await Assert.That(hash1).IsNotEqualTo(hash2);
  }

  [Test]
  public async Task OutboxMessage_SupportsNullScope_ShouldAllowNullAsync() {
    // Arrange & Act
    var message = new OutboxMessage(
      MessageId.New(),
      "test-destination",
      "TestEvent",
      "{\"value\": 123}",
      "{\"hops\": []}",
      null,
      DateTimeOffset.UtcNow
    );

    // Assert
    await Assert.That(message.Scope).IsNull();
  }

  private static OutboxMessage CreateTestMessage() {
    return new OutboxMessage(
      MessageId.New(),
      $"destination-{Guid.NewGuid()}",
      "TestEvent",
      "{\"value\": 123}",
      "{\"hops\": []}",
      null,
      DateTimeOffset.UtcNow
    );
  }
}
