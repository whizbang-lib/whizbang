using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for security context propagation when receptors return events that get cascaded.
/// Verifies that CascadeContext.GetSecurityFromAmbient() works correctly during event cascading.
/// </summary>
/// <docs>core-concepts/scope-propagation</docs>
public class ReceptorInvokerScopePropagationTests {
  private sealed record TestCommand(string Value) : IMessage;
  private sealed record TestEvent(string Data) : IEvent;

  /// <summary>
  /// Verifies that when a receptor returns an event, the security context is available
  /// via CascadeContext.GetSecurityFromAmbient() during cascading.
  ///
  /// This test reproduces the bug where scope is not propagated to cascaded events
  /// because ReceptorInvoker sets a non-ImmutableScopeContext on the accessor,
  /// and GetSecurityFromAmbient() only works with ImmutableScopeContext.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WhenReceptorReturnsEvent_GetSecurityFromAmbientShouldReturnScopeAsync() {
    // Arrange
    var expectedTenantId = "test-tenant-123";
    var expectedUserId = "test-user-456";

    // Security context returned by provider - MUST be ImmutableScopeContext with ShouldPropagate=true
    // This matches what DefaultMessageSecurityContextProvider returns
    var testScopeContext = new TestScopeContext(expectedTenantId, expectedUserId);
    var securityContext = testScopeContext.ToImmutable(shouldPropagate: true);
    var securityProvider = new TestSecurityContextProvider(returns: securityContext);

    // Capture what GetSecurityFromAmbient returns during cascade
    SecurityContext? capturedAmbientContext = null;
    var cascader = new TestEventCascader(onCascade: () => {
      // This is called during CascadeFromResultAsync - simulating what PublishToOutboxAsync does
      capturedAmbientContext = CascadeContext.GetSecurityFromAmbient();
    });

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    // CRITICAL: Must register IScopeContextAccessor so security context gets set on AsyncLocal
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register receptor that returns an event
    registry.RegisterReceptorThatReturnsEvent<TestCommand, TestEvent>(
      "EventReturningReceptor",
      LifecycleStage.PostInboxInline,
      () => new TestEvent("cascaded-data"));

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider, cascader);
    var envelope = _createEnvelope(new TestCommand("test"));

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - GetSecurityFromAmbient should have returned the scope during cascade
    await Assert.That(capturedAmbientContext).IsNotNull()
      .Because("GetSecurityFromAmbient should return context when receptor cascades events");
    await Assert.That(capturedAmbientContext!.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturedAmbientContext.UserId).IsEqualTo(expectedUserId);
  }

  /// <summary>
  /// Verifies that when the security provider returns NULL but the envelope has scope in hops,
  /// the scope is STILL available via GetSecurityFromAmbient() during event cascading.
  ///
  /// This is the EXACT BUG SCENARIO: Events arrive from another service with scope in hops,
  /// but no JWT/token extraction happens (security provider returns null). The scope from
  /// hops should still be wrapped in ImmutableScopeContext and propagated.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WhenSecurityProviderReturnsNull_ButEnvelopeHasScopeInHops_GetSecurityFromAmbientShouldReturnScopeAsync() {
    // Arrange
    var expectedTenantId = "hop-tenant-from-bff";
    var expectedUserId = "hop-user-from-bff";

    // Security provider returns NULL - simulating no JWT/token extraction
    var securityProvider = new TestSecurityContextProvider(returns: null);

    // Capture what GetSecurityFromAmbient returns during cascade
    SecurityContext? capturedAmbientContext = null;
    var cascader = new TestEventCascader(onCascade: () => {
      capturedAmbientContext = CascadeContext.GetSecurityFromAmbient();
    });

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    // CRITICAL: Must register IMessageContextAccessor for the fix to apply
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    // Register receptor that returns an event (triggers cascading)
    registry.RegisterReceptorThatReturnsEvent<TestCommand, TestEvent>(
      "EventReturningReceptor",
      LifecycleStage.PostInboxInline,
      () => new TestEvent("cascaded-data"));

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider, cascader);

    // Create envelope WITH scope in hops - simulating event from BffService with authenticated user
    var envelope = _createEnvelopeWithScopeInHops(new TestCommand("test"), expectedTenantId, expectedUserId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - GetSecurityFromAmbient should return scope from hops even when provider returns null
    await Assert.That(capturedAmbientContext).IsNotNull()
      .Because("GetSecurityFromAmbient should return context from envelope hops when security provider returns null");
    await Assert.That(capturedAmbientContext!.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturedAmbientContext.UserId).IsEqualTo(expectedUserId);
  }

  /// <summary>
  /// Verifies that MessageContext.ScopeContext is set from the established security context,
  /// not from envelope.GetCurrentScope() which may be null.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WhenSecurityProviderReturnsContext_MessageContextShouldHaveScopeContextAsync() {
    // Arrange
    var expectedTenantId = "test-tenant-789";
    var expectedUserId = "test-user-abc";

    // Security context returned by provider - matches DefaultMessageSecurityContextProvider
    var testScopeContext = new TestScopeContext(expectedTenantId, expectedUserId);
    var securityContext = testScopeContext.ToImmutable(shouldPropagate: true);
    var securityProvider = new TestSecurityContextProvider(returns: securityContext);

    // Capture the message context that gets set
    IMessageContext? capturedMessageContext = null;
    var messageContextAccessor = new TestMessageContextAccessor(onSet: ctx => {
      capturedMessageContext = ctx;
    });

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddSingleton<IMessageContextAccessor>(messageContextAccessor);
    var provider = services.BuildServiceProvider();

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestCommand>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, provider, null);

    // Create envelope WITHOUT scope in hops (simulating incoming command without hop scope)
    var envelope = _createEnvelopeWithoutScope(new TestCommand("test"));

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - MessageContext.ScopeContext should be from security provider, not envelope
    await Assert.That(capturedMessageContext).IsNotNull();
    await Assert.That(capturedMessageContext!.ScopeContext).IsNotNull()
      .Because("ScopeContext should come from security provider when envelope has no scope");
    await Assert.That(capturedMessageContext.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturedMessageContext.UserId).IsEqualTo(expectedUserId);
  }

  #region Test Helpers

  private static MessageEnvelope<T> _createEnvelope<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = [new MessageHop { Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
  }

  private static MessageEnvelope<T> _createEnvelopeWithoutScope<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = null // Explicitly no scope in hops
      }]
    };
  }

  private static MessageEnvelope<T> _createEnvelopeWithScopeInHops<T>(T message, string tenantId, string userId) where T : notnull {
    // Create a ScopeDelta from SecurityContext - this is how hops carry scope
    var scopeDelta = ScopeDelta.FromSecurityContext(new SecurityContext {
      TenantId = tenantId,
      UserId = userId
    });

    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        // Scope in hop - simulating event arriving from BffService with authenticated user
        Scope = scopeDelta
      }]
    };
  }

  private sealed class TestSecurityContextProvider(IScopeContext? returns = null) : IMessageSecurityContextProvider {
    private readonly IScopeContext? _returns = returns;

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(_returns);
    }
  }

  /// <summary>
  /// Creates an ImmutableScopeContext for testing - matches what DefaultMessageSecurityContextProvider returns.
  /// </summary>
  private sealed class TestScopeContext : IScopeContext {
    public PerspectiveScope Scope { get; }
    public IReadOnlySet<string> Roles => new HashSet<string>();
    public IReadOnlySet<Permission> Permissions => new HashSet<Permission>();
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => new HashSet<SecurityPrincipalId>();
    public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>();
    public string? ActualPrincipal => null;
    public string? EffectivePrincipal => null;
    public SecurityContextType ContextType => SecurityContextType.User;

    public TestScopeContext(string tenantId, string userId) {
      Scope = new PerspectiveScope { TenantId = tenantId, UserId = userId };
    }

    public bool HasPermission(Permission permission) => false;
    public bool HasAnyPermission(params Permission[] permissions) => false;
    public bool HasAllPermissions(params Permission[] permissions) => false;
    public bool HasRole(string roleName) => false;
    public bool HasAnyRole(params string[] roleNames) => false;
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => false;
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => false;

    /// <summary>
    /// Creates an ImmutableScopeContext from this test context - matches DefaultMessageSecurityContextProvider behavior.
    /// </summary>
    public ImmutableScopeContext ToImmutable(bool shouldPropagate = true) {
      var extraction = new SecurityExtraction {
        Scope = Scope,
        Roles = Roles,
        Permissions = Permissions,
        SecurityPrincipals = SecurityPrincipals,
        Claims = Claims,
        Source = "Test",
        ActualPrincipal = ActualPrincipal,
        EffectivePrincipal = EffectivePrincipal,
        ContextType = ContextType
      };
      return new ImmutableScopeContext(extraction, shouldPropagate);
    }
  }

  private sealed class TestEventCascader(System.Action? onCascade = null) : IEventCascader {
    private readonly System.Action? _onCascade = onCascade;

    public Task CascadeFromResultAsync(object result, IMessageEnvelope? sourceEnvelope, DispatchMode? receptorDefault = null, CancellationToken cancellationToken = default) {
      _onCascade?.Invoke();
      return Task.CompletedTask;
    }
  }

  private sealed class TestMessageContextAccessor(System.Action<IMessageContext?>? onSet = null) : IMessageContextAccessor {
    private readonly System.Action<IMessageContext?>? _onSet = onSet;
    private IMessageContext? _current;

    public IMessageContext? Current {
      get => _current;
      set {
        _current = value;
        _onSet?.Invoke(value);
      }
    }
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
  /// Test receptor registry that allows registering receptors that return events.
  /// </summary>
  private sealed class TestReceptorRegistry(ReceptorInvokerScopePropagationTests.InvocationTracker tracker) : IReceptorRegistry {
    private readonly Dictionary<(System.Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];
    private readonly InvocationTracker _tracker = tracker;

    public void RegisterReceptor<TMessage>(string receptorId, LifecycleStage stage) where TMessage : notnull {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: receptorId,
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _tracker.RecordInvocation(receptorId, stage);
          return ValueTask.FromResult<object?>(null); // Return null (no cascade)
        }));
    }

    public void RegisterReceptorThatReturnsEvent<TCommand, TEvent>(
        string receptorId,
        LifecycleStage stage,
        System.Func<TEvent> eventFactory)
        where TCommand : notnull
        where TEvent : IEvent {
      var key = (typeof(TCommand), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(new ReceptorInfo(
        MessageType: typeof(TCommand),
        ReceptorId: receptorId,
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _tracker.RecordInvocation(receptorId, stage);
          return ValueTask.FromResult<object?>(eventFactory()); // Return event for cascading
        }));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(System.Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  #endregion
}
