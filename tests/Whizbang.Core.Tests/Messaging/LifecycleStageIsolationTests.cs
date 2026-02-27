using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for lifecycle stage isolation - ensures receptors ONLY fire at their registered stage.
/// Critical for PostPerspectiveAsync to fire AFTER perspective processing, not before.
/// </summary>
/// <docs>core-concepts/lifecycle-receptors#stage-isolation</docs>
[Category("Messaging")]
[Category("Lifecycle")]
[Category("PostPerspectiveAsync")]
public class LifecycleStageIsolationTests {

  // ========================================
  // RuntimeLifecycleInvoker Stage Isolation Tests
  // ========================================

  /// <summary>
  /// CRITICAL TEST: Verifies that a receptor registered at PostPerspectiveAsync
  /// is NOT invoked when the lifecycle invoker is called with PrePerspectiveAsync stage.
  /// This is the core bug - receptors firing before perspective ApplyAsync.
  /// </summary>
  [Test]
  public async Task RuntimeInvoker_PostPerspectiveAsyncReceptor_NotInvokedAtPrePerspectiveAsyncAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var invoker = new RuntimeLifecycleInvoker(registry);
    var trackingReceptor = new InvocationTrackingReceptor();

    // Register at PostPerspectiveAsync ONLY
    registry.Register<TestEvent>(trackingReceptor, LifecycleStage.PostPerspectiveAsync);

    var envelope = _createTestEnvelope(new TestEvent());

    // Act - Invoke at PrePerspectiveAsync (WRONG stage)
    await invoker.InvokeAsync(
      envelope,
      LifecycleStage.PrePerspectiveAsync,
      context: null,
      CancellationToken.None);

    // Assert - Should NOT have fired
    await Assert.That(trackingReceptor.InvocationCount).IsEqualTo(0)
      .Because("PostPerspectiveAsync receptor should NOT fire when invoked at PrePerspectiveAsync stage");
  }

  /// <summary>
  /// Verifies that a receptor registered at PostPerspectiveAsync IS invoked
  /// when the lifecycle invoker is called with PostPerspectiveAsync stage.
  /// </summary>
  [Test]
  public async Task RuntimeInvoker_PostPerspectiveAsyncReceptor_InvokedAtPostPerspectiveAsyncAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var invoker = new RuntimeLifecycleInvoker(registry);
    var trackingReceptor = new InvocationTrackingReceptor();

    // Register at PostPerspectiveAsync
    registry.Register<TestEvent>(trackingReceptor, LifecycleStage.PostPerspectiveAsync);

    var envelope = _createTestEnvelope(new TestEvent());

    // Act - Invoke at PostPerspectiveAsync (CORRECT stage)
    await invoker.InvokeAsync(
      envelope,
      LifecycleStage.PostPerspectiveAsync,
      context: null,
      CancellationToken.None);

    // Assert - SHOULD have fired
    await Assert.That(trackingReceptor.InvocationCount).IsEqualTo(1)
      .Because("PostPerspectiveAsync receptor SHOULD fire when invoked at PostPerspectiveAsync stage");
  }

  /// <summary>
  /// Verifies that a receptor registered at PostPerspectiveAsync
  /// is NOT invoked when called at PostPerspectiveInline stage.
  /// Async vs Inline stages must be isolated.
  /// </summary>
  [Test]
  public async Task RuntimeInvoker_PostPerspectiveAsyncReceptor_NotInvokedAtPostPerspectiveInlineAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var invoker = new RuntimeLifecycleInvoker(registry);
    var trackingReceptor = new InvocationTrackingReceptor();

    // Register at PostPerspectiveAsync ONLY
    registry.Register<TestEvent>(trackingReceptor, LifecycleStage.PostPerspectiveAsync);

    var envelope = _createTestEnvelope(new TestEvent());

    // Act - Invoke at PostPerspectiveInline (WRONG stage - Inline not Async)
    await invoker.InvokeAsync(
      envelope,
      LifecycleStage.PostPerspectiveInline,
      context: null,
      CancellationToken.None);

    // Assert - Should NOT have fired
    await Assert.That(trackingReceptor.InvocationCount).IsEqualTo(0)
      .Because("PostPerspectiveAsync receptor should NOT fire at PostPerspectiveInline stage");
  }

  /// <summary>
  /// Verifies that a receptor registered at PostPerspectiveAsync
  /// is NOT invoked when called at PostDistributeAsync stage.
  /// Different pipeline stages must be isolated.
  /// </summary>
  [Test]
  public async Task RuntimeInvoker_PostPerspectiveAsyncReceptor_NotInvokedAtPostDistributeAsyncAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var invoker = new RuntimeLifecycleInvoker(registry);
    var trackingReceptor = new InvocationTrackingReceptor();

    // Register at PostPerspectiveAsync ONLY
    registry.Register<TestEvent>(trackingReceptor, LifecycleStage.PostPerspectiveAsync);

    var envelope = _createTestEnvelope(new TestEvent());

    // Act - Invoke at PostDistributeAsync (WRONG stage - different pipeline)
    await invoker.InvokeAsync(
      envelope,
      LifecycleStage.PostDistributeAsync,
      context: null,
      CancellationToken.None);

    // Assert - Should NOT have fired
    await Assert.That(trackingReceptor.InvocationCount).IsEqualTo(0)
      .Because("PostPerspectiveAsync receptor should NOT fire at PostDistributeAsync stage");
  }

  // ========================================
  // All 4 Perspective Stages Isolation Tests
  // ========================================

  /// <summary>
  /// Verifies all 4 perspective lifecycle stages are properly isolated from each other:
  /// PrePerspectiveAsync, PrePerspectiveInline, PostPerspectiveAsync, PostPerspectiveInline
  /// </summary>
  [Test]
  public async Task RuntimeInvoker_AllPerspectiveStages_CompletelyIsolatedAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var invoker = new RuntimeLifecycleInvoker(registry);

    var preAsyncReceptor = new InvocationTrackingReceptor();
    var preInlineReceptor = new InvocationTrackingReceptor();
    var postAsyncReceptor = new InvocationTrackingReceptor();
    var postInlineReceptor = new InvocationTrackingReceptor();

    // Register each receptor at its specific stage
    registry.Register<TestEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveAsync);
    registry.Register<TestEvent>(preInlineReceptor, LifecycleStage.PrePerspectiveInline);
    registry.Register<TestEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveAsync);
    registry.Register<TestEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);

    var envelope = _createTestEnvelope(new TestEvent());

    // Act - Invoke at PostPerspectiveAsync ONLY
    await invoker.InvokeAsync(
      envelope,
      LifecycleStage.PostPerspectiveAsync,
      context: null,
      CancellationToken.None);

    // Assert - ONLY postAsyncReceptor should have fired
    await Assert.That(preAsyncReceptor.InvocationCount).IsEqualTo(0)
      .Because("PrePerspectiveAsync receptor should not fire at PostPerspectiveAsync stage");
    await Assert.That(preInlineReceptor.InvocationCount).IsEqualTo(0)
      .Because("PrePerspectiveInline receptor should not fire at PostPerspectiveAsync stage");
    await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1)
      .Because("PostPerspectiveAsync receptor SHOULD fire at PostPerspectiveAsync stage");
    await Assert.That(postInlineReceptor.InvocationCount).IsEqualTo(0)
      .Because("PostPerspectiveInline receptor should not fire at PostPerspectiveAsync stage");
  }

  /// <summary>
  /// Verifies PrePerspectiveAsync receptor only fires at PrePerspectiveAsync stage.
  /// </summary>
  [Test]
  public async Task RuntimeInvoker_PrePerspectiveAsyncReceptor_OnlyFiresAtPrePerspectiveAsyncAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var invoker = new RuntimeLifecycleInvoker(registry);
    var trackingReceptor = new InvocationTrackingReceptor();

    registry.Register<TestEvent>(trackingReceptor, LifecycleStage.PrePerspectiveAsync);

    var envelope = _createTestEnvelope(new TestEvent());

    // Act 1 - Invoke at PostPerspectiveAsync (wrong stage)
    await invoker.InvokeAsync(envelope, LifecycleStage.PostPerspectiveAsync, null, CancellationToken.None);

    // Assert 1 - Should not have fired
    await Assert.That(trackingReceptor.InvocationCount).IsEqualTo(0);

    // Act 2 - Invoke at PrePerspectiveAsync (correct stage)
    await invoker.InvokeAsync(envelope, LifecycleStage.PrePerspectiveAsync, null, CancellationToken.None);

    // Assert 2 - Should have fired once
    await Assert.That(trackingReceptor.InvocationCount).IsEqualTo(1);
  }

  // ========================================
  // Test Helpers
  // ========================================

  /// <summary>
  /// Creates a test envelope with the specified payload.
  /// </summary>
  private static MessageEnvelope<TMessage> _createTestEnvelope<TMessage>(TMessage message) where TMessage : IMessage {
    return new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow
      }]
    };
  }

  // ========================================
  // Test Types (AOT-compatible, no reflection)
  // ========================================

  /// <summary>
  /// Test event for stage isolation tests.
  /// </summary>
  internal sealed record TestEvent : IEvent;

  /// <summary>
  /// Tracking receptor that counts invocations. AOT-compatible.
  /// </summary>
  internal sealed class InvocationTrackingReceptor : IReceptor<TestEvent> {
    private int _invocationCount;
    private readonly object _lock = new();

    /// <summary>
    /// Number of times HandleAsync was called.
    /// </summary>
    public int InvocationCount {
      get {
        lock (_lock) { return _invocationCount; }
      }
    }

    /// <summary>
    /// Increments invocation count. Thread-safe.
    /// </summary>
    public ValueTask HandleAsync(TestEvent message, CancellationToken cancellationToken = default) {
      lock (_lock) { _invocationCount++; }
      return ValueTask.CompletedTask;
    }
  }
}
