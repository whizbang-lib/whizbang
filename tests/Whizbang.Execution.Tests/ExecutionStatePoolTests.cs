using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Pooling;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Tests for ExecutionStatePool{T} and ExecutionState{T}.
/// Verifies rent/return mechanics, initialization, and reset behavior.
/// </summary>
public class ExecutionStatePoolTests {
  // ============================================================================
  // EXECUTION STATE POOL TESTS
  // ============================================================================

  [Test]
  public async Task Rent_ShouldReturnExecutionStateAsync() {
    // Arrange & Act
    var state = ExecutionStatePool<int>.Rent();

    // Assert
    await Assert.That(state).IsNotNull();

    // TODO: Implement ExecutionStatePool.Rent() - should return pooled or new ExecutionState<T>
    throw new NotImplementedException("ExecutionStatePool tests pending implementation");
  }

  [Test]
  public async Task Return_ShouldAddToPoolAsync() {
    // Arrange
    var state = ExecutionStatePool<int>.Rent();
    state.Reset();

    // Act
    ExecutionStatePool<int>.Return(state);

    // Rent again to verify pooling
    var reused = ExecutionStatePool<int>.Rent();

    // Assert - Should get the same instance back
    await Assert.That(object.ReferenceEquals(state, reused)).IsTrue();

    // TODO: Implement ExecutionStatePool.Return() - should add state to pool for reuse
    throw new NotImplementedException("ExecutionStatePool tests pending implementation");
  }

  [Test]
  public async Task RentReturn_ShouldReuseInstanceAsync() {
    // Arrange
    var original = ExecutionStatePool<string>.Rent();
    original.Reset();

    // Act
    ExecutionStatePool<string>.Return(original);
    var reused = ExecutionStatePool<string>.Rent();

    // Assert
    await Assert.That(object.ReferenceEquals(original, reused)).IsTrue();

    // TODO: Implement ExecutionStatePool rent/return cycle
    throw new NotImplementedException("ExecutionStatePool tests pending implementation");
  }

  // ============================================================================
  // EXECUTION STATE TESTS
  // ============================================================================

  [Test]
  public async Task Initialize_ShouldSetPropertiesAsync() {
    // Arrange
    var state = new ExecutionState<int>();
    var envelope = CreateTestEnvelope();
    var context = CreateTestPolicyContext();
    var handler = CreateTestHandler();
    var source = new PooledValueTaskSource<int>();

    // Act
    state.Initialize(envelope, context, handler, source);

    // Assert
    await Assert.That(state.Envelope).IsEqualTo(envelope);
    await Assert.That(state.Context).IsEqualTo(context);
    await Assert.That(state.Handler).IsEqualTo(handler);
    await Assert.That(state.Source).IsEqualTo(source);

    // TODO: Implement ExecutionState.Initialize() - should set all properties
    throw new NotImplementedException("ExecutionState tests pending implementation");
  }

  [Test]
  public async Task Reset_ShouldClearPropertiesAsync() {
    // Arrange
    var state = new ExecutionState<int>();
    var envelope = CreateTestEnvelope();
    var context = CreateTestPolicyContext();
    var handler = CreateTestHandler();
    var source = new PooledValueTaskSource<int>();

    state.Initialize(envelope, context, handler, source);

    // Act
    state.Reset();

    // Assert - All properties should be null
    await Assert.That(state.Envelope).IsNull();
    await Assert.That(state.Context).IsNull();
    await Assert.That(state.Handler).IsNull();
    await Assert.That(state.Source).IsNull();

    // TODO: Implement ExecutionState.Reset() - should clear all properties
    throw new NotImplementedException("ExecutionState tests pending implementation");
  }

  // ============================================================================
  // HELPER METHODS
  // ============================================================================

  private static MessageEnvelope<TestMessage> CreateTestEnvelope() {
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

  private static PolicyContext CreateTestPolicyContext() {
    return new PolicyContext();
  }

  private static Func<IMessageEnvelope, PolicyContext, ValueTask<int>> CreateTestHandler() {
    return (envelope, context) => ValueTask.FromResult(42);
  }

  private class TestMessage {
    public string Content { get; set; } = "test";
  }
}
