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
  private IMessageEnvelope CreateTestEnvelope<TMessage>(TMessage payload) {
    var envelope = new Whizbang.Core.Observability.MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = new List<MessageHop>()
    };
    envelope.AddHop(new MessageHop {
      ServiceName = "Test",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });
    return envelope;
  }

  /// <summary>
  /// Helper to create a test policy context
  /// </summary>
  private PolicyContext CreateTestContext<TMessage>(TMessage message, IMessageEnvelope envelope) {
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
}
