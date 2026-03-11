using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests that verify scope is passed to IMessageTagProcessor.ProcessTagsAsync
/// instead of null. This is a bug fix - scope should flow from the established
/// security context to the tag processor.
/// </summary>
/// <docs>core-concepts/scope-propagation</docs>
public class ReceptorInvokerTagProcessorScopeTests {
  private sealed record TestCommand(string Value) : IMessage;
  private sealed record TestEvent(string Data) : IEvent;

  /// <summary>
  /// Verifies that when security context is established, the scope is passed
  /// to ProcessTagsAsync instead of null.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WhenSecurityContextEstablished_PassesScopeToTagProcessorAsync() {
    // Arrange
    var expectedTenantId = "test-tenant-for-tags";
    var expectedUserId = "test-user-for-tags";

    // Security context returned by provider
    var testScopeContext = new TestScopeContext(expectedTenantId, expectedUserId);
    var securityContext = testScopeContext.ToImmutable(shouldPropagate: true);
    var securityProvider = new TestSecurityContextProvider(returns: securityContext);

    // Capture the scope passed to ProcessTagsAsync
    IScopeContext? capturedScope = null;
    var tagProcessor = new TestTagProcessor(onProcessTags: scope => {
      capturedScope = scope;
    });

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddSingleton<IMessageTagProcessor>(tagProcessor);
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestCommand>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider, null);
    var envelope = _createEnvelope(new TestCommand("test"));

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - Scope should be passed to ProcessTagsAsync
    await Assert.That(capturedScope).IsNotNull()
      .Because("ProcessTagsAsync should receive scope from established security context, not null");
    await Assert.That(capturedScope!.Scope?.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturedScope.Scope?.UserId).IsEqualTo(expectedUserId);
  }

  /// <summary>
  /// Verifies that when security provider returns null but envelope has scope in hops,
  /// the scope from hops is passed to ProcessTagsAsync.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WhenScopeInHops_PassesScopeToTagProcessorAsync() {
    // Arrange
    var expectedTenantId = "hop-tenant-for-tags";
    var expectedUserId = "hop-user-for-tags";

    // Security provider returns NULL
    var securityProvider = new TestSecurityContextProvider(returns: null);

    // Capture the scope passed to ProcessTagsAsync
    IScopeContext? capturedScope = null;
    var tagProcessor = new TestTagProcessor(onProcessTags: scope => {
      capturedScope = scope;
    });

    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddSingleton<IMessageTagProcessor>(tagProcessor);
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestCommand>("TestReceptor", LifecycleStage.PostInboxInline);

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider, null);
    var envelope = _createEnvelopeWithScopeInHops(new TestCommand("test"), expectedTenantId, expectedUserId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    // Assert - Scope from hops should be passed to ProcessTagsAsync
    await Assert.That(capturedScope).IsNotNull()
      .Because("ProcessTagsAsync should receive scope from envelope hops when security provider returns null");
    await Assert.That(capturedScope!.Scope?.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturedScope.Scope?.UserId).IsEqualTo(expectedUserId);
  }

  #region Test Helpers

  private static MessageEnvelope<T> _createEnvelope<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = [new MessageHop { Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
  }

  private static MessageEnvelope<T> _createEnvelopeWithScopeInHops<T>(T message, string tenantId, string userId) where T : notnull {
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
        Scope = scopeDelta
      }]
    };
  }

  private sealed class TestSecurityContextProvider : IMessageSecurityContextProvider {
    private readonly IScopeContext? _returns;

    public TestSecurityContextProvider(IScopeContext? returns = null) {
      _returns = returns;
    }

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(_returns);
    }
  }

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

  /// <summary>
  /// Test tag processor that captures the scope passed to ProcessTagsAsync.
  /// </summary>
  private sealed class TestTagProcessor : IMessageTagProcessor {
    private readonly System.Action<IScopeContext?>? _onProcessTags;

    public TestTagProcessor(System.Action<IScopeContext?>? onProcessTags = null) {
      _onProcessTags = onProcessTags;
    }

    public ValueTask ProcessTagsAsync(
        object message,
        Type messageType,
        LifecycleStage stage,
        IScopeContext? scope = null,
        CancellationToken ct = default) {
      _onProcessTags?.Invoke(scope);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class InvocationTracker {
    private readonly List<(string ReceptorId, LifecycleStage Stage)> _invocations = [];
    public List<(string ReceptorId, LifecycleStage Stage)> Invocations => _invocations;
    public void RecordInvocation(string receptorId, LifecycleStage stage) => _invocations.Add((receptorId, stage));
  }

  private sealed class TestReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(System.Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];
    private readonly InvocationTracker _tracker;

    public TestReceptorRegistry(InvocationTracker tracker) {
      _tracker = tracker;
    }

    public void RegisterReceptor<TMessage>(string receptorId, LifecycleStage stage) where TMessage : notnull {
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
          return ValueTask.FromResult<object?>(null);
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
