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
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

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
  /// Creates a service provider for testing.
  /// ReceptorInvoker now uses ambient scope (scoped service) instead of creating its own scope.
  /// </summary>
  private static ServiceProvider _createServiceProvider() {
    var services = new ServiceCollection();
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Wraps a message in an IMessageEnvelope for testing.
  /// </summary>
  private static MessageEnvelope<T> _wrapInEnvelope<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = []
    };
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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act - Try to invoke at PreOutboxInline (wrong stage)
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PreOutboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act - Invoke at PostInboxInline
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PreOutboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.LocalImmediateInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act - Invoke at PreInboxInline (NOT a default stage)
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PreInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("test");

    // Act & Assert - Should not throw
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);
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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

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

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), cascadeTracker);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - No events should have been cascaded
    await Assert.That(cascadeTracker.CascadedMessages).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Tracks which messages were cascaded (for testing purposes).
  /// </summary>
  private sealed class CascadeTracker : IEventCascader {
    private readonly List<IMessage> _cascadedMessages = [];
    public List<IMessage> CascadedMessages => _cascadedMessages;

    public Task CascadeFromResultAsync(object result, IMessageEnvelope? sourceEnvelope, DispatchMode? receptorDefault = null, CancellationToken cancellationToken = default) {
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

    /// <summary>
    /// Registers a receptor with a callback that runs during invocation.
    /// Used to test security context timing.
    /// </summary>
    public void RegisterReceptorWithCallback<TMessage>(
        string receptorId,
        LifecycleStage stage,
        Action callback) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }

      list.Add(new ReceptorInfo(
          typeof(TMessage),
          receptorId,
          (sp, msg, ct) => {
            callback();
            _tracker.RecordInvocation(receptorId, stage);
            return ValueTask.FromResult<object?>(null);
          }));
    }

    /// <summary>
    /// Registers a receptor that checks the service provider state.
    /// Used to verify scoped services have security context.
    /// </summary>
    public void RegisterReceptorWithServiceCheck<TMessage>(
        string receptorId,
        LifecycleStage stage,
        Action<IServiceProvider> checkCallback) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }

      list.Add(new ReceptorInfo(
          typeof(TMessage),
          receptorId,
          (sp, msg, ct) => {
            checkCallback(sp);
            _tracker.RecordInvocation(receptorId, stage);
            return ValueTask.FromResult<object?>(null);
          }));
    }

    /// <summary>
    /// Registers a receptor with sync attributes.
    /// Used to test [AwaitPerspectiveSync] attribute behavior.
    /// </summary>
    public void RegisterReceptorWithSyncAttributes<TMessage>(
        string receptorId,
        LifecycleStage stage,
        IReadOnlyList<ReceptorSyncAttributeInfo> syncAttributes,
        Action<List<string>>? callOrderCallback = null) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }

      list.Add(new ReceptorInfo(
          typeof(TMessage),
          receptorId,
          (sp, msg, ct) => {
            callOrderCallback?.Invoke([$"ReceptorInvoked:{receptorId}"]);
            _tracker.RecordInvocation(receptorId, stage);
            return ValueTask.FromResult<object?>(null);
          },
          SyncAttributes: syncAttributes));
    }
  }

  // ========================================
  // ENVELOPE-BASED INVOCATION TESTS
  // These tests are for the new envelope-based signature (TDD RED phase)
  // ========================================

  #region Envelope Extraction Tests

  /// <summary>
  /// Verifies that InvokeAsync with envelope extracts payload and invokes receptor.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithEnvelope_ExtractsPayloadAndInvokesReceptorAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var message = new TestMessage("envelope-test");
    var envelope = _createEnvelope(message);

    // Act - This will fail until interface changes to accept envelope
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that InvokeAsync with null envelope throws ArgumentNullException.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithNullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    // Act & Assert - This will fail until interface changes
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await invoker.InvokeAsync((IMessageEnvelope)null!, LifecycleStage.PostInboxInline));
  }

  #endregion

  #region Security Context Tests

  /// <summary>
  /// Verifies that security context is established BEFORE receptors are invoked.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithSecurityProvider_EstablishesContextBeforeReceptorAsync() {
    // Arrange
    var contextEstablished = false;
    var contextEstablishedBeforeReceptor = false;

    var securityProvider = new TestSecurityContextProvider(
        onEstablish: () => { contextEstablished = true; });

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    var provider = services.BuildServiceProvider();

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptorWithCallback<TestMessage>(
        "SecureReceptor",
        LifecycleStage.PostInboxInline,
        callback: () => { contextEstablishedBeforeReceptor = contextEstablished; });

    // Security provider is resolved from the service provider
    var invoker = new ReceptorInvoker(registry, provider, null);
    var envelope = _createEnvelope(new TestMessage("secure"));

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(contextEstablished).IsTrue();
    await Assert.That(contextEstablishedBeforeReceptor).IsTrue();
  }

  /// <summary>
  /// Verifies that IScopeContextAccessor.Current is set after security context establishment.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithSecurityProvider_SetsAccessorCurrentAsync() {
    // Arrange
    var expectedContext = new TestScopeContext();
    var securityProvider = new TestSecurityContextProvider(returns: expectedContext);

    var services = new ServiceCollection();
    var accessor = new TestScopeContextAccessor();
    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var provider = services.BuildServiceProvider();

    // Security provider is resolved from the service provider
    var invoker = new ReceptorInvoker(registry, provider, null);
    var envelope = _createEnvelope(new TestMessage("test"));

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - accessor should have been set during the scope
    await Assert.That(accessor.WasSet).IsTrue();
    await Assert.That(accessor.LastSetContext).IsEqualTo(expectedContext);
  }

  /// <summary>
  /// Verifies that when security provider returns null, accessor is not set.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SecurityProviderReturnsNull_DoesNotSetAccessorAsync() {
    // Arrange
    var securityProvider = new TestSecurityContextProvider(returns: null);

    var services = new ServiceCollection();
    var accessor = new TestScopeContextAccessor();
    services.AddSingleton<IScopeContextAccessor>(accessor);
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var provider = services.BuildServiceProvider();

    // Security provider is resolved from the service provider
    var invoker = new ReceptorInvoker(registry, provider, null);
    var envelope = _createEnvelope(new TestMessage("test"));

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(accessor.WasSet).IsFalse();
  }

  /// <summary>
  /// Verifies that without a security provider, receptors still get invoked (backwards compatibility).
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithNullSecurityProvider_StillInvokesReceptorsAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    // No security provider registered - invoker should handle this gracefully
    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), null);
    var envelope = _createEnvelope(new TestMessage("test"));

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that the envelope is passed to the security provider.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithSecurityProvider_PassesEnvelopeToProviderAsync() {
    // Arrange
    IMessageEnvelope? receivedEnvelope = null;
    var securityProvider = new TestSecurityContextProvider(
        onEstablish: () => { },
        captureEnvelope: env => { receivedEnvelope = env; });

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    var provider = services.BuildServiceProvider();

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, provider, null);

    var message = new TestMessage("envelope-test");
    var envelope = _createEnvelope(message);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(receivedEnvelope).IsNotNull();
    await Assert.That(receivedEnvelope!.MessageId).IsEqualTo(envelope.MessageId);
  }

  #endregion

  #region Test Helpers for Envelope Tests

  private static MessageEnvelope<TMessage> _createEnvelope<TMessage>(TMessage message) {
    return new MessageEnvelope<TMessage> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = message,
      Hops = [new MessageHop { Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
  }

  private sealed class TestSecurityContextProvider : IMessageSecurityContextProvider {
    private readonly Action? _onEstablish;
    private readonly IScopeContext? _returns;
    private readonly Action<IMessageEnvelope>? _captureEnvelope;

    public TestSecurityContextProvider(
        Action? onEstablish = null,
        IScopeContext? returns = null,
        Action<IMessageEnvelope>? captureEnvelope = null) {
      _onEstablish = onEstablish;
      _returns = returns;
      _captureEnvelope = captureEnvelope;
    }

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      _captureEnvelope?.Invoke(envelope);
      _onEstablish?.Invoke();
      return ValueTask.FromResult(_returns);
    }
  }

  private sealed class TestScopeContextAccessor : IScopeContextAccessor {
    public bool WasSet { get; private set; }
    public IScopeContext? LastSetContext { get; private set; }

    private IScopeContext? _current;
    public IScopeContext? Current {
      get => _current;
      set {
        WasSet = true;
        LastSetContext = value;
        _current = value;
      }
    }
  }

  private sealed class TestScopeContext : IScopeContext {
    public PerspectiveScope Scope => new();
    public IReadOnlySet<string> Roles => new HashSet<string>();
    public IReadOnlySet<Permission> Permissions => new HashSet<Permission>();
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => new HashSet<SecurityPrincipalId>();
    public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>();
    public string? ActualPrincipal => null;
    public string? EffectivePrincipal => null;
    public SecurityContextType ContextType => SecurityContextType.User;

    public bool HasPermission(Permission permission) => false;
    public bool HasAnyPermission(params Permission[] permissions) => false;
    public bool HasAllPermissions(params Permission[] permissions) => false;
    public bool HasRole(string roleName) => false;
    public bool HasAnyRole(params string[] roleNames) => false;
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => false;
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => false;
  }

  #endregion

  // ========================================
  // PERSPECTIVE SYNC ATTRIBUTE TESTS
  // ========================================

  #region Perspective Sync Attribute Tests

  /// <summary>
  /// Dummy perspective type for sync attribute tests.
  /// </summary>
  private sealed class TestPerspective { }

  /// <summary>
  /// Another perspective type for multi-attribute tests.
  /// </summary>
  private sealed class TestPerspective2 { }

  /// <summary>
  /// Verifies that a receptor with [AwaitPerspectiveSync] attribute calls the sync awaiter.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorWithSyncAttribute_AwaitsBeforeInvokingAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var syncAwaiter = new TestSyncAwaiter();
    var registry = new TestReceptorRegistry(tracker);

    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        LookupMode: SyncLookupMode.Local,
        TimeoutMs: 5000,
        ThrowOnTimeout: false
    );

    registry.RegisterReceptorWithSyncAttributes<TestMessage>(
        "SyncReceptor",
        LifecycleStage.PostInboxInline,
        [syncAttr],
        callOrderCallback: items => syncAwaiter.CallOrder.AddRange(items)
    );

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), null, syncAwaiter);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - Sync awaiter was called before receptor
    await Assert.That(syncAwaiter.WaitCalls).Count().IsEqualTo(1);
    await Assert.That(syncAwaiter.WaitCalls[0].PerspectiveType).IsEqualTo(typeof(TestPerspective));
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
    await Assert.That(syncAwaiter.CallOrder[0]).IsEqualTo("Wait:TestPerspective");
    await Assert.That(syncAwaiter.CallOrder[1]).IsEqualTo("ReceptorInvoked:SyncReceptor");
  }

  /// <summary>
  /// Verifies that a receptor with ThrowOnTimeout=true throws PerspectiveSyncTimeoutException when timed out.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttributeThrowOnTimeoutAndTimedOut_ThrowsExceptionAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var syncAwaiter = new TestSyncAwaiter {
      SimulateTimeout = true
    };
    var registry = new TestReceptorRegistry(tracker);

    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        LookupMode: SyncLookupMode.Local,
        TimeoutMs: 5000,
        ThrowOnTimeout: true  // Should throw when timed out
    );

    registry.RegisterReceptorWithSyncAttributes<TestMessage>(
        "SyncReceptor",
        LifecycleStage.PostInboxInline,
        [syncAttr]
    );

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), null, syncAwaiter);
    var message = new TestMessage("test");

    // Act & Assert
    var exception = await Assert.ThrowsAsync<PerspectiveSyncTimeoutException>(async () =>
        await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline));

    await Assert.That(exception!.PerspectiveType!).IsEqualTo(typeof(TestPerspective));
    await Assert.That(exception.Timeout).IsEqualTo(TimeSpan.FromMilliseconds(5000));
    await Assert.That(tracker.Invocations).Count().IsEqualTo(0); // Receptor not invoked
  }

  /// <summary>
  /// Verifies that a receptor with ThrowOnTimeout=false does NOT throw when timed out.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttributeNoThrowOnTimeoutAndTimedOut_InvokesReceptorAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var syncAwaiter = new TestSyncAwaiter {
      SimulateTimeout = true
    };
    var registry = new TestReceptorRegistry(tracker);

    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        LookupMode: SyncLookupMode.Local,
        TimeoutMs: 5000,
        ThrowOnTimeout: false  // Should not throw
    );

    registry.RegisterReceptorWithSyncAttributes<TestMessage>(
        "SyncReceptor",
        LifecycleStage.PostInboxInline,
        [syncAttr]
    );

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), null, syncAwaiter);
    var message = new TestMessage("test");

    // Act - should not throw
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - receptor was invoked despite timeout
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that multiple sync attributes are all awaited in order.
  /// </summary>
  [Test]
  public async Task InvokeAsync_MultipleSyncAttributes_AwaitsAllInOrderAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var syncAwaiter = new TestSyncAwaiter();
    var registry = new TestReceptorRegistry(tracker);

    var syncAttr1 = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        LookupMode: SyncLookupMode.Local,
        TimeoutMs: 5000,
        ThrowOnTimeout: false
    );
    var syncAttr2 = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective2),
        EventTypes: [typeof(TestEvent)],
        LookupMode: SyncLookupMode.Distributed,
        TimeoutMs: 3000,
        ThrowOnTimeout: false
    );

    registry.RegisterReceptorWithSyncAttributes<TestMessage>(
        "SyncReceptor",
        LifecycleStage.PostInboxInline,
        [syncAttr1, syncAttr2]
    );

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), null, syncAwaiter);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - Both sync attributes awaited
    await Assert.That(syncAwaiter.WaitCalls).Count().IsEqualTo(2);
    await Assert.That(syncAwaiter.WaitCalls[0].PerspectiveType).IsEqualTo(typeof(TestPerspective));
    await Assert.That(syncAwaiter.WaitCalls[1].PerspectiveType).IsEqualTo(typeof(TestPerspective2));
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that receptor without sync attributes invokes directly (no sync awaiter call).
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorWithoutSyncAttribute_DoesNotCallSyncAwaiterAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var syncAwaiter = new TestSyncAwaiter();
    var registry = new TestReceptorRegistry(tracker);

    // Register receptor without sync attributes
    registry.RegisterReceptor<TestMessage>("NormalReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), null, syncAwaiter);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - Sync awaiter was NOT called
    await Assert.That(syncAwaiter.WaitCalls).Count().IsEqualTo(0);
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that sync options are correctly built from attribute info.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttribute_PassesCorrectOptionsToAwaiterAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var syncAwaiter = new TestSyncAwaiter();
    var registry = new TestReceptorRegistry(tracker);

    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: [typeof(TestEvent), typeof(TestMessage)],
        LookupMode: SyncLookupMode.Distributed,
        TimeoutMs: 7500,
        ThrowOnTimeout: false
    );

    registry.RegisterReceptorWithSyncAttributes<TestMessage>(
        "SyncReceptor",
        LifecycleStage.PostInboxInline,
        [syncAttr]
    );

    var invoker = new ReceptorInvoker(registry, _createServiceProvider(), null, syncAwaiter);
    var message = new TestMessage("test");

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - Options are correct
    await Assert.That(syncAwaiter.WaitCalls).Count().IsEqualTo(1);
    var call = syncAwaiter.WaitCalls[0];
    await Assert.That(call.Options.LookupMode).IsEqualTo(SyncLookupMode.Distributed);
    await Assert.That(call.Options.Timeout).IsEqualTo(TimeSpan.FromMilliseconds(7500));
    await Assert.That(call.Options.Filter).IsTypeOf<EventTypeFilter>();
  }

  /// <summary>
  /// Test sync awaiter that tracks wait calls.
  /// </summary>
  private sealed class TestSyncAwaiter : IPerspectiveSyncAwaiter {
    public List<(Type PerspectiveType, PerspectiveSyncOptions Options)> WaitCalls { get; } = [];
    public List<string> CallOrder { get; } = [];
    public bool SimulateTimeout { get; set; }

    public Task<SyncResult> WaitAsync(
        Type perspectiveType,
        PerspectiveSyncOptions options,
        CancellationToken ct = default) {
      WaitCalls.Add((perspectiveType, options));
      CallOrder.Add($"Wait:{perspectiveType.Name}");

      var outcome = SimulateTimeout
          ? SyncOutcome.TimedOut
          : SyncOutcome.Synced;

      return Task.FromResult(new SyncResult(
          Outcome: outcome,
          EventsAwaited: 1,
          ElapsedTime: TimeSpan.FromMilliseconds(100)));
    }

    public Task<bool> IsCaughtUpAsync(
        Type perspectiveType,
        PerspectiveSyncOptions options,
        CancellationToken ct = default) {
      return Task.FromResult(!SimulateTimeout);
    }
  }

  #endregion
}
