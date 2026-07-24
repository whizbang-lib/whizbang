using Whizbang.Core.Validation;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// Tests for StreamIdGuard - fail-fast validation guards for StreamId values.
/// </summary>
public class StreamIdGuardTests {
  [Test]
  public async Task ThrowIfEmpty_WithGuidEmpty_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange
    var messageId = Guid.NewGuid();

    // Act & Assert
    await Assert.That(() => StreamIdGuard.ThrowIfEmpty(Guid.Empty, messageId, "Dispatcher.Outbox"))
        .Throws<InvalidStreamIdException>();
  }

  [Test]
  public async Task ThrowIfEmpty_WithValidGuid_DoesNotThrowAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    // Act & Assert - should not throw
    void action() => StreamIdGuard.ThrowIfEmpty(streamId, messageId, "Dispatcher.Outbox");
    await Assert.That(action).ThrowsNothing();
  }

  [Test]
  public async Task ThrowIfEmpty_CapturesCallerInfoAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    InvalidStreamIdException? caught = null;

    // Act
    try {
      StreamIdGuard.ThrowIfEmpty(Guid.Empty, messageId, "Dispatcher.Outbox");
    } catch (InvalidStreamIdException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.CallerMemberName).IsNotEqualTo(string.Empty);
    await Assert.That(caught.CallerFilePath).Contains("StreamIdGuardTests.cs");
    await Assert.That(caught.CallerLineNumber).IsGreaterThan(0);
  }

  [Test]
  public async Task ThrowIfEmpty_IncludesMessageIdAndContextInMessageAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    InvalidStreamIdException? caught = null;

    // Act
    try {
      StreamIdGuard.ThrowIfEmpty(Guid.Empty, messageId, "Dispatcher.Outbox", "OrderCreatedEvent");
    } catch (InvalidStreamIdException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains(messageId.ToString());
    await Assert.That(caught.Message).Contains("Dispatcher.Outbox");
    await Assert.That(caught.Message).Contains("OrderCreatedEvent");
    await Assert.That(caught.MessageId).IsEqualTo(messageId);
    await Assert.That(caught.Context).IsEqualTo("Dispatcher.Outbox");
    await Assert.That(caught.StreamId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task ThrowIfNonNullEmpty_WithNull_DoesNotThrowAsync() {
    // Arrange
    var messageId = Guid.NewGuid();

    // Act & Assert - null StreamId is valid (no stream concept)
    void action() => StreamIdGuard.ThrowIfNonNullEmpty(null, messageId, "WorkCoordinator.QueueOutbox");
    await Assert.That(action).ThrowsNothing();
  }

  [Test]
  public async Task ThrowIfNonNullEmpty_WithGuidEmpty_ThrowsAsync() {
    // Arrange
    var messageId = Guid.NewGuid();

    // Act & Assert
    await Assert.That(() => StreamIdGuard.ThrowIfNonNullEmpty(Guid.Empty, messageId, "WorkCoordinator.QueueOutbox"))
        .Throws<InvalidStreamIdException>();
  }

  [Test]
  public async Task ThrowIfNonNullEmpty_WithValidGuid_DoesNotThrowAsync() {
    // Arrange
    var streamId = (Guid?)Guid.NewGuid();
    var messageId = Guid.NewGuid();

    // Act & Assert - should not throw
    void action() => StreamIdGuard.ThrowIfNonNullEmpty(streamId, messageId, "WorkCoordinator.QueueOutbox");
    await Assert.That(action).ThrowsNothing();
  }
}
