using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;
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
  /// Test event type for cascade tests.
  /// </summary>
  private sealed record TestEvent(Guid Id, string Data) : IEvent;

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

  // ========================================
  // RETURN VALUE CASCADING TESTS
  // ========================================

  /// <summary>
  /// Verifies that when a receptor returns an IEvent, that event is cascaded (dispatched).
  /// This is critical for inbox processing where receptors produce events that need publishing.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorReturnsEvent_ShouldCascadeEventAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var cascadeTracker = new CascadeTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a receptor that returns an event
    var returnedEvent = new TestEvent(Guid.CreateVersion7(), "cascaded");
    registry.RegisterReceptorWithReturn<TestMessage, TestEvent>(
      "EventProducingReceptor",
      LifecycleStage.PostInboxInline,
      returnedEvent
    );

    var invoker = new ReceptorInvoker(registry, _createScopeFactory(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert - The returned event should have been cascaded
    await Assert.That(cascadeTracker.CascadedMessages).Count().IsEqualTo(1);
    await Assert.That(cascadeTracker.CascadedMessages[0]).IsEqualTo(returnedEvent);
  }

  /// <summary>
  /// Verifies that when a receptor returns a tuple containing events, all events are cascaded.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorReturnsTupleWithEvents_ShouldCascadeAllEventsAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var cascadeTracker = new CascadeTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a receptor that returns a tuple with multiple events
    var event1 = new TestEvent(Guid.CreateVersion7(), "event1");
    var event2 = new TestEvent(Guid.CreateVersion7(), "event2");
    registry.RegisterReceptorWithReturn<TestMessage, (TestEvent, TestEvent)>(
      "TupleReceptor",
      LifecycleStage.PostInboxInline,
      (event1, event2)
    );

    var invoker = new ReceptorInvoker(registry, _createScopeFactory(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert - Both events should have been cascaded
    await Assert.That(cascadeTracker.CascadedMessages).Count().IsEqualTo(2);
    await Assert.That(cascadeTracker.CascadedMessages).Contains(event1);
    await Assert.That(cascadeTracker.CascadedMessages).Contains(event2);
  }

  /// <summary>
  /// Verifies that when a receptor returns an array of events, all events are cascaded.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorReturnsEventArray_ShouldCascadeAllEventsAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var cascadeTracker = new CascadeTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a receptor that returns an array of events
    var events = new[] {
      new TestEvent(Guid.CreateVersion7(), "event1"),
      new TestEvent(Guid.CreateVersion7(), "event2"),
      new TestEvent(Guid.CreateVersion7(), "event3")
    };
    registry.RegisterReceptorWithReturn<TestMessage, TestEvent[]>(
      "ArrayReceptor",
      LifecycleStage.PostInboxInline,
      events
    );

    var invoker = new ReceptorInvoker(registry, _createScopeFactory(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert - All events should have been cascaded
    await Assert.That(cascadeTracker.CascadedMessages).Count().IsEqualTo(3);
  }

  /// <summary>
  /// Verifies that when a receptor returns null, no cascade happens.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorReturnsNull_ShouldNotCascadeAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var cascadeTracker = new CascadeTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a receptor that returns null
    registry.RegisterReceptorWithReturn<TestMessage, TestEvent?>(
      "NullReceptor",
      LifecycleStage.PostInboxInline,
      null
    );

    var invoker = new ReceptorInvoker(registry, _createScopeFactory(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert - No events should have been cascaded
    await Assert.That(cascadeTracker.CascadedMessages).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Verifies that when a receptor returns a non-event result, no cascade happens.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorReturnsNonEvent_ShouldNotCascadeAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var cascadeTracker = new CascadeTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register a receptor that returns a plain string (not an IEvent)
    registry.RegisterReceptorWithReturn<TestMessage, string>(
      "StringReceptor",
      LifecycleStage.PostInboxInline,
      "just a string result"
    );

    var invoker = new ReceptorInvoker(registry, _createScopeFactory(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(message, LifecycleStage.PostInboxInline);

    // Assert - No events should have been cascaded
    await Assert.That(cascadeTracker.CascadedMessages).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Tracks which messages were cascaded (for testing purposes).
  /// </summary>
  private sealed class CascadeTracker : IEventCascader {
    private readonly List<IMessage> _cascadedMessages = [];
    public List<IMessage> CascadedMessages => _cascadedMessages;

    public Task CascadeFromResultAsync(object result, DispatchMode? receptorDefault = null, CancellationToken cancellationToken = default) {
      // Extract messages from result (using same logic as DispatcherEventCascader)
      foreach (var (message, _) in MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault)) {
        _cascadedMessages.Add(message);
      }
      return Task.CompletedTask;
    }
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
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    /// <summary>
    /// Registers a receptor that returns a specific value.
    /// Used to test return value cascading.
    /// </summary>
    public void RegisterReceptorWithReturn<TMessage, TReturn>(
      string receptorId,
      LifecycleStage stage,
      TReturn? returnValue
    ) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }

      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: receptorId,
        InvokeAsync: (sp, msg, ct) => {
          _tracker.RecordInvocation(receptorId, stage);
          return ValueTask.FromResult<object?>(returnValue);
        }
      ));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }
  }
}
