using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
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
    private IMessageContext? _initiatingContext;

    public IScopeContext? Current {
      get => _current;
      set {
        WasSet = true;
        LastSetContext = value;
        _current = value;
      }
    }

    public IMessageContext? InitiatingContext {
      get => _initiatingContext;
      set => _initiatingContext = value;
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
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireAlways
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
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireOnSuccess  // Should throw when timed out
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
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireAlways  // Should not throw
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
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireAlways
    );
    var syncAttr2 = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective2),
        EventTypes: [typeof(TestEvent)],
        TimeoutMs: 3000,
        FireBehavior: SyncFireBehavior.FireAlways
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
        TimeoutMs: 7500,
        FireBehavior: SyncFireBehavior.FireAlways
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

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) {
      CallOrder.Add($"WaitForStream:{perspectiveType.Name}:{streamId}");

      var outcome = SimulateTimeout
          ? SyncOutcome.TimedOut
          : SyncOutcome.Synced;

      return Task.FromResult(new SyncResult(
          Outcome: outcome,
          EventsAwaited: 1,
          ElapsedTime: TimeSpan.FromMilliseconds(100)));
    }
  }

  #endregion

  // ========================================
  // ROUTED<T> UNWRAPPING TESTS
  // ========================================

  #region Routed<T> Unwrapping Tests

  /// <summary>
  /// Verifies that when the envelope payload contains Routed&lt;T&gt;, the inner message is extracted
  /// and used to find receptors.
  /// </summary>
  [Test]
  public async Task InvokeAsync_EnvelopeWithRoutedPayload_UnwrapsAndInvokesReceptorAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register receptor for TestMessage (not for Routed<TestMessage>)
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    // Create envelope with Routed<TestMessage> as payload
    var innerMessage = new TestMessage("unwrap-test");
    var routedPayload = Route.Local(innerMessage);
    var envelope = new MessageEnvelope<Routed<TestMessage>> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = routedPayload,
      Hops = []
    };

    // Act - This should unwrap Routed<T> and find receptor for TestMessage
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - Receptor for TestMessage should be invoked
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
    await Assert.That(tracker.Invocations[0].ReceptorId).IsEqualTo("TestReceptor");
  }

  /// <summary>
  /// Verifies that when the envelope payload contains Route.Outbox&lt;T&gt;, the inner message is extracted.
  /// </summary>
  [Test]
  public async Task InvokeAsync_EnvelopeWithRouteOutboxPayload_UnwrapsAndInvokesReceptorAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    var innerMessage = new TestMessage("outbox-test");
    var routedPayload = Route.Outbox(innerMessage);
    var envelope = new MessageEnvelope<Routed<TestMessage>> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = routedPayload,
      Hops = []
    };

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that when the envelope payload contains Route.Both&lt;T&gt;, the inner message is extracted.
  /// </summary>
  [Test]
  public async Task InvokeAsync_EnvelopeWithRouteBothPayload_UnwrapsAndInvokesReceptorAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    var innerMessage = new TestMessage("both-test");
    var routedPayload = Route.Both(innerMessage);
    var envelope = new MessageEnvelope<Routed<TestMessage>> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = routedPayload,
      Hops = []
    };

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that when the envelope payload contains Route.None(), the invoker returns early.
  /// </summary>
  [Test]
  public async Task InvokeAsync_EnvelopeWithRouteNonePayload_ReturnsEarlyWithoutInvokingAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    // Create envelope with RoutedNone as payload
    var routedNone = Route.None();
    var envelope = new MessageEnvelope<RoutedNone> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = routedNone,
      Hops = []
    };

    // Act - This should return early without error
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - No receptor should be invoked
    await Assert.That(tracker.Invocations).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Verifies that the unwrapped message is passed to the receptor delegate, not the Routed wrapper.
  /// </summary>
  [Test]
  public async Task InvokeAsync_RoutedPayload_PassesUnwrappedMessageToReceptorDelegateAsync() {
    // Arrange
    var customRegistry = new MessageCapturingRegistry();

    var invoker = new ReceptorInvoker(customRegistry, _createServiceProvider());

    var innerMessage = new TestMessage("capture-test");
    var routedPayload = Route.Local(innerMessage);
    var envelope = new MessageEnvelope<Routed<TestMessage>> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = routedPayload,
      Hops = []
    };

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - The captured message should be TestMessage, not Routed<TestMessage>
    await Assert.That(customRegistry.ReceivedMessage).IsNotNull();
    await Assert.That(customRegistry.ReceivedMessage).IsTypeOf<TestMessage>();
    await Assert.That(((TestMessage)customRegistry.ReceivedMessage!).Value).IsEqualTo("capture-test");
  }

  /// <summary>
  /// Custom registry that captures the message passed to the receptor delegate.
  /// </summary>
  private sealed class MessageCapturingRegistry : IReceptorRegistry {
    public object? ReceivedMessage { get; private set; }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      // Only respond to TestMessage type
      if (messageType == typeof(TestMessage) && stage == LifecycleStage.PostInboxInline) {
        return [new ReceptorInfo(
            typeof(TestMessage),
            "CaptureReceptor",
            (sp, msg, ct) => {
              ReceivedMessage = msg;
              return ValueTask.FromResult<object?>(null);
            })];
      }
      return [];
    }
  }

  #endregion

  // ========================================
  // STREAM-BASED SYNC TESTS (with IStreamIdExtractor)
  // ========================================

  #region Stream-Based Sync Tests

  /// <summary>
  /// Verifies that when a receptor has sync attributes and an IStreamIdExtractor is available,
  /// the invoker uses WaitForStreamAsync instead of WaitAsync.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttribute_UsesWaitForStreamAsyncWhenExtractorAvailableAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    var streamId = Guid.NewGuid();

    var services = new ServiceCollection();
    services.AddSingleton<IStreamIdExtractor>(new TestStreamIdExtractor(streamId));
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var syncAwaiter = new StreamIdTrackingSyncAwaiter(streamId);

    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireOnSuccess);

    registry.RegisterReceptorWithSyncAttributes<TestMessageWithStreamId>(
        "TestReceptor",
        LifecycleStage.PostInboxInline,
        [syncAttr]);

    var invoker = new ReceptorInvoker(registry, scopedProvider, null, syncAwaiter);
    var message = new TestMessageWithStreamId { Value = "test", StreamId = streamId };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - Should use WaitForStreamAsync, not WaitAsync
    await Assert.That(syncAwaiter.WaitForStreamCalls).Count().IsGreaterThan(0);
    await Assert.That(syncAwaiter.WaitAsyncCalls).Count().IsEqualTo(0);
  }

  /// <summary>
  /// Verifies that the extracted StreamId is passed correctly to WaitForStreamAsync.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttribute_PassesExtractedStreamIdToAwaiterAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    var streamId = Guid.NewGuid();

    var services = new ServiceCollection();
    services.AddSingleton<IStreamIdExtractor>(new TestStreamIdExtractor(streamId));
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var syncAwaiter = new StreamIdTrackingSyncAwaiter(streamId);

    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireOnSuccess);

    registry.RegisterReceptorWithSyncAttributes<TestMessageWithStreamId>(
        "TestReceptor",
        LifecycleStage.PostInboxInline,
        [syncAttr]);

    var invoker = new ReceptorInvoker(registry, scopedProvider, null, syncAwaiter);
    var message = new TestMessageWithStreamId { Value = "test", StreamId = streamId };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(syncAwaiter.WaitForStreamCalls).Count().IsGreaterThan(0);
    var call = syncAwaiter.WaitForStreamCalls[0];
    await Assert.That(call.StreamId).IsEqualTo(streamId);
    await Assert.That(call.PerspectiveType).IsEqualTo(typeof(TestPerspective));
  }

  /// <summary>
  /// Verifies that SyncContext is registered in scope after sync completes.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttribute_RegistersSyncContextInScopeAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var streamId = Guid.NewGuid();

    var services = new ServiceCollection();
    services.AddSingleton<IStreamIdExtractor>(new TestStreamIdExtractor(streamId));
    var provider = services.BuildServiceProvider();
    var scope = provider.CreateScope();
    var scopedProvider = scope.ServiceProvider;

    var syncAwaiter = new StreamIdTrackingSyncAwaiter(streamId);

    SyncContext? capturedContext = null;
    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireOnSuccess);

    // Create custom registry that captures SyncContext
    var registry = new ContextCapturingRegistry(
        "ContextCapturingReceptor",
        [syncAttr],
        ctx => capturedContext = ctx);

    var invoker = new ReceptorInvoker(registry, scopedProvider, null, syncAwaiter);
    var message = new TestMessageWithStreamId { Value = "test", StreamId = streamId };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(capturedContext).IsNotNull();
    await Assert.That(capturedContext!.StreamId).IsEqualTo(streamId);
    await Assert.That(capturedContext.PerspectiveType).IsEqualTo(typeof(TestPerspective));
    await Assert.That(capturedContext.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  /// <summary>
  /// Verifies that SyncContext contains correct values including elapsed time.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttribute_SyncContextHasCorrectElapsedTimeAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var streamId = Guid.NewGuid();
    var expectedElapsed = TimeSpan.FromMilliseconds(150);

    var services = new ServiceCollection();
    services.AddSingleton<IStreamIdExtractor>(new TestStreamIdExtractor(streamId));
    var provider = services.BuildServiceProvider();
    var scope = provider.CreateScope();
    var scopedProvider = scope.ServiceProvider;

    var syncAwaiter = new StreamIdTrackingSyncAwaiter(streamId, elapsedTime: expectedElapsed);

    SyncContext? capturedContext = null;
    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireOnSuccess);

    var registry = new ContextCapturingRegistry(
        "ContextCapturingReceptor",
        [syncAttr],
        ctx => capturedContext = ctx);

    var invoker = new ReceptorInvoker(registry, scopedProvider, null, syncAwaiter);
    var message = new TestMessageWithStreamId { Value = "test", StreamId = streamId };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(capturedContext).IsNotNull();
    await Assert.That(capturedContext!.ElapsedTime).IsEqualTo(expectedElapsed);
  }

  /// <summary>
  /// Verifies that SyncContext has failure reason set when sync times out.
  /// </summary>
  [Test]
  public async Task InvokeAsync_SyncAttribute_SyncContextHasFailureReasonOnTimeoutAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var streamId = Guid.NewGuid();

    var services = new ServiceCollection();
    services.AddSingleton<IStreamIdExtractor>(new TestStreamIdExtractor(streamId));
    var provider = services.BuildServiceProvider();
    var scope = provider.CreateScope();
    var scopedProvider = scope.ServiceProvider;

    var syncAwaiter = new StreamIdTrackingSyncAwaiter(streamId, simulateTimeout: true);

    SyncContext? capturedContext = null;
    var syncAttr = new ReceptorSyncAttributeInfo(
        PerspectiveType: typeof(TestPerspective),
        EventTypes: null,
        TimeoutMs: 5000,
        FireBehavior: SyncFireBehavior.FireAlways);  // FireAlways so handler runs

    var registry = new ContextCapturingRegistry(
        "ContextCapturingReceptor",
        [syncAttr],
        ctx => capturedContext = ctx);

    var invoker = new ReceptorInvoker(registry, scopedProvider, null, syncAwaiter);
    var message = new TestMessageWithStreamId { Value = "test", StreamId = streamId };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(capturedContext).IsNotNull();
    await Assert.That(capturedContext!.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(capturedContext.FailureReason).IsNotNull();
  }

  /// <summary>
  /// Test message with StreamId property for stream-based sync tests.
  /// </summary>
  private sealed record TestMessageWithStreamId : IMessage {
    [StreamId]
    public Guid StreamId { get; init; }
    public string Value { get; init; } = string.Empty;
  }

  /// <summary>
  /// Test implementation of IStreamIdExtractor that returns a configured StreamId.
  /// </summary>
  private sealed class TestStreamIdExtractor : IStreamIdExtractor {
    private readonly Guid _streamId;

    public TestStreamIdExtractor(Guid streamId) => _streamId = streamId;

    public Guid? ExtractStreamId(object message, Type messageType) => _streamId;
  }

  /// <summary>
  /// Custom registry that captures SyncContext when receptor is invoked.
  /// </summary>
  private sealed class ContextCapturingRegistry : IReceptorRegistry {
    private readonly string _receptorId;
    private readonly IReadOnlyList<ReceptorSyncAttributeInfo> _syncAttributes;
    private readonly Action<SyncContext?> _contextCallback;

    public ContextCapturingRegistry(
        string receptorId,
        IReadOnlyList<ReceptorSyncAttributeInfo> syncAttributes,
        Action<SyncContext?> contextCallback) {
      _receptorId = receptorId;
      _syncAttributes = syncAttributes;
      _contextCallback = contextCallback;
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      if (messageType == typeof(TestMessageWithStreamId) && stage == LifecycleStage.PostInboxInline) {
        return [new ReceptorInfo(
            typeof(TestMessageWithStreamId),
            _receptorId,
            (sp, msg, ct) => {
              // Try to get SyncContext from accessor (AsyncLocal pattern)
              _contextCallback(SyncContextAccessor.CurrentContext);
              return ValueTask.FromResult<object?>(null);
            },
            SyncAttributes: _syncAttributes)];
      }
      return [];
    }
  }

  /// <summary>
  /// Sync awaiter that tracks calls to WaitAsync and WaitForStreamAsync separately.
  /// </summary>
  private sealed class StreamIdTrackingSyncAwaiter : IPerspectiveSyncAwaiter {
    private readonly Guid _expectedStreamId;
    private readonly bool _simulateTimeout;
    private readonly TimeSpan _elapsedTime;

    public List<(Type PerspectiveType, PerspectiveSyncOptions Options)> WaitAsyncCalls { get; } = [];
    public List<(Type PerspectiveType, Guid StreamId, Type[]? EventTypes, TimeSpan Timeout)> WaitForStreamCalls { get; } = [];

    public StreamIdTrackingSyncAwaiter(
        Guid streamId,
        bool simulateTimeout = false,
        TimeSpan? elapsedTime = null) {
      _expectedStreamId = streamId;
      _simulateTimeout = simulateTimeout;
      _elapsedTime = elapsedTime ?? TimeSpan.FromMilliseconds(100);
    }

    public Task<SyncResult> WaitAsync(
        Type perspectiveType,
        PerspectiveSyncOptions options,
        CancellationToken ct = default) {
      WaitAsyncCalls.Add((perspectiveType, options));
      var outcome = _simulateTimeout ? SyncOutcome.TimedOut : SyncOutcome.Synced;
      return Task.FromResult(new SyncResult(outcome, 1, _elapsedTime));
    }

    public Task<bool> IsCaughtUpAsync(
        Type perspectiveType,
        PerspectiveSyncOptions options,
        CancellationToken ct = default) {
      return Task.FromResult(!_simulateTimeout);
    }

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) {
      WaitForStreamCalls.Add((perspectiveType, streamId, eventTypes, timeout));
      var outcome = _simulateTimeout ? SyncOutcome.TimedOut : SyncOutcome.Synced;
      return Task.FromResult(new SyncResult(outcome, 1, _elapsedTime));
    }
  }

  #endregion

  // ========================================
  // ADDITIONAL COVERAGE TESTS
  // ========================================

  #region Additional Coverage Tests

  /// <summary>
  /// Verifies that trace parent context is extracted from envelope hops.
  /// </summary>
  [Test]
  public async Task InvokeAsync_EnvelopeWithTraceParent_ExtractsParentContextAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    // Create envelope with TraceParent in hops
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = new TestMessage("trace-test"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
        }
      ]
    };

    // Act - should not throw and should use the trace parent
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(tracker.Invocations).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that message context accessor is set with correct values.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithMessageContextAccessor_SetsMessageContextAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("TestReceptor", LifecycleStage.PostInboxInline);

    var services = new ServiceCollection();
    var accessor = new TestMessageContextAccessor();
    services.AddSingleton<IMessageContextAccessor>(accessor);
    var provider = services.BuildServiceProvider();

    var invoker = new ReceptorInvoker(registry, provider);

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.From(Guid.CreateVersion7()),
      Payload = new TestMessage("context-test"),
      Hops = []
    };

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(accessor.WasSet).IsTrue();
    await Assert.That(accessor.LastSetContext).IsNotNull();
    await Assert.That(accessor.LastSetContext!.MessageId).IsEqualTo(envelope.MessageId);
  }

  /// <summary>
  /// Verifies that receptor exceptions propagate correctly.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ReceptorThrows_PropagatesExceptionAsync() {
    // Arrange
    var registry = new ThrowingReceptorRegistry();
    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    var envelope = _wrapInEnvelope(new TestMessage("throw-test"));

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline));
  }

  /// <summary>
  /// Verifies that NullReceptorInvoker does nothing.
  /// </summary>
  [Test]
  public async Task NullReceptorInvoker_InvokeAsync_DoesNothingAsync() {
    // Arrange
    var invoker = new NullReceptorInvoker();
    var envelope = _wrapInEnvelope(new TestMessage("null-test"));

    // Act - should not throw and complete successfully
    var completed = false;
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);
    completed = true;

    // Assert - completed without throwing
    await Assert.That(completed).IsTrue();
  }

  /// <summary>
  /// Verifies constructor throws on null registry.
  /// </summary>
  [Test]
  public async Task Constructor_NullRegistry_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new ReceptorInvoker(null!, _createServiceProvider())));
  }

  /// <summary>
  /// Verifies constructor throws on null service provider.
  /// </summary>
  [Test]
  public async Task Constructor_NullServiceProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new ReceptorInvoker(registry, null!)));
  }

  /// <summary>
  /// Test message context accessor that tracks when it was set.
  /// </summary>
  private sealed class TestMessageContextAccessor : IMessageContextAccessor {
    public bool WasSet { get; private set; }
    public IMessageContext? LastSetContext { get; private set; }

    private IMessageContext? _current;
    public IMessageContext? Current {
      get => _current;
      set {
        WasSet = true;
        LastSetContext = value;
        _current = value;
      }
    }
  }

  /// <summary>
  /// Registry that throws an exception when receptor is invoked.
  /// </summary>
  private sealed class ThrowingReceptorRegistry : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      if (messageType == typeof(TestMessage) && stage == LifecycleStage.PostInboxInline) {
        return [new ReceptorInfo(
            typeof(TestMessage),
            "ThrowingReceptor",
            (sp, msg, ct) => throw new InvalidOperationException("Test exception"))];
      }
      return [];
    }
  }

  #endregion
}
