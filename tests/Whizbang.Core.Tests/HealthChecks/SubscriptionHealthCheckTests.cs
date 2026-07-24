using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.HealthChecks;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.HealthChecks;

/// <summary>
/// Tests for <see cref="SubscriptionHealthCheck"/>.
/// </summary>
public class SubscriptionHealthCheckTests {
  [Test]
  public async Task CheckHealthAsync_AllHealthy_ReturnsHealthyAsync() {
    // Arrange
    var states = new Dictionary<TransportDestination, SubscriptionState> {
      [new TransportDestination("queue-1")] = new(new TransportDestination("queue-1")) { Status = SubscriptionStatus.Healthy },
      [new TransportDestination("queue-2")] = new(new TransportDestination("queue-2")) { Status = SubscriptionStatus.Healthy }
    };
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    await Assert.That(result.Description).Contains("2/2");
  }

  [Test]
  public async Task CheckHealthAsync_AllFailed_ReturnsUnhealthyAsync() {
    // Arrange
    var states = new Dictionary<TransportDestination, SubscriptionState> {
      [new TransportDestination("queue-1")] = new(new TransportDestination("queue-1")) { Status = SubscriptionStatus.Failed },
      [new TransportDestination("queue-2")] = new(new TransportDestination("queue-2")) { Status = SubscriptionStatus.Failed }
    };
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(result.Description).Contains("0/2");
  }

  [Test]
  public async Task CheckHealthAsync_SomeRecovering_ReturnsDegradedAsync() {
    // Arrange
    var states = new Dictionary<TransportDestination, SubscriptionState> {
      [new TransportDestination("queue-1")] = new(new TransportDestination("queue-1")) { Status = SubscriptionStatus.Healthy },
      [new TransportDestination("queue-2")] = new(new TransportDestination("queue-2")) { Status = SubscriptionStatus.Recovering }
    };
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    await Assert.That(result.Description).Contains("1/2");
  }

  [Test]
  public async Task CheckHealthAsync_SomeFailed_ReturnsDegradedAsync() {
    // Arrange
    var states = new Dictionary<TransportDestination, SubscriptionState> {
      [new TransportDestination("queue-1")] = new(new TransportDestination("queue-1")) { Status = SubscriptionStatus.Healthy },
      [new TransportDestination("queue-2")] = new(new TransportDestination("queue-2")) { Status = SubscriptionStatus.Failed }
    };
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    await Assert.That(result.Description).Contains("1/2");
  }

  [Test]
  public async Task CheckHealthAsync_NoSubscriptions_ReturnsHealthyAsync() {
    // Arrange
    var states = new Dictionary<TransportDestination, SubscriptionState>();
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    await Assert.That(result.Description).Contains("No subscriptions");
  }

  [Test]
  public async Task CheckHealthAsync_AllPending_ReturnsDegradedAsync() {
    // Arrange
    var states = new Dictionary<TransportDestination, SubscriptionState> {
      [new TransportDestination("queue-1")] = new(new TransportDestination("queue-1")) { Status = SubscriptionStatus.Pending },
      [new TransportDestination("queue-2")] = new(new TransportDestination("queue-2")) { Status = SubscriptionStatus.Pending }
    };
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    await Assert.That(result.Description).Contains("0/2");
  }

  [Test]
  public async Task CheckHealthAsync_IncludesFailedDestinationsInDataAsync() {
    // Arrange
    var failedDest = new TransportDestination("failed-queue");
    var states = new Dictionary<TransportDestination, SubscriptionState> {
      [new TransportDestination("healthy-queue")] = new(new TransportDestination("healthy-queue")) { Status = SubscriptionStatus.Healthy },
      [failedDest] = new(failedDest) {
        Status = SubscriptionStatus.Failed,
        LastError = new InvalidOperationException("Connection failed"),
        LastErrorTime = DateTimeOffset.UtcNow
      }
    };
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Data).ContainsKey("failed_destinations");
    var failedDestinations = result.Data["failed_destinations"] as IReadOnlyList<string>;
    await Assert.That(failedDestinations).IsNotNull();
    await Assert.That(failedDestinations).Contains("failed-queue");
  }

  [Test]
  public async Task CheckHealthAsync_IncludesRecoveringDestinationsInDataAsync() {
    // Arrange
    var recoveringDest = new TransportDestination("recovering-queue");
    var states = new Dictionary<TransportDestination, SubscriptionState> {
      [new TransportDestination("healthy-queue")] = new(new TransportDestination("healthy-queue")) { Status = SubscriptionStatus.Healthy },
      [recoveringDest] = new(recoveringDest) {
        Status = SubscriptionStatus.Recovering,
        AttemptCount = 3
      }
    };
    var healthCheck = new SubscriptionHealthCheck(states);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Data).ContainsKey("recovering_destinations");
    var recoveringDestinations = result.Data["recovering_destinations"] as IReadOnlyList<string>;
    await Assert.That(recoveringDestinations).IsNotNull();
    await Assert.That(recoveringDestinations).Contains("recovering-queue");
  }

  [Test]
  public async Task Constructor_NullStates_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    var ex = Assert.Throws<ArgumentNullException>(() => _ = new SubscriptionHealthCheck(null!));
    await Assert.That(ex).IsNotNull();
  }
}
