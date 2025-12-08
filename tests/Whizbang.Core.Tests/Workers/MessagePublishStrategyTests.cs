using System;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

public class MessagePublishStrategyTests {
  [Test]
  public async Task MessagePublishResult_Success_ShouldHaveCorrectPropertiesAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var completedStatus = MessageProcessingStatus.Published;

    // Act
    var result = new MessagePublishResult {
      MessageId = messageId,
      Success = true,
      CompletedStatus = completedStatus,
      Error = null
    };

    // Assert
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.CompletedStatus).IsEqualTo(completedStatus);
    await Assert.That(result.Error).IsNull();
  }

  [Test]
  public async Task MessagePublishResult_Failure_ShouldHaveErrorMessageAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var completedStatus = MessageProcessingStatus.Stored; // Partially completed
    var errorMessage = "Transport publish failed";

    // Act
    var result = new MessagePublishResult {
      MessageId = messageId,
      Success = false,
      CompletedStatus = completedStatus,
      Error = errorMessage
    };

    // Assert
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.CompletedStatus).IsEqualTo(completedStatus);
    await Assert.That(result.Error).IsEqualTo(errorMessage);
  }

  [Test]
  public async Task IMessagePublishStrategy_Interface_ShouldHavePublishAsyncMethodAsync() {
    // Arrange - This is a compile-time test, but we verify the interface contract
    var strategyType = typeof(IMessagePublishStrategy);

    // Act
    var method = strategyType.GetMethod(nameof(IMessagePublishStrategy.PublishAsync));

    // Assert
    await Assert.That(method).IsNotNull();
    await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task<MessagePublishResult>));

    var parameters = method.GetParameters();
    await Assert.That(parameters).HasCount().EqualTo(2);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(OutboxWork));
    await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
  }
}
