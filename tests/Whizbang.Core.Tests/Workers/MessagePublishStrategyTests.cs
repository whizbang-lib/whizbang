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
    await Assert.That(parameters).Count().IsEqualTo(2);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(OutboxWork));
    await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
  }

  [Test]
  public async Task IMessagePublishStrategy_DefaultIsReadyAsync_ShouldReturnTrueAsync() {
    // Arrange
    IMessagePublishStrategy strategy = new DefaultPublishStrategy();

    // Act
    var isReady = await strategy.IsReadyAsync();

    // Assert - Default implementation should always return true
    await Assert.That(isReady).IsTrue()
      .Because("Default IsReadyAsync should return true for strategies without transport dependencies");
  }

  [Test]
  public async Task IMessagePublishStrategy_CustomIsReadyAsync_CanBeOverriddenAsync() {
    // Arrange
    var strategy = new ReadinessAwarePublishStrategy(isReady: false);

    // Act
    var isReady = await strategy.IsReadyAsync();

    // Assert - Custom implementation can override default behavior
    await Assert.That(isReady).IsFalse()
      .Because("Custom IsReadyAsync implementation should be able to return false");
  }

  [Test]
  public async Task IMessagePublishStrategy_IsReadyAsync_RespectsCancellationTokenAsync() {
    // Arrange
    IMessagePublishStrategy strategy = new DefaultPublishStrategy();
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await strategy.IsReadyAsync(cts.Token)
    );
  }

  // ========================================
  // Test Implementations
  // ========================================

  private sealed class DefaultPublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(true);
    }

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published
      });
    }
  }

  private sealed class ReadinessAwarePublishStrategy : IMessagePublishStrategy {
    private readonly bool _isReady;

    public ReadinessAwarePublishStrategy(bool isReady) {
      _isReady = isReady;
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(_isReady);
    }

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = _isReady,
        CompletedStatus = _isReady ? MessageProcessingStatus.Published : MessageProcessingStatus.Stored,
        Error = _isReady ? null : "Transport not ready"
      });
    }
  }
}
