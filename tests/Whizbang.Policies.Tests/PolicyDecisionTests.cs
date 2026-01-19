using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Policies;

namespace Whizbang.Policies.Tests;

/// <summary>
/// Tests for PolicyDecision record to achieve 100% coverage.
/// </summary>
public class PolicyDecisionTests {
  [Test]
  public async Task PolicyDecision_ShouldStoreAllPropertiesAsync() {
    // Arrange
    var timestamp = DateTimeOffset.UtcNow;

    // Act
    var decision = new PolicyDecision {
      PolicyName = "StreamSelection",
      Rule = "Order.* → order-{id}",
      Matched = true,
      Configuration = "stream-order-123",
      Reason = "Message type matches Order.* pattern",
      Timestamp = timestamp
    };

    // Assert
    await Assert.That(decision.PolicyName).IsEqualTo("StreamSelection");
    await Assert.That(decision.Rule).IsEqualTo("Order.* → order-{id}");
    await Assert.That(decision.Matched).IsTrue();
    await Assert.That(decision.Configuration).IsEqualTo("stream-order-123");
    await Assert.That(decision.Reason).IsEqualTo("Message type matches Order.* pattern");
    await Assert.That(decision.Timestamp).IsEqualTo(timestamp);
  }

  [Test]
  public async Task PolicyDecision_Equality_WithSameValues_ShouldBeEqualAsync() {
    // Arrange
    var timestamp = DateTimeOffset.UtcNow;

    var decision1 = new PolicyDecision {
      PolicyName = "StreamSelection",
      Rule = "Order.*",
      Matched = true,
      Configuration = null,
      Reason = "Test",
      Timestamp = timestamp
    };

    var decision2 = new PolicyDecision {
      PolicyName = "StreamSelection",
      Rule = "Order.*",
      Matched = true,
      Configuration = null,
      Reason = "Test",
      Timestamp = timestamp
    };

    // Assert
    await Assert.That(decision1).IsEqualTo(decision2);
    await Assert.That(decision1.GetHashCode()).IsEqualTo(decision2.GetHashCode());
  }

  [Test]
  public async Task PolicyDecision_Equality_WithDifferentValues_ShouldNotBeEqualAsync() {
    // Arrange
    var decision1 = new PolicyDecision {
      PolicyName = "StreamSelection",
      Rule = "Order.*",
      Matched = true,
      Configuration = null,
      Reason = "Test",
      Timestamp = DateTimeOffset.UtcNow
    };

    var decision2 = new PolicyDecision {
      PolicyName = "ExecutionStrategy",
      Rule = "Payment.*",
      Matched = false,
      Configuration = null,
      Reason = "Different",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(decision1).IsNotEqualTo(decision2);
  }

  [Test]
  public async Task PolicyDecision_WithExpression_ShouldCreateNewInstanceAsync() {
    // Arrange
    var original = new PolicyDecision {
      PolicyName = "StreamSelection",
      Rule = "Order.*",
      Matched = true,
      Configuration = "config1",
      Reason = "Test",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    var modified = original with { Matched = false, Reason = "Updated" };

    // Assert
    await Assert.That(modified.PolicyName).IsEqualTo("StreamSelection");
    await Assert.That(modified.Rule).IsEqualTo("Order.*");
    await Assert.That(modified.Matched).IsFalse();
    await Assert.That(modified.Configuration).IsEqualTo("config1");
    await Assert.That(modified.Reason).IsEqualTo("Updated");
    await Assert.That(modified).IsNotEqualTo(original);
  }

  [Test]
  public async Task PolicyDecision_ToString_ShouldContainPropertyValuesAsync() {
    // Arrange
    var decision = new PolicyDecision {
      PolicyName = "StreamSelection",
      Rule = "Order.* → order-{id}",
      Matched = true,
      Configuration = "stream-order-123",
      Reason = "Message type matches",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    var result = decision.ToString();

    // Assert
    await Assert.That(result).Contains("StreamSelection");
    await Assert.That(result).Contains("Order.*");
    await Assert.That(result).Contains("True");
  }

  [Test]
  public async Task PolicyDecision_WithNullConfiguration_ShouldWorkAsync() {
    // Arrange & Act
    var decision = new PolicyDecision {
      PolicyName = "Test",
      Rule = "Test",
      Matched = false,
      Configuration = null,
      Reason = "No config needed",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(decision.Configuration).IsNull();
  }
}
