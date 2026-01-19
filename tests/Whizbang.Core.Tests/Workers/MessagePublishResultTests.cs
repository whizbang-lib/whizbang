using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

public class MessagePublishResultTests {
  [Test]
  public async Task MessagePublishResult_Success_HasReasonNoneAsync() {
    // Arrange & Act
    var result = new MessagePublishResult {
      MessageId = Guid.NewGuid(),
      Success = true,
      CompletedStatus = MessageProcessingStatus.Published,
      Reason = MessageFailureReason.None
    };

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.None);
  }

  [Test]
  public async Task MessagePublishResult_Failure_HasReasonAsync() {
    // Arrange & Act
    var result = new MessagePublishResult {
      MessageId = Guid.NewGuid(),
      Success = false,
      CompletedStatus = MessageProcessingStatus.Stored,
      Error = "Transport not ready",
      Reason = MessageFailureReason.TransportNotReady
    };

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.TransportNotReady);
  }

  [Test]
  public async Task MessagePublishResult_WithoutReason_DefaultsToUnknownAsync() {
    // Arrange & Act
    var result = new MessagePublishResult {
      MessageId = Guid.NewGuid(),
      Success = false,
      CompletedStatus = MessageProcessingStatus.Stored,
      Error = "Some error"
    };

    // Assert
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.Unknown);
  }

  [Test]
  public async Task MessagePublishResult_AllReasonTypes_CanBeAssignedAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var reasons = new[] {
      MessageFailureReason.None,
      MessageFailureReason.TransportNotReady,
      MessageFailureReason.TransportException,
      MessageFailureReason.SerializationError,
      MessageFailureReason.ValidationError,
      MessageFailureReason.MaxAttemptsExceeded,
      MessageFailureReason.LeaseExpired,
      MessageFailureReason.Unknown
    };

    // Act & Assert
    foreach (var reason in reasons) {
      var result = new MessagePublishResult {
        MessageId = messageId,
        Success = reason == MessageFailureReason.None,
        CompletedStatus = reason == MessageFailureReason.None
          ? MessageProcessingStatus.Published
          : MessageProcessingStatus.Stored,
        Error = reason == MessageFailureReason.None ? null : $"Error: {reason}",
        Reason = reason
      };

      await Assert.That(result.Reason).IsEqualTo(reason);
    }
  }
}
