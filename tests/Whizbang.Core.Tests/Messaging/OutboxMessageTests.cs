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
    var payload = new byte[] { 1, 2, 3, 4, 5 };
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, payload, createdAt);
    var message2 = new OutboxMessage(messageId, destination, payload, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentMessageId_ShouldReturnFalseAsync() {
    // Arrange
    var destination = "test-destination";
    var payload = new byte[] { 1, 2, 3, 4, 5 };
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(MessageId.New(), destination, payload, createdAt);
    var message2 = new OutboxMessage(MessageId.New(), destination, payload, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentDestination_ShouldReturnFalseAsync() {
    // Arrange
    var messageId = MessageId.New();
    var payload = new byte[] { 1, 2, 3, 4, 5 };
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, "destination1", payload, createdAt);
    var message2 = new OutboxMessage(messageId, "destination2", payload, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentPayload_ShouldReturnFalseAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, new byte[] { 1, 2, 3 }, createdAt);
    var message2 = new OutboxMessage(messageId, destination, new byte[] { 4, 5, 6 }, createdAt);

    // Act
    var result = message1.Equals(message2);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task OutboxMessage_Equals_WithDifferentPayloadLength_ShouldReturnFalseAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, new byte[] { 1, 2, 3 }, createdAt);
    var message2 = new OutboxMessage(messageId, destination, new byte[] { 1, 2, 3, 4 }, createdAt);

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
    var payload = new byte[] { 1, 2, 3, 4, 5 };

    var message1 = new OutboxMessage(messageId, destination, payload, DateTimeOffset.UtcNow);
    await Task.Delay(10); // Ensure different timestamps
    var message2 = new OutboxMessage(messageId, destination, payload, DateTimeOffset.UtcNow);

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
    var payload = new byte[] { 1, 2, 3, 4, 5 };
    var createdAt = DateTimeOffset.UtcNow;

    var message1 = new OutboxMessage(messageId, destination, payload, createdAt);
    var message2 = new OutboxMessage(messageId, destination, payload, createdAt);

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
  public async Task OutboxMessage_GetHashCode_UsesPayloadLength_ForPerformanceAsync() {
    // Arrange
    var messageId = MessageId.New();
    var destination = "test-destination";
    var createdAt = DateTimeOffset.UtcNow;

    // Same length, different contents
    var message1 = new OutboxMessage(messageId, destination, new byte[] { 1, 2, 3, 4, 5 }, createdAt);
    var message2 = new OutboxMessage(messageId, destination, new byte[] { 5, 4, 3, 2, 1 }, createdAt);

    // Act
    var hash1 = message1.GetHashCode();
    var hash2 = message2.GetHashCode();

    // Assert - Same hash because same length is used for performance
    await Assert.That(hash1).IsEqualTo(hash2);
  }

  private static OutboxMessage CreateTestMessage() {
    return new OutboxMessage(
      MessageId.New(),
      $"destination-{Guid.NewGuid()}",
      new byte[] { 1, 2, 3, 4, 5 },
      DateTimeOffset.UtcNow
    );
  }
}
