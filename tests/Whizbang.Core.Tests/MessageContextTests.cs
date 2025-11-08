using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for MessageContext implementation.
/// Ensures all creation patterns and property initializers are properly tested.
/// </summary>
public class MessageContextTests {
  [Test]
  public async Task DefaultConstructor_InitializesRequiredProperties_AutomaticallyAsync() {
    // Arrange & Act
    var context = new MessageContext();

    // Assert - Default initialization creates new MessageId
    await Assert.That(context.MessageId.Value).IsNotEqualTo(Guid.Empty);

    // Timestamp should be recent (within 1 second of now)
    var now = DateTimeOffset.UtcNow;
    var diff = (now - context.Timestamp).TotalSeconds;
    await Assert.That(Math.Abs(diff)).IsLessThan(1.0);

    // Note: CorrelationId and CausationId do NOT have default initializers
    // They remain uninitialized when using default constructor
    // Use MessageContext.New() or MessageContext.Create() for full initialization
  }

  [Test]
  public async Task Create_WithCorrelationId_GeneratesNewMessageIdAndCausationIdAsync() {
    // Arrange
    var correlationId = CorrelationId.New();

    // Act
    var context = MessageContext.Create(correlationId);

    // Assert
    await Assert.That(context.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(context.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(context.CausationId.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task Create_WithCorrelationIdAndCausationId_UsesProvidedCausationIdAsync() {
    // Arrange
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    // Act
    var context = MessageContext.Create(correlationId, causationId);

    // Assert
    await Assert.That(context.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(context.CausationId).IsEqualTo(causationId);
    await Assert.That(context.MessageId.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task New_GeneratesAllNewIdentifiersAsync() {
    // Arrange & Act
    var context = MessageContext.New();

    // Assert
    await Assert.That(context.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(context.CorrelationId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(context.CausationId.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task New_GeneratesUniqueMessageIds_AcrossMultipleCallsAsync() {
    // Arrange & Act
    var context1 = MessageContext.New();
    var context2 = MessageContext.New();

    // Assert
    await Assert.That(context1.MessageId).IsNotEqualTo(context2.MessageId);
  }

  [Test]
  public async Task Metadata_IsEmptyByDefaultAsync() {
    // Arrange & Act
    var context = new MessageContext();

    // Assert
    await Assert.That(context.Metadata).IsNotNull();
    await Assert.That(context.Metadata.Count).IsEqualTo(0);
  }

  [Test]
  public async Task UserId_IsNullByDefaultAsync() {
    // Arrange & Act
    var context = new MessageContext();

    // Assert
    await Assert.That(context.UserId).IsNull();
  }

  [Test]
  public async Task Properties_CanBeSetViaInitializer_WithInitSyntaxAsync() {
    // Arrange
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var timestamp = DateTimeOffset.UtcNow.AddHours(-1);
    var userId = "user123";

    // Act
    var context = new MessageContext {
      MessageId = messageId,
      CorrelationId = correlationId,
      CausationId = causationId,
      Timestamp = timestamp,
      UserId = userId
    };

    // Assert
    await Assert.That(context.MessageId).IsEqualTo(messageId);
    await Assert.That(context.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(context.CausationId).IsEqualTo(causationId);
    await Assert.That(context.Timestamp).IsEqualTo(timestamp);
    await Assert.That(context.UserId).IsEqualTo(userId);
  }
}
