using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Tests for AzureServiceBusConnectionRetry.
/// Verifies retry logic, exponential backoff, and error handling.
/// </summary>
public class AzureServiceBusConnectionRetryTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new AzureServiceBusConnectionRetry(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithValidOptions_CreatesInstanceAsync() {
    // Arrange
    var options = new AzureServiceBusOptions();

    // Act
    var retry = new AzureServiceBusConnectionRetry(options);

    // Assert
    await Assert.That(retry).IsNotNull();
  }

  #endregion

  #region CalculateNextDelay Tests

  [Test]
  public async Task CalculateNextDelay_WithDefaultMultiplier_DoublesDelayAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };
    var retry = new AzureServiceBusConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(1);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task CalculateNextDelay_WithCustomMultiplier_AppliesMultiplierAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      BackoffMultiplier = 3.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };
    var retry = new AzureServiceBusConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(2);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(6));
  }

  [Test]
  public async Task CalculateNextDelay_WhenExceedsMaxDelay_CapsAtMaxDelayAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(30)
    };
    var retry = new AzureServiceBusConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(20);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert - Would be 40 seconds, but capped at 30
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task CalculateNextDelay_WhenBelowMaxDelay_ReturnsCalculatedValueAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(30)
    };
    var retry = new AzureServiceBusConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(10);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(20));
  }

  [Test]
  public async Task CalculateNextDelay_WithMultiplierLessThanOne_DecreasesDelayAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      BackoffMultiplier = 0.5,
      MaxRetryDelay = TimeSpan.FromSeconds(30)
    };
    var retry = new AzureServiceBusConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(10);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(5));
  }

  #endregion

  #region CreateClientWithRetryAsync Tests

  [Test]
  public async Task CreateClientWithRetryAsync_WithNullConnectionString_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = new AzureServiceBusOptions();
    var retry = new AzureServiceBusConnectionRetry(options);

    // Act & Assert
    await Assert.That(async () => await retry.CreateClientWithRetryAsync(null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task CreateClientWithRetryAsync_WithEmptyConnectionString_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = new AzureServiceBusOptions();
    var retry = new AzureServiceBusConnectionRetry(options);

    // Act & Assert
    await Assert.That(async () => await retry.CreateClientWithRetryAsync(""))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task CreateClientWithRetryAsync_WhenCancelled_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      InitialRetryAttempts = 5,
      InitialRetryDelay = TimeSpan.FromSeconds(1)
    };
    var retry = new AzureServiceBusConnectionRetry(options);
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await retry.CreateClientWithRetryAsync("Endpoint=sb://invalid.servicebus.windows.net/;SharedAccessKeyName=Test;SharedAccessKey=abc123", cts.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task CreateClientWithRetryAsync_WithRetryIndefinitelyFalse_TriesInitialAttemptsAndThrowsAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      InitialRetryAttempts = 1,  // Only one retry after initial attempt
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      RetryIndefinitely = false   // Disable indefinite retry
    };
    var retry = new AzureServiceBusConnectionRetry(options);

    // Act & Assert - Using invalid connection string to force failure
    // The Azure SDK throws ServiceBusException for connection failures
    await Assert.That(async () => await retry.CreateClientWithRetryAsync("Endpoint=sb://invalid-test-namespace.servicebus.windows.net/;SharedAccessKeyName=Test;SharedAccessKey=abc123"))
      .ThrowsException();  // Could be ServiceBusException or wrapped in AggregateException
  }

  #endregion

  #region AzureServiceBusOptions Default Values Tests

  [Test]
  public async Task AzureServiceBusOptions_DefaultInitialRetryAttempts_IsFiveAsync() {
    // Arrange & Act
    var options = new AzureServiceBusOptions();

    // Assert
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(5);
  }

  [Test]
  public async Task AzureServiceBusOptions_DefaultInitialRetryDelay_IsOneSecondAsync() {
    // Arrange & Act
    var options = new AzureServiceBusOptions();

    // Assert
    await Assert.That(options.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(1));
  }

  [Test]
  public async Task AzureServiceBusOptions_DefaultMaxRetryDelay_Is120SecondsAsync() {
    // Arrange & Act
    var options = new AzureServiceBusOptions();

    // Assert
    await Assert.That(options.MaxRetryDelay).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task AzureServiceBusOptions_DefaultBackoffMultiplier_IsTwoAsync() {
    // Arrange & Act
    var options = new AzureServiceBusOptions();

    // Assert
    await Assert.That(options.BackoffMultiplier).IsEqualTo(2.0);
  }

  [Test]
  public async Task AzureServiceBusOptions_DefaultRetryIndefinitely_IsTrueAsync() {
    // Arrange & Act
    var options = new AzureServiceBusOptions();

    // Assert
    await Assert.That(options.RetryIndefinitely).IsTrue();
  }

  #endregion

  #region Exponential Backoff Sequence Tests

  [Test]
  public async Task CalculateNextDelay_ExponentialSequence_FollowsExpectedPatternAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };
    var retry = new AzureServiceBusConnectionRetry(options);

    // Act - Simulate exponential backoff sequence
    var delay1 = TimeSpan.FromSeconds(1);
    var delay2 = retry.CalculateNextDelay(delay1);
    var delay3 = retry.CalculateNextDelay(delay2);
    var delay4 = retry.CalculateNextDelay(delay3);
    var delay5 = retry.CalculateNextDelay(delay4);

    // Assert
    await Assert.That(delay1).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(delay2).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(delay3).IsEqualTo(TimeSpan.FromSeconds(4));
    await Assert.That(delay4).IsEqualTo(TimeSpan.FromSeconds(8));
    await Assert.That(delay5).IsEqualTo(TimeSpan.FromSeconds(16));
  }

  [Test]
  public async Task CalculateNextDelay_ExponentialSequence_CapsAtMaxAsync() {
    // Arrange
    var options = new AzureServiceBusOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(10)
    };
    var retry = new AzureServiceBusConnectionRetry(options);

    // Act - Simulate exponential backoff that hits the cap
    var delay1 = TimeSpan.FromSeconds(1);
    var delay2 = retry.CalculateNextDelay(delay1);  // 2
    var delay3 = retry.CalculateNextDelay(delay2);  // 4
    var delay4 = retry.CalculateNextDelay(delay3);  // 8
    var delay5 = retry.CalculateNextDelay(delay4);  // 16 -> capped at 10
    var delay6 = retry.CalculateNextDelay(delay5);  // stays at 10

    // Assert
    await Assert.That(delay4).IsEqualTo(TimeSpan.FromSeconds(8));
    await Assert.That(delay5).IsEqualTo(TimeSpan.FromSeconds(10));  // Capped
    await Assert.That(delay6).IsEqualTo(TimeSpan.FromSeconds(10));  // Stays capped
  }

  #endregion
}
