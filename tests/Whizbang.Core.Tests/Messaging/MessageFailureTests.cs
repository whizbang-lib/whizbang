using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

public class MessageFailureTests {
  [Test]
  public async Task MessageFailure_WithReason_StoresReasonAsync() {
    // Arrange & Act
    var failure = new MessageFailure {
      MessageId = Guid.NewGuid(),
      CompletedStatus = MessageProcessingStatus.Stored,
      Error = "Transport not ready",
      Reason = MessageFailureReason.TransportNotReady
    };

    // Assert
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.TransportNotReady);
  }

  [Test]
  public async Task MessageFailure_WithoutReason_DefaultsToUnknownAsync() {
    // Arrange & Act
    var failure = new MessageFailure {
      MessageId = Guid.NewGuid(),
      CompletedStatus = MessageProcessingStatus.Stored,
      Error = "Some error"
    };

    // Assert
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.Unknown);
  }

  [Test]
  public async Task MessageFailure_AllReasonTypes_CanBeAssignedAsync() {
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
      var failure = new MessageFailure {
        MessageId = messageId,
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = $"Error: {reason}",
        Reason = reason
      };

      await Assert.That(failure.Reason).IsEqualTo(reason);
    }
  }
}
