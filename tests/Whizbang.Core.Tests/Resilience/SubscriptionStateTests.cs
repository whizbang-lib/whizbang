using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Tests for <see cref="SubscriptionState"/> to verify status transitions and state tracking.
/// </summary>
/// <tests>src/Whizbang.Core/Resilience/SubscriptionState.cs</tests>
public class SubscriptionStateTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithDestination_InitializesWithPendingStatusAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");

    // Act
    var state = new SubscriptionState(destination);

    // Assert
    await Assert.That(state.Destination).IsEqualTo(destination);
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Pending);
  }

  [Test]
  public async Task Constructor_WithNullDestination_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new SubscriptionState(null!);
      await Task.CompletedTask;
    });
  }

  #endregion

  #region Status Transition Tests

  [Test]
  public async Task Status_SetToRecovering_UpdatesStatusAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination) {
      // Act
      Status = SubscriptionStatus.Recovering
    };

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Recovering);
  }

  [Test]
  public async Task Status_SetToHealthy_UpdatesStatusAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination) {
      // Act
      Status = SubscriptionStatus.Healthy
    };

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
  }

  [Test]
  public async Task Status_SetToFailed_UpdatesStatusAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination) {
      // Act
      Status = SubscriptionStatus.Failed
    };

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Failed);
  }

  #endregion

  #region Attempt Tracking Tests

  [Test]
  public async Task AttemptCount_InitialValue_IsZeroAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);

    // Assert
    await Assert.That(state.AttemptCount).IsEqualTo(0);
  }

  [Test]
  public async Task AttemptCount_SetValue_ReturnsSetValueAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination) {
      // Act
      AttemptCount = 5
    };

    // Assert
    await Assert.That(state.AttemptCount).IsEqualTo(5);
  }

  [Test]
  public async Task IncrementAttempt_IncrementsCountByOneAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);

    // Act
    state.IncrementAttempt();
    state.IncrementAttempt();
    state.IncrementAttempt();

    // Assert
    await Assert.That(state.AttemptCount).IsEqualTo(3);
  }

  [Test]
  public async Task ResetAttempts_ResetsCountToZeroAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination) {
      AttemptCount = 10
    };

    // Act
    state.ResetAttempts();

    // Assert
    await Assert.That(state.AttemptCount).IsEqualTo(0);
  }

  #endregion

  #region Error Tracking Tests

  [Test]
  public async Task LastError_InitialValue_IsNullAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);

    // Assert
    await Assert.That(state.LastError).IsNull();
  }

  [Test]
  public async Task LastError_SetException_ReturnsExceptionAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);
    var exception = new InvalidOperationException("Test error");

    // Act
    state.LastError = exception;

    // Assert
    await Assert.That(state.LastError).IsEqualTo(exception);
  }

  [Test]
  public async Task LastErrorTime_InitialValue_IsNullAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);

    // Assert
    await Assert.That(state.LastErrorTime).IsNull();
  }

  [Test]
  public async Task LastErrorTime_SetValue_ReturnsValueAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);
    var errorTime = DateTimeOffset.UtcNow;

    // Act
    state.LastErrorTime = errorTime;

    // Assert
    await Assert.That(state.LastErrorTime).IsEqualTo(errorTime);
  }

  #endregion

  #region Subscription Reference Tests

  [Test]
  public async Task Subscription_InitialValue_IsNullAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);

    // Assert
    await Assert.That(state.Subscription).IsNull();
  }

  [Test]
  public async Task Subscription_SetValue_ReturnsValueAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-subscription");
    var state = new SubscriptionState(destination);
    var subscription = new TestSubscription();

    // Act
    state.Subscription = subscription;

    // Assert
    await Assert.That(state.Subscription).IsEqualTo(subscription);
  }

  #endregion

  #region SubscriptionStatus Enum Tests

  [Test]
  public async Task SubscriptionStatus_HasExpectedValuesAsync() {
    // Arrange - get all defined values
    var definedValues = Enum.GetValues<SubscriptionStatus>();

    // Assert - should have exactly 4 values
    await Assert.That(definedValues.Length).IsEqualTo(4);
    await Assert.That(definedValues).Contains(SubscriptionStatus.Pending);
    await Assert.That(definedValues).Contains(SubscriptionStatus.Recovering);
    await Assert.That(definedValues).Contains(SubscriptionStatus.Healthy);
    await Assert.That(definedValues).Contains(SubscriptionStatus.Failed);
  }

  [Test]
  public async Task SubscriptionStatus_DefaultValue_IsPendingAsync() {
    // Arrange - default value of enum should be Pending (0)
    var defaultStatus = default(SubscriptionStatus);

    // Assert
    await Assert.That(defaultStatus).IsEqualTo(SubscriptionStatus.Pending);
  }

  #endregion

  #region Test Helpers

  private sealed class TestSubscription : ISubscription {
    public bool IsActive => true;

#pragma warning disable CS0067 // Event is required by interface but not used in test
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }

  #endregion
}
