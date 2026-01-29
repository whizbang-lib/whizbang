using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Pooling;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Policies.Tests;

/// <summary>
/// Tests for PolicyContextPool object pooling.
/// </summary>
/// <remarks>
/// These tests MUST run in isolation because PolicyContextPool is a static singleton.
/// Parallel test execution causes interference when multiple tests rent/return from the same pool.
/// </remarks>
[Category("Policies")]
[NotInParallel("PolicyContextPool")]
public class PolicyContextPoolTests {
  [Test]
  public async Task Rent_ShouldReturnInitializedContextAsync() {
    // Arrange
    var message = new TestMessage();
    var envelope = _createTestEnvelope();
    var environment = "test";

    // Act
    var context = PolicyContextPool.Rent(message, envelope, null, environment);

    // Assert
    await Assert.That(context).IsNotNull();
    await Assert.That(context.Message).IsEqualTo(message);
    await Assert.That(context.Envelope).IsEqualTo(envelope);
    await Assert.That(context.Environment).IsEqualTo(environment);
  }

  [Test]
  public async Task Return_WithNullContext_ShouldNotThrowAsync() {
    // Act & Assert - Should not throw
    PolicyContextPool.Return(null);
  }

  [Test]
  public async Task RentReturn_ShouldReinitializeContextAsync() {
    // Arrange
    var message1 = new TestMessage { Content = "first" };
    var envelope1 = _createTestEnvelope();

    // Act - Rent and return a context to populate the pool
    var context1 = PolicyContextPool.Rent(message1, envelope1, null, "env1");
    PolicyContextPool.Return(context1);

    // Rent again - might get same instance or different one from pool
    var message2 = new TestMessage { Content = "second" };
    var envelope2 = _createTestEnvelope();
    var context2 = PolicyContextPool.Rent(message2, envelope2, null, "env2");

    // Assert - Context should be properly initialized with new values
    // (whether it's the same instance or a different one from the pool)
    await Assert.That(context2.Message).IsEqualTo(message2); // Should have new message
    await Assert.That(context2.Envelope).IsEqualTo(envelope2); // Should have new envelope
    await Assert.That(context2.Environment).IsEqualTo("env2"); // Should have new environment
  }

  [Test]
  public async Task Pool_ShouldCreateNewContext_WhenEmptyAsync() {
    // Act - Rent from empty pool
    var context1 = PolicyContextPool.Rent(new TestMessage(), _createTestEnvelope(), null, "env");
    var context2 = PolicyContextPool.Rent(new TestMessage(), _createTestEnvelope(), null, "env");

    // Assert - Should be different instances (pool was empty)
    await Assert.That(context1).IsNotSameReferenceAs(context2);
  }

  [Test]
  public async Task Pool_ShouldNotExceedMaxSize_WhenReturningManyContextsAsync() {
    // Arrange - Create more contexts than max pool size (1024)
    const int contextCount = 1200;
    var contexts = new List<PolicyContext>();

    // Act - Rent many contexts
    for (int i = 0; i < contextCount; i++) {
      var context = PolicyContextPool.Rent(new TestMessage(), _createTestEnvelope(), null, $"env-{i}");
      contexts.Add(context);
    }

    // Return all contexts
    foreach (var context in contexts) {
      PolicyContextPool.Return(context);
    }

    // Rent contexts again - pool should have capped at max size
    var rentedAfterReturn = new List<PolicyContext>();
    for (int i = 0; i < contextCount; i++) {
      rentedAfterReturn.Add(PolicyContextPool.Rent(new TestMessage(), _createTestEnvelope(), null, $"env-{i}"));
    }

    // Assert - Most should be reused (pool caps at 1024)
    // Use a tolerance range because:
    // 1. ConcurrentBag may have slight timing variations
    // 2. Static pool state may have residual items from test infrastructure
    var reusedCount = 0;
    foreach (var rented in rentedAfterReturn) {
      if (contexts.Contains(rented)) {
        reusedCount++;
      }
    }

    // Pool max size is 1024, so we expect close to that many reused (allowing ±5% tolerance)
    await Assert.That(reusedCount).IsGreaterThanOrEqualTo(970)
      .Because("Pool should reuse approximately 1024 contexts (max pool size)");
    await Assert.That(reusedCount).IsLessThanOrEqualTo(1024)
      .Because("Cannot reuse more than max pool size");
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }

  private sealed class TestMessage {
    public string Content { get; set; } = "test";
  }
}
