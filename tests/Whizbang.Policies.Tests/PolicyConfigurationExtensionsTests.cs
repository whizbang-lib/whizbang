using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Policies;

namespace Whizbang.Policies.Tests;

/// <summary>
/// Tests for PolicyConfiguration fluent API extensions.
/// Covers core configuration methods and method chaining.
/// </summary>
[Category("Policies")]
[Category("Configuration")]
public class PolicyConfigurationExtensionsTests {
  // ========================================
  // Topic Configuration Tests
  // ========================================

  [Test]
  public async Task UseTopic_ShouldSetTopicAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.UseTopic("orders");

    // Assert
    await Assert.That(config.Topic).IsEqualTo("orders");
  }

  [Test]
  public async Task UseTopic_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.UseTopic("orders");

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  // ========================================
  // Stream Key Configuration Tests
  // ========================================

  [Test]
  public async Task UseStreamKey_ShouldSetStreamKeyAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.UseStreamKey("order-123");

    // Assert
    await Assert.That(config.StreamKey).IsEqualTo("order-123");
  }

  [Test]
  public async Task UseStreamKey_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.UseStreamKey("order-123");

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  // ========================================
  // Execution Strategy Configuration Tests
  // ========================================

  [Test]
  public async Task UseExecutionStrategy_ShouldSetExecutionStrategyTypeAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.UseExecutionStrategy<FakeExecutionStrategy>();

    // Assert
    await Assert.That(config.ExecutionStrategyType).IsEqualTo(typeof(FakeExecutionStrategy));
  }

  [Test]
  public async Task UseExecutionStrategy_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.UseExecutionStrategy<FakeExecutionStrategy>();

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  // ========================================
  // Partition Router Configuration Tests
  // ========================================

  [Test]
  public async Task UsePartitionRouter_ShouldSetPartitionRouterTypeAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.UsePartitionRouter<FakePartitionRouter>();

    // Assert
    await Assert.That(config.PartitionRouterType).IsEqualTo(typeof(FakePartitionRouter));
  }

  [Test]
  public async Task UsePartitionRouter_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.UsePartitionRouter<FakePartitionRouter>();

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  // ========================================
  // Sequence Provider Configuration Tests
  // ========================================

  [Test]
  public async Task UseSequenceProvider_ShouldSetSequenceProviderTypeAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.UseSequenceProvider<FakeSequenceProvider>();

    // Assert
    await Assert.That(config.SequenceProviderType).IsEqualTo(typeof(FakeSequenceProvider));
  }

  [Test]
  public async Task UseSequenceProvider_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.UseSequenceProvider<FakeSequenceProvider>();

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  // ========================================
  // Partition Configuration Tests
  // ========================================

  [Test]
  public async Task WithPartitions_ShouldSetPartitionCountAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.WithPartitions(16);

    // Assert
    await Assert.That(config.PartitionCount).IsEqualTo(16);
  }

  [Test]
  public async Task WithPartitions_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.WithPartitions(16);

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  [Test]
  public async Task WithPartitions_WithZero_ShouldThrowAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act & Assert
    await Assert.That(() => config.WithPartitions(0))
      .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task WithPartitions_WithNegative_ShouldThrowAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act & Assert
    await Assert.That(() => config.WithPartitions(-1))
      .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  // ========================================
  // Concurrency Configuration Tests
  // ========================================

  [Test]
  public async Task WithConcurrency_ShouldSetMaxConcurrencyAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.WithConcurrency(10);

    // Assert
    await Assert.That(config.MaxConcurrency).IsEqualTo(10);
  }

  [Test]
  public async Task WithConcurrency_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.WithConcurrency(10);

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  [Test]
  public async Task WithConcurrency_WithZero_ShouldThrowAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act & Assert
    await Assert.That(() => config.WithConcurrency(0))
      .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task WithConcurrency_WithNegative_ShouldThrowAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act & Assert
    await Assert.That(() => config.WithConcurrency(-1))
      .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  // ========================================
  // Method Chaining Tests
  // ========================================

  [Test]
  public async Task PolicyConfiguration_ShouldSupportMethodChainingAsync() {
    // Arrange & Act
    var config = new PolicyConfiguration()
      .UseTopic("orders")
      .UseStreamKey("order-123")
      .UseExecutionStrategy<FakeExecutionStrategy>()
      .WithPartitions(16)
      .WithConcurrency(10);

    // Assert
    await Assert.That(config.Topic).IsEqualTo("orders");
    await Assert.That(config.StreamKey).IsEqualTo("order-123");
    await Assert.That(config.ExecutionStrategyType).IsEqualTo(typeof(FakeExecutionStrategy));
    await Assert.That(config.PartitionCount).IsEqualTo(16);
    await Assert.That(config.MaxConcurrency).IsEqualTo(10);
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportComplexChainingAsync() {
    // Arrange & Act
    var config = new PolicyConfiguration()
      .UseTopic("orders")
      .UsePartitionRouter<FakePartitionRouter>()
      .WithPartitions(32)
      .UseSequenceProvider<FakeSequenceProvider>()
      .PublishToKafka("orders-kafka")
      .SubscribeFromKafka("orders-input", "order-processor");

    // Assert
    await Assert.That(config.Topic).IsEqualTo("orders");
    await Assert.That(config.PartitionRouterType).IsEqualTo(typeof(FakePartitionRouter));
    await Assert.That(config.PartitionCount).IsEqualTo(32);
    await Assert.That(config.SequenceProviderType).IsEqualTo(typeof(FakeSequenceProvider));
    await Assert.That(config.PublishTargets).HasCount().EqualTo(1);
    await Assert.That(config.SubscriptionTargets).HasCount().EqualTo(1);
  }

  // Fake types for testing
  private class FakeExecutionStrategy { }
  private class FakePartitionRouter { }
  private class FakeSequenceProvider { }
}
