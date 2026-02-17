using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IReceptorInvoker - the component that invokes receptors at appropriate lifecycle stages.
/// The source generator categorizes receptors at compile time, so the invoker just looks up and invokes.
/// </summary>
/// <docs>core-concepts/lifecycle-receptors</docs>
public class ReceptorInvokerTests {

  /// <summary>
  /// Test message type for receptor invocation tests.
  /// </summary>
  private sealed record TestMessage(string Value) : IMessage;

  /// <summary>
  /// Creates a service scope factory that returns a fresh ServiceProvider each time.
  /// </summary>
  private static IServiceScopeFactory _createScopeFactory() {
    var services = new ServiceCollection();
    var provider = services.BuildServiceProvider();
    return provider.GetRequiredService<IServiceScopeFactory>();
  }

  /// <summary>
  /// Tracks which receptors were invoked and at which stages.
  /// </summary>
  private sealed class InvocationTracker {
    private readonly List<(string ReceptorId, LifecycleStage Stage)> _invocations = [];
    public List<(string ReceptorId, LifecycleStage Stage)> Invocations => _invocations;
    public void RecordInvocation(string receptorId, LifecycleStage stage) => _invocations.Add((receptorId, stage));
    public void Clear() => _invocations.Clear();
  }

  /// <summary>
  /// Verifies that a receptor registered at PostInboxInline is invoked at that stage.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorAtPostInboxInline_ShouldInvokeAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a receptor at PostInboxInline (like a receptor with [FireAt(PostInboxInline)])
    registry.RegisterReceptor<TestMessage>("PostInboxReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
    await Assert.That(tracker.Invocations[0].ReceptorId).IsEqualTo("PostInboxReceptor");
    await Assert.That(tracker.Invocations[0].Stage).IsEqualTo(LifecycleStage.PostInboxInline);
  }

  /// <summary>
  /// Verifies that a receptor registered at PostInboxInline is NOT invoked at PreOutboxInline.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorAtPostInboxInline_ShouldNotInvokeAtPreOutboxAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a receptor at PostInboxInline only
    registry.RegisterReceptor<TestMessage>("PostInboxReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act - Try to invoke at PreOutboxInline (wrong stage)
    await invoker.InvokeAsync(message, LifecycleStage.PreOutboxInline);

    // Assert - Receptor should NOT be invoked
    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Verifies that a "default" receptor (registered at all 3 default stages by source generator)
  /// is invoked at PostInboxInline.
  /// </summary>
  [Test]
  public async Task InvokeAsync_DefaultReceptor_ShouldInvokeAtPostInboxInlineAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a default receptor at all 3 default stages (simulating no [FireAt] attribute)
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PostInboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PreOutboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.LocalImmediateInline);

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act - Invoke at PostInboxInline
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
    await Assert.That(tracker.Invocations[0].ReceptorId).IsEqualTo("DefaultReceptor");
  }

  /// <summary>
  /// Verifies that a "default" receptor is invoked at PreOutboxInline.
  /// </summary>
  [Test]
  public async Task InvokeAsync_DefaultReceptor_ShouldInvokeAtPreOutboxInlineAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a default receptor at all 3 default stages
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PostInboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PreOutboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.LocalImmediateInline);

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PreOutboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
    await Assert.That(tracker.Invocations[0].ReceptorId).IsEqualTo("DefaultReceptor");
  }

  /// <summary>
  /// Verifies that a "default" receptor is invoked at LocalImmediateInline.
  /// </summary>
  [Test]
  public async Task InvokeAsync_DefaultReceptor_ShouldInvokeAtLocalImmediateInlineAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a default receptor at all 3 default stages
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PostInboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PreOutboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.LocalImmediateInline);

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.LocalImmediateInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
    await Assert.That(tracker.Invocations[0].ReceptorId).IsEqualTo("DefaultReceptor");
  }

  /// <summary>
  /// Verifies that a "default" receptor is NOT invoked at non-default stages.
  /// </summary>
  [Test]
  public async Task InvokeAsync_DefaultReceptor_ShouldNotInvokeAtNonDefaultStagesAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a default receptor at all 3 default stages only
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PostInboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.PreOutboxInline);
    registry.RegisterReceptor<TestMessage>("DefaultReceptor", LifecycleStage.LocalImmediateInline);

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act - Invoke at PreInboxInline (NOT a default stage)
    await invoker.InvokeAsync(message, LifecycleStage.PreInboxInline);

    // Assert - Receptor should NOT be invoked
    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Verifies that multiple receptors for the same message type are all invoked.
  /// </summary>
  [Test]
  public async Task InvokeAsync_MultipleReceptors_ShouldInvokeAllAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register two receptors at PostInboxInline
    registry.RegisterReceptor<TestMessage>("Receptor1", LifecycleStage.PostInboxInline);
    registry.RegisterReceptor<TestMessage>("Receptor2", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert - Both receptors should be invoked
    await Assert.That(tracker.Invocations).Count().IsEqualTo(2);
    await Assert.That(tracker.Invocations.Select(i => i.ReceptorId)).Contains("Receptor1");
    await Assert.That(tracker.Invocations.Select(i => i.ReceptorId)).Contains("Receptor2");
  }

  /// <summary>
  /// Verifies that InvokeAsync with unknown message type returns without error.
  /// </summary>
  [Test]
  public async Task InvokeAsync_UnknownMessageType_ShouldNotThrowAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    // Don't register any receptors for TestMessage

    var invoker = new ReceptorInvoker(registry, _createScopeFactory());
    var message = new TestMessage("test");

    // Act & Assert - Should not throw
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);
    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Test registry implementation that mimics source-generated behavior.
  /// Receptors are registered at specific stages - the compile-time categorization is simulated.
  /// </summary>
  private sealed class TestReceptorRegistry : IReceptorRegistry {
    private readonly InvocationTracker _tracker;
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public TestReceptorRegistry(InvocationTracker tracker) {
      _tracker = tracker;
    }

    public void RegisterReceptor<TMessage>(string receptorId, LifecycleStage stage) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }

      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: receptorId,
        InvokeAsync: (sp, msg, ct) => {
          // sp is the scoped service provider (not used in tests)
          _tracker.RecordInvocation(receptorId, stage);
          return ValueTask.CompletedTask;
        }
      ));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }
  }
}
