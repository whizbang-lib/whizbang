using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Tests for <see cref="SubscriptionResilienceOptions"/> to verify default values
/// match RabbitMQOptions and all properties work correctly.
/// </summary>
/// <tests>src/Whizbang.Core/Resilience/SubscriptionResilienceOptions.cs</tests>
public class SubscriptionResilienceOptionsTests {
  #region Default Value Tests

  [Test]
  public async Task InitialRetryAttempts_Default_MatchesRabbitMQOptionsDefaultAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();

    // Assert - matches RabbitMQOptions default of 5
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(5);
  }

  [Test]
  public async Task InitialRetryDelay_Default_MatchesRabbitMQOptionsDefaultAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();

    // Assert - matches RabbitMQOptions default of 1 second
    await Assert.That(options.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(1));
  }

  [Test]
  public async Task MaxRetryDelay_Default_MatchesRabbitMQOptionsDefaultAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();

    // Assert - matches RabbitMQOptions default of 120 seconds
    await Assert.That(options.MaxRetryDelay).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task BackoffMultiplier_Default_MatchesRabbitMQOptionsDefaultAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();

    // Assert - matches RabbitMQOptions default of 2.0
    await Assert.That(options.BackoffMultiplier).IsEqualTo(2.0);
  }

  [Test]
  public async Task RetryIndefinitely_Default_IsTrueAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();

    // Assert - subscriptions are critical, so always retry by default
    await Assert.That(options.RetryIndefinitely).IsTrue();
  }

  [Test]
  public async Task HealthCheckInterval_Default_IsOneMinuteAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();

    // Assert - health check every minute by default
    await Assert.That(options.HealthCheckInterval).IsEqualTo(TimeSpan.FromMinutes(1));
  }

  [Test]
  public async Task AllowPartialSubscriptions_Default_IsTrueAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();

    // Assert - allow partial subscriptions by default
    await Assert.That(options.AllowPartialSubscriptions).IsTrue();
  }

  #endregion

  #region Property Setter Tests

  [Test]
  public async Task InitialRetryAttempts_SetValue_ReturnsSetValueAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      // Act
      InitialRetryAttempts = 10
    };

    // Assert
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(10);
  }

  [Test]
  public async Task InitialRetryDelay_SetValue_ReturnsSetValueAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();
    var customDelay = TimeSpan.FromSeconds(5);

    // Act
    options.InitialRetryDelay = customDelay;

    // Assert
    await Assert.That(options.InitialRetryDelay).IsEqualTo(customDelay);
  }

  [Test]
  public async Task MaxRetryDelay_SetValue_ReturnsSetValueAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();
    var customMaxDelay = TimeSpan.FromMinutes(5);

    // Act
    options.MaxRetryDelay = customMaxDelay;

    // Assert
    await Assert.That(options.MaxRetryDelay).IsEqualTo(customMaxDelay);
  }

  [Test]
  public async Task BackoffMultiplier_SetValue_ReturnsSetValueAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      // Act
      BackoffMultiplier = 1.5
    };

    // Assert
    await Assert.That(options.BackoffMultiplier).IsEqualTo(1.5);
  }

  [Test]
  public async Task RetryIndefinitely_SetFalse_ReturnsFalseAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      // Act
      RetryIndefinitely = false
    };

    // Assert
    await Assert.That(options.RetryIndefinitely).IsFalse();
  }

  [Test]
  public async Task HealthCheckInterval_SetValue_ReturnsSetValueAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions();
    var customInterval = TimeSpan.FromSeconds(30);

    // Act
    options.HealthCheckInterval = customInterval;

    // Assert
    await Assert.That(options.HealthCheckInterval).IsEqualTo(customInterval);
  }

  [Test]
  public async Task AllowPartialSubscriptions_SetFalse_ReturnsFalseAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      // Act
      AllowPartialSubscriptions = false
    };

    // Assert
    await Assert.That(options.AllowPartialSubscriptions).IsFalse();
  }

  #endregion

  #region Edge Case Tests

  [Test]
  public async Task InitialRetryAttempts_SetToZero_AllowsSkippingInitialPhaseAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      // Act - zero means skip initial warning phase
      InitialRetryAttempts = 0
    };

    // Assert
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(0);
  }

  [Test]
  public async Task InitialRetryDelay_SetToZero_AllowsImmediateRetryAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      // Act
      InitialRetryDelay = TimeSpan.Zero
    };

    // Assert
    await Assert.That(options.InitialRetryDelay).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task BackoffMultiplier_SetToOne_DisablesExponentialBackoffAsync() {
    // Arrange
    var options = new SubscriptionResilienceOptions {
      // Act - multiplier of 1.0 means no growth (constant delay)
      BackoffMultiplier = 1.0
    };

    // Assert
    await Assert.That(options.BackoffMultiplier).IsEqualTo(1.0);
  }

  #endregion
}
