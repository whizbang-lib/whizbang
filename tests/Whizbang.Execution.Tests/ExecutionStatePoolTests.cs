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
  }

  // ============================================================================
  // EXECUTION STATE TESTS
  // ============================================================================

  [Test]
  public async Task Initialize_ShouldSetPropertiesAsync() {
    // Arrange
    var state = new ExecutionState<int>();
    var envelope = _createTestEnvelope();
    var context = _createTestPolicyContext();
    var handler = _createTestHandler();
    var source = new PooledValueTaskSource<int>();

    // Act
    state.Initialize(envelope, context, handler, source);

    // Assert
    await Assert.That(state.Envelope).IsEqualTo(envelope);
    await Assert.That(state.Context).IsEqualTo(context);
#pragma warning disable TUnitAssertions0008 // False positive - Handler is a delegate, not a ValueTask
    await Assert.That(state.Handler).IsNotNull();
#pragma warning restore TUnitAssertions0008
#pragma warning disable TUnitAssertions0008 // False positive - Source is a reference type, not a ValueTask
    await Assert.That(state.Source).IsNotNull();
#pragma warning restore TUnitAssertions0008
  }

  [Test]
  public async Task Reset_ShouldClearPropertiesAsync() {
    // Arrange
    var state = new ExecutionState<int>();
    var envelope = _createTestEnvelope();
    var context = _createTestPolicyContext();
    var handler = _createTestHandler();
    var source = new PooledValueTaskSource<int>();

    state.Initialize(envelope, context, handler, source);

    // Act
    state.Reset();

    // Assert - All properties should be null
    await Assert.That(state.Envelope).IsNull();
    await Assert.That(state.Context).IsNull();
#pragma warning disable TUnitAssertions0008 // False positive - Handler is a delegate, not a ValueTask
    await Assert.That(state.Handler).IsNull();
#pragma warning restore TUnitAssertions0008
#pragma warning disable TUnitAssertions0008 // False positive - Source is a reference type, not a ValueTask
    await Assert.That(state.Source).IsNull();
#pragma warning restore TUnitAssertions0008
  }

  // ============================================================================
  // HELPER METHODS
  // ============================================================================

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

  private static PolicyContext _createTestPolicyContext() {
    return new PolicyContext();
  }

  private static Func<IMessageEnvelope, PolicyContext, ValueTask<int>> _createTestHandler() {
    return (envelope, context) => ValueTask.FromResult(42);
  }

  private sealed class TestMessage {
    public string Content { get; set; } = "test";
  }
}
