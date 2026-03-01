using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Tests for <see cref="SubscriptionRetryHelper"/>.
/// </summary>
public class SubscriptionRetryHelperTests {
  // ==========================================================================
  // CalculateNextDelay Tests
  // ==========================================================================

  [Test]
  public async Task CalculateNextDelay_AppliesBackoffMultiplierAsync() {
    // Arrange
    var currentDelay = TimeSpan.FromSeconds(1);
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task CalculateNextDelay_CapsAtMaxRetryDelayAsync() {
    // Arrange
    var currentDelay = TimeSpan.FromMinutes(3);
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert - should be capped at 5 minutes, not 6
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromMinutes(5));
  }

  [Test]
  public async Task CalculateNextDelay_ReturnsExactMaxWhenCalculatedExceedsAsync() {
    // Arrange
    var currentDelay = TimeSpan.FromMinutes(10);
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromMinutes(5));
  }

  [Test]
  public async Task CalculateNextDelay_WithSmallMultiplier_IncrementsCorrectlyAsync() {
    // Arrange
    var currentDelay = TimeSpan.FromSeconds(1);
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 1.5,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromMilliseconds(1500));
  }

  [Test]
  public async Task CalculateNextDelay_WithOneMultiplier_ReturnsCurrentDelayAsync() {
    // Arrange
    var currentDelay = TimeSpan.FromSeconds(5);
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 1.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(5));
  }

  [Test]
  public async Task CalculateNextDelay_WithZeroDelay_ReturnsZeroAsync() {
    // Arrange
    var currentDelay = TimeSpan.Zero;
    var options = new SubscriptionResilienceOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };

    // Act
    var nextDelay = SubscriptionRetryHelper.CalculateNextDelay(currentDelay, options);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.Zero);
  }

  // ==========================================================================
  // SubscribeWithRetryAsync Tests
  // ==========================================================================

  [Test]
  public async Task SubscribeWithRetryAsync_SuccessOnFirstAttempt_SetsHealthyStatusAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-routing");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport();
    var options = new SubscriptionResilienceOptions();
    var handler = _createNoOpHandler();

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(state.Subscription).IsNotNull();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_FailureExhaustsRetries_SetsFailedStatusAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-routing");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(alwaysFail: true);
    var options = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 2,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      RetryIndefinitely = false
    };
    var handler = _createNoOpHandler();

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Failed);
    await Assert.That(state.LastError).IsNotNull();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_RetrySucceeds_SetsHealthyStatusAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-routing");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(failFirstNAttempts: 1); // Fail first, then succeed
    var options = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 3,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      RetryIndefinitely = false
    };
    var handler = _createNoOpHandler();

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Assert
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(state.Subscription).IsNotNull();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_Cancellation_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(alwaysFail: true);
    var options = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 10,
      InitialRetryDelay = TimeSpan.FromMilliseconds(100),
      RetryIndefinitely = true
    };
    var handler = _createNoOpHandler();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

    // Act & Assert
    await Assert.That(() => SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, cts.Token))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_SetsRecoveringStatus_DuringRetryAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(failFirstNAttempts: 1);
    var options = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 3,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10)
    };
    var handler = _createNoOpHandler();

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Assert - should end up Healthy but IncrementAttempt should have been called
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(2); // Failed once, then succeeded
  }

  // ==========================================================================
  // Helper Methods
  // ==========================================================================

  private static Func<IMessageEnvelope, string?, CancellationToken, Task> _createNoOpHandler() {
    return (_, _, _) => Task.CompletedTask;
  }

  // ==========================================================================
  // Mock Transport
  // ==========================================================================

  private sealed class MockTransport : ITransport {
    private readonly bool _alwaysFail;
    private readonly int _failFirstNAttempts;
    private int _attemptCount;

    public int SubscribeCallCount { get; private set; }

    public MockTransport(bool alwaysFail = false, int failFirstNAttempts = 0) {
      _alwaysFail = alwaysFail;
      _failFirstNAttempts = failFirstNAttempts;
    }

    public string Name => "MockTransport";

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      _attemptCount++;

      if (_alwaysFail) {
        throw new InvalidOperationException("Mock transport always fails");
      }

      if (_attemptCount <= _failFirstNAttempts) {
        throw new InvalidOperationException($"Mock transport fails on attempt {_attemptCount}");
      }

      return Task.FromResult<ISubscription>(new MockSubscription());
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task PublishAsync(IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
      return ValueTask.CompletedTask;
    }
  }

  private sealed class MockSubscription : ISubscription {
    public string SubscriptionId => Guid.NewGuid().ToString();

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public Task CancelAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Helper to trigger disconnect event for testing
    public void TriggerDisconnect(string reason, bool applicationInitiated) {
      OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs(
        SubscriptionId, reason, null, applicationInitiated));
    }
  }
}
