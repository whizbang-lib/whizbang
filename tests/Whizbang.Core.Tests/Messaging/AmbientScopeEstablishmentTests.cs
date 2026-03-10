using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests that lifecycle invokers establish ambient ScopeContext from envelope hops
/// via ScopeContextAccessor.CurrentContext (AsyncLocal-based propagation).
/// </summary>
/// <docs>core-concepts/scope-propagation</docs>
[Category("Messaging")]
[Category("Security")]
public class AmbientScopeEstablishmentTests {

  [Test]
  public async Task RuntimeLifecycleInvoker_EstablishesAmbientScope_FromEnvelopeHopsAsync() {
    // Arrange
    IScopeContext? capturedScope = null;

    var registry = new TestLifecycleReceptorRegistry();
    registry.RegisterHandler<AmbientScopeTestEvent>(LifecycleStage.PreOutboxInline, (message, context, ct) => {
      // Capture the ambient scope during handler execution
      capturedScope = ScopeContextAccessor.CurrentContext;
      return ValueTask.CompletedTask;
    });

    var invoker = new RuntimeLifecycleInvoker(registry);
    var envelope = _createEnvelopeWithScope("tenant-abc", "user-xyz");

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

    // Assert - handler should have seen the scope from envelope hops
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope?.TenantId).IsEqualTo("tenant-abc");
    await Assert.That(capturedScope.Scope?.UserId).IsEqualTo("user-xyz");
  }

  [Test]
  public async Task RuntimeLifecycleInvoker_WhenNoScope_DoesNotOverwriteExistingAmbientScopeAsync() {
    // Arrange - set an existing ambient scope
    var existingScope = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "existing-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    ScopeContextAccessor.CurrentContext = existingScope;

    IScopeContext? capturedScope = null;
    var registry = new TestLifecycleReceptorRegistry();
    registry.RegisterHandler<AmbientScopeTestEvent>(LifecycleStage.PreOutboxInline, (message, context, ct) => {
      capturedScope = ScopeContextAccessor.CurrentContext;
      return ValueTask.CompletedTask;
    });

    var invoker = new RuntimeLifecycleInvoker(registry);
    var envelope = _createEnvelopeWithoutScope();

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

    // Assert - existing scope should still be there (not overwritten with null)
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope?.TenantId).IsEqualTo("existing-tenant");

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
  }

  [Test]
  public async Task RuntimeLifecycleInvoker_ScopeIsImmutableAsync() {
    // Arrange
    IScopeContext? capturedScope = null;

    var registry = new TestLifecycleReceptorRegistry();
    registry.RegisterHandler<AmbientScopeTestEvent>(LifecycleStage.PreOutboxInline, (message, context, ct) => {
      capturedScope = ScopeContextAccessor.CurrentContext;
      return ValueTask.CompletedTask;
    });

    var invoker = new RuntimeLifecycleInvoker(registry);
    var envelope = _createEnvelopeWithScope("immutable-tenant", "immutable-user");

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

    // Assert - scope should be ImmutableScopeContext (not mutable)
    await Assert.That(capturedScope).IsTypeOf<ImmutableScopeContext>();
  }

  [Test]
  public async Task RuntimeLifecycleInvoker_WithTraceAndScope_ExtractsBothAsync() {
    // Arrange
    IScopeContext? capturedScope = null;

    var registry = new TestLifecycleReceptorRegistry();
    registry.RegisterHandler<AmbientScopeTestEvent>(LifecycleStage.PreOutboxInline, (message, context, ct) => {
      capturedScope = ScopeContextAccessor.CurrentContext;
      return ValueTask.CompletedTask;
    });

    var invoker = new RuntimeLifecycleInvoker(registry);

    var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var scopeDelta = ScopeDelta.FromSecurityContext(
      new SecurityContext { TenantId = "both-tenant", UserId = "both-user" });

    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        TraceParent = traceParent,
        Scope = scopeDelta
      }
    };

    var envelope = new MessageEnvelope<AmbientScopeTestEvent> {
      MessageId = MessageId.New(),
      Payload = new AmbientScopeTestEvent(),
      Hops = hops
    };

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

    // Assert - scope should be established from envelope
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope?.TenantId).IsEqualTo("both-tenant");
    await Assert.That(capturedScope.Scope?.UserId).IsEqualTo("both-user");
  }

  // --- Helpers ---

  private static MessageEnvelope<AmbientScopeTestEvent> _createEnvelopeWithScope(string tenantId, string userId) {
    var scopeDelta = ScopeDelta.FromSecurityContext(
      new SecurityContext { TenantId = tenantId, UserId = userId });

    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = scopeDelta
      }
    };

    return new MessageEnvelope<AmbientScopeTestEvent> {
      MessageId = MessageId.New(),
      Payload = new AmbientScopeTestEvent(),
      Hops = hops
    };
  }

  private static MessageEnvelope<AmbientScopeTestEvent> _createEnvelopeWithoutScope() {
    var hops = new List<MessageHop> {
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = null
      }
    };

    return new MessageEnvelope<AmbientScopeTestEvent> {
      MessageId = MessageId.New(),
      Payload = new AmbientScopeTestEvent(),
      Hops = hops
    };
  }

  private sealed record AmbientScopeTestEvent : IMessage;

  /// <summary>
  /// Simple test implementation of ILifecycleReceptorRegistry.
  /// </summary>
  private sealed class TestLifecycleReceptorRegistry : ILifecycleReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<Func<object, ILifecycleContext?, CancellationToken, ValueTask>>> _handlers = new();

    public void RegisterHandler<T>(LifecycleStage stage, Func<object, ILifecycleContext?, CancellationToken, ValueTask> handler) {
      var key = (typeof(T), stage);
      if (!_handlers.TryGetValue(key, out var list)) {
        list = [];
        _handlers[key] = list;
      }
      list.Add(handler);
    }

    public IReadOnlyList<Func<object, ILifecycleContext?, CancellationToken, ValueTask>> GetHandlers(Type messageType, LifecycleStage stage) {
      return _handlers.TryGetValue((messageType, stage), out var list)
        ? list
        : [];
    }

    // Unused interface members for this test
    public void Register<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public IReadOnlyList<object> GetReceptors(Type messageType, LifecycleStage stage) => [];
  }
}
