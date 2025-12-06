using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Partitioning;
using Whizbang.Core.Policies;
using Whizbang.Core.Sequencing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Policies.Tests;

/// <summary>
/// Tests for PolicyEngine implementation.
/// Verifies policy matching, configuration, and decision trail recording.
/// </summary>
[Category("Policies")]
public class PolicyEngineTests {
  // Test messages
  private record OrderCommand(string OrderId, decimal Amount);
  private record PaymentCommand(string PaymentId, decimal Amount);
  private record NotificationCommand(string UserId, string Message);

  /// <summary>
  /// Helper to create a test envelope
  /// </summary>
  private static Whizbang.Core.Observability.MessageEnvelope<TMessage> CreateTestEnvelope<TMessage>(TMessage payload) {
    var envelope = new Whizbang.Core.Observability.MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = []
    };
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });
    return envelope;
  }

  /// <summary>
  /// Helper to create a test policy context
  /// </summary>
  private static PolicyContext CreateTestContext<TMessage>(TMessage message, IMessageEnvelope envelope) {
    var services = new ServiceCollection().BuildServiceProvider();
    return new PolicyContext(
      message: message!,
      envelope: envelope,
      services: services,
      environment: "test"
    );
  }

  [Test]
  public async Task PolicyEngine_ShouldMatchSinglePolicyAsync() {
    // Arrange
    var engine = new PolicyEngine();
    var matchedPolicyName = "";

    engine.AddPolicy("OrderPolicy", ctx => ctx.Message is OrderCommand, config => {
      matchedPolicyName = "OrderPolicy";
      config.UseTopic("orders");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(matchedPolicyName).IsEqualTo("OrderPolicy");
    await Assert.That(policyConfig).IsNotNull();
    await Assert.That(policyConfig!.Topic).IsEqualTo("orders");
  }

  [Test]
  public async Task PolicyEngine_ShouldMatchFirstMatchingPolicyAsync() {
    // Arrange
    var engine = new PolicyEngine();
    var matchedPolicies = new List<string>();

    engine.AddPolicy("Policy1", ctx => ctx.Message is OrderCommand, config => {
      matchedPolicies.Add("Policy1");
      config.UseTopic("topic1");
    });

    engine.AddPolicy("Policy2", ctx => ctx.Message is OrderCommand, config => {
      matchedPolicies.Add("Policy2");
      config.UseTopic("topic2");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert - Should match first policy only
    await Assert.That(matchedPolicies).HasCount().EqualTo(1);
    await Assert.That(matchedPolicies[0]).IsEqualTo("Policy1");
    await Assert.That(policyConfig!.Topic).IsEqualTo("topic1");
  }

  [Test]
  public async Task PolicyEngine_ShouldReturnNullWhenNoPolicyMatchesAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("PaymentPolicy", ctx => ctx.Message is PaymentCommand, config => {
      config.UseTopic("payments");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig).IsNull();
  }

  [Test]
  public async Task PolicyEngine_ShouldRecordDecisionInTrailAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => ctx.Message is OrderCommand, config => {
      config.UseTopic("orders");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    await engine.MatchAsync(context);

    // Assert - Decision should be recorded in trail
    var decisions = context.Trail.Decisions;
    await Assert.That(decisions).HasCount().GreaterThan(0);
    await Assert.That(decisions[0].PolicyName).IsEqualTo("OrderPolicy");
    await Assert.That(decisions[0].Matched).IsTrue();
  }

  [Test]
  public async Task PolicyEngine_ShouldRecordUnmatchedPoliciesInTrailAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("PaymentPolicy", ctx => ctx.Message is PaymentCommand, config => {
      config.UseTopic("payments");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    await engine.MatchAsync(context);

    // Assert - Unmatched policy should be recorded
    var decisions = context.Trail.Decisions;
    await Assert.That(decisions).HasCount().GreaterThan(0);
    await Assert.That(decisions[0].PolicyName).IsEqualTo("PaymentPolicy");
    await Assert.That(decisions[0].Matched).IsFalse();
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportTopicAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => true, config => {
      config.UseTopic("orders");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig!.Topic).IsEqualTo("orders");
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportStreamKeyAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => true, config => {
      config.UseStreamKey("order-123");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig!.StreamKey).IsEqualTo("order-123");
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportExecutionStrategyAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => true, config => {
      config.UseExecutionStrategy<SerialExecutor>();
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig!.ExecutionStrategyType).IsEqualTo(typeof(SerialExecutor));
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportPartitionRouterAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => true, config => {
      config.UsePartitionRouter<HashPartitionRouter>();
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig!.PartitionRouterType).IsEqualTo(typeof(HashPartitionRouter));
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportSequenceProviderAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => true, config => {
      config.UseSequenceProvider<InMemorySequenceProvider>();
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig!.SequenceProviderType).IsEqualTo(typeof(InMemorySequenceProvider));
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportPartitionCountAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => true, config => {
      config.WithPartitions(4);
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig!.PartitionCount).IsEqualTo(4);
  }

  [Test]
  public async Task PolicyConfiguration_ShouldSupportConcurrencyAsync() {
    // Arrange
    var engine = new PolicyEngine();

    engine.AddPolicy("OrderPolicy", ctx => true, config => {
      config.WithConcurrency(10);
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert
    await Assert.That(policyConfig!.MaxConcurrency).IsEqualTo(10);
  }

  [Test]
  public async Task AddPolicy_WithNullName_ShouldThrowAsync() {
    // Arrange
    var engine = new PolicyEngine();

    // Act & Assert
    var exception = await Assert.That(() => engine.AddPolicy(null!, ctx => true, config => { }))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(exception.Message).Contains("Policy name cannot be null or empty");
  }

  [Test]
  public async Task AddPolicy_WithEmptyName_ShouldThrowAsync() {
    // Arrange
    var engine = new PolicyEngine();

    // Act & Assert
    var exception = await Assert.That(() => engine.AddPolicy("", ctx => true, config => { }))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(exception.Message).Contains("Policy name cannot be null or empty");
  }

  [Test]
  public async Task AddPolicy_WithWhitespaceName_ShouldThrowAsync() {
    // Arrange
    var engine = new PolicyEngine();

    // Act & Assert
    var exception = await Assert.That(() => engine.AddPolicy("   ", ctx => true, config => { }))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(exception.Message).Contains("Policy name cannot be null or empty");
  }

  [Test]
  public async Task AddPolicy_WithNullPredicate_ShouldThrowAsync() {
    // Arrange
    var engine = new PolicyEngine();

    // Act & Assert
    await Assert.That(() => engine.AddPolicy("TestPolicy", null!, config => { }))
      .ThrowsExactly<ArgumentNullException>()
      .WithParameterName("predicate");
  }

  [Test]
  public async Task AddPolicy_WithNullConfigure_ShouldThrowAsync() {
    // Arrange
    var engine = new PolicyEngine();

    // Act & Assert
    await Assert.That(() => engine.AddPolicy("TestPolicy", ctx => true, null!))
      .ThrowsExactly<ArgumentNullException>()
      .WithParameterName("configure");
  }

  [Test]
  public async Task MatchAsync_WithNullContext_ShouldThrowAsync() {
    // Arrange
    var engine = new PolicyEngine();
    engine.AddPolicy("TestPolicy", ctx => true, config => { });

    // Act & Assert
    await Assert.That(async () => await engine.MatchAsync(null!))
      .ThrowsExactly<ArgumentNullException>()
      .WithParameterName("context");
  }

  [Test]
  public async Task MatchAsync_WithPredicateThrowingException_ShouldRecordFailureAsync() {
    // Arrange
    var engine = new PolicyEngine();
    engine.AddPolicy("FailingPolicy", ctx => throw new InvalidOperationException("Test exception"), config => {
      config.UseTopic("test");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert - Should return null and record failure
    await Assert.That(policyConfig).IsNull();
    var decisions = context.Trail.Decisions;
    await Assert.That(decisions).HasCount().EqualTo(1);
    await Assert.That(decisions[0].PolicyName).IsEqualTo("FailingPolicy");
    await Assert.That(decisions[0].Matched).IsFalse();
    await Assert.That(decisions[0].Reason).Contains("Evaluation failed");
    await Assert.That(decisions[0].Reason).Contains("Test exception");
  }

  [Test]
  public async Task MatchAsync_WithPredicateThrowingException_ShouldContinueToNextPolicyAsync() {
    // Arrange
    var engine = new PolicyEngine();
    engine.AddPolicy("FailingPolicy", ctx => throw new InvalidOperationException("Test exception"), config => {
      config.UseTopic("test1");
    });
    engine.AddPolicy("SuccessPolicy", ctx => ctx.Message is OrderCommand, config => {
      config.UseTopic("test2");
    });

    var message = new OrderCommand("order-123", 100m);
    var envelope = CreateTestEnvelope(message);
    var context = CreateTestContext(message, envelope);

    // Act
    var policyConfig = await engine.MatchAsync(context);

    // Assert - Should skip failing policy and match the second one
    await Assert.That(policyConfig).IsNotNull();
    await Assert.That(policyConfig!.Topic).IsEqualTo("test2");
    var decisions = context.Trail.Decisions;
    await Assert.That(decisions).HasCount().EqualTo(2);
    await Assert.That(decisions[0].Matched).IsFalse(); // FailingPolicy
    await Assert.That(decisions[1].Matched).IsTrue(); // SuccessPolicy
  }
}
