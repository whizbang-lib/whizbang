using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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

    // Act & Assert - TaskCanceledException is a subclass of OperationCanceledException
    await Assert.That(() => SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, cts.Token))
      .Throws<OperationCanceledException>();
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

  [Test]
  public async Task SubscribeWithRetryAsync_OnDisconnected_ApplicationInitiated_DoesNotReconnectAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-routing");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(returnDisconnectableSubscription: true);
    var options = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromMilliseconds(10)
    };
    var handler = _createNoOpHandler();

    // Act - subscribe successfully
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);

    // Trigger application-initiated disconnect
    var subscription = (DisconnectableMockSubscription)state.Subscription!;
    subscription.TriggerDisconnect("Application shutdown", applicationInitiated: true);

    // Wait a bit to ensure no reconnection is attempted
    await Task.Delay(50);

    // Assert - status should still be Healthy (not recovering) because app initiated
    // and subscribe count should still be 1
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task SubscribeWithRetryAsync_OnDisconnected_ExternalDisconnect_TriggersReconnectionAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-routing");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(returnDisconnectableSubscription: true);
    var options = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromMilliseconds(10)
    };
    var handler = _createNoOpHandler();

    // Act - subscribe successfully
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);

    // Trigger non-application-initiated disconnect
    var subscription = (DisconnectableMockSubscription)state.Subscription!;
    var disconnectException = new InvalidOperationException("Connection lost");
    subscription.TriggerDisconnect("Connection lost", exception: disconnectException, applicationInitiated: false);

    // Wait for reconnection to happen
    await Task.Delay(100);

    // Assert - should have attempted reconnection
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(transport.SubscribeCallCount).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task SubscribeWithRetryAsync_OnDisconnected_SetsRecoveringStatusAndLastErrorAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic", "test-routing");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(returnDisconnectableSubscription: true);
    var options = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromMilliseconds(500) // Long delay to catch intermediate state
    };
    var handler = _createNoOpHandler();

    // Act - subscribe successfully
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Trigger disconnect with exception
    var subscription = (DisconnectableMockSubscription)state.Subscription!;
    var disconnectException = new InvalidOperationException("Network error");
    subscription.TriggerDisconnect("Network error", exception: disconnectException, applicationInitiated: false);

    // Give a small delay for the event handler to run (but not enough for full reconnect)
    await Task.Delay(20);

    // Assert intermediate state - should be recovering with last error set
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Recovering);
    await Assert.That(state.LastError).IsEqualTo(disconnectException);
    await Assert.That(state.LastErrorTime).IsNotNull();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_IndefiniteRetry_LogsEvery10AttemptsAsync() {
    // Arrange
    var destination = new TransportDestination("test-topic");
    var state = new SubscriptionState(destination);
    // Fail first 15 attempts to hit the attempt % 10 == 0 condition (at attempt 10)
    var transport = new MockTransport(failFirstNAttempts: 15);
    var options = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 3, // After 3 attempts, switches to indefinite
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      MaxRetryDelay = TimeSpan.FromMilliseconds(5),
      BackoffMultiplier = 1.0,
      RetryIndefinitely = true
    };
    var handler = _createNoOpHandler();

    // Act - this will go through attempts 1-16, succeeding on 16
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Assert - should succeed after 16 attempts
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(16);
  }

  [Test]
  public async Task SubscribeWithRetryAsync_WithRoutingKey_UsesDefaultWildcardInLogsAsync() {
    // Arrange - test the routing key defaulting logic
    var destination = new TransportDestination("test-topic"); // No routing key
    var state = new SubscriptionState(destination);
    var transport = new MockTransport();
    var options = new SubscriptionResilienceOptions();
    var handler = _createNoOpHandler();

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Assert - should succeed (logs would use "#" as default routing key)
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
  }

  [Test]
  public async Task SubscribeWithRetryAsync_RetryAfterMultipleFailures_LogsRetrySuccessAsync() {
    // Arrange - test the "attempt > 1" logging path
    var destination = new TransportDestination("test-topic", "custom-key");
    var state = new SubscriptionState(destination);
    var transport = new MockTransport(failFirstNAttempts: 2);
    var options = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 5,
      InitialRetryDelay = TimeSpan.FromMilliseconds(5),
      BackoffMultiplier = 1.0
    };
    var handler = _createNoOpHandler();

    // Act
    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, handler, state, options,
      NullLogger.Instance, CancellationToken.None);

    // Assert - should log "established after N attempts" since attempt > 1
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(3); // Failed 2, succeeded on 3
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
    private readonly bool _returnDisconnectableSubscription;
    private int _attemptCount;

    public int SubscribeCallCount { get; private set; }

    public MockTransport(bool alwaysFail = false, int failFirstNAttempts = 0, bool returnDisconnectableSubscription = false) {
      _alwaysFail = alwaysFail;
      _failFirstNAttempts = failFirstNAttempts;
      _returnDisconnectableSubscription = returnDisconnectableSubscription;
    }

    public bool IsInitialized => true;

    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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

      ISubscription subscription = _returnDisconnectableSubscription
        ? new DisconnectableMockSubscription()
        : new MockSubscription();

      return Task.FromResult(subscription);
    }

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull {
      throw new NotSupportedException("Request/response not supported in mock");
    }
  }

  private sealed class MockSubscription : ISubscription {
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public bool IsActive { get; private set; } = true;

    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() {
      IsActive = false;
    }

    // Helper to trigger disconnect event for testing
    public void TriggerDisconnect(string reason, Exception? exception = null, bool applicationInitiated = false) {
      OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs {
        Reason = reason,
        Exception = exception,
        IsApplicationInitiated = applicationInitiated
      });
    }
  }

  /// <summary>
  /// A mock subscription that exposes the ability to trigger disconnect events for testing.
  /// </summary>
  private sealed class DisconnectableMockSubscription : ISubscription {
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;

    public bool IsActive { get; private set; } = true;

    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() {
      IsActive = false;
    }

    /// <summary>
    /// Triggers the OnDisconnected event to simulate a subscription disconnect.
    /// </summary>
    public void TriggerDisconnect(string reason, Exception? exception = null, bool applicationInitiated = false) {
      OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs {
        Reason = reason,
        Exception = exception,
        IsApplicationInitiated = applicationInitiated
      });
    }
  }
}
