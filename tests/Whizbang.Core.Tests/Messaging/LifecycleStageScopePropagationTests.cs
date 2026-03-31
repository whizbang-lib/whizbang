using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Integration tests verifying that ReceptorInvoker correctly establishes IScopeContextAccessor.Current
/// from ScopeDelta on MessageEnvelope hops at every lifecycle stage.
/// These use the real ReceptorInvoker, real ScopeContextAccessor, and real EnvelopeContextExtractor
/// to prove the actual working code path produces correct ScopeContext.
/// </summary>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>core-concepts/scope-propagation</docs>
[NotInParallel("ScopeContext")]
public class LifecycleStageScopePropagationTests {

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDeltaOnHop_EstablishesScopeContext_AtStageAsync(LifecycleStage stage) {
    // Arrange
    const string expectedTenantId = "test-tenant-456";
    const string expectedUserId = "user@example.com";

    IScopeContext? capturedScope = null;
    var registry = new StageTestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(JsonElement),
      ReceptorId: $"test_scope_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        var accessor = sp.GetService<IScopeContextAccessor>();
        capturedScope = accessor?.Current;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithScope(expectedUserId, expectedTenantId);

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert - ReceptorInvoker should have established scope from ScopeDelta on the hop
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturedScope!.Scope.UserId).IsEqualTo(expectedUserId);
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDeltaOnHop_ScopeContextShouldPropagate_AtStageAsync(LifecycleStage stage) {
    // Arrange - verify the scope context has ShouldPropagate=true for cascade security propagation
    IScopeContext? capturedScope = null;
    var registry = new StageTestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(JsonElement),
      ReceptorId: $"test_propagation_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        var accessor = sp.GetService<IScopeContextAccessor>();
        capturedScope = accessor?.Current;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithScope("user-123", "tenant-789");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert - scope should be ImmutableScopeContext with propagation enabled
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope).IsTypeOf<ImmutableScopeContext>();
    var immutable = (ImmutableScopeContext)capturedScope!;
    await Assert.That(immutable.ShouldPropagate).IsTrue();
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDeltaOnHop_SetsMessageContext_AtStageAsync(LifecycleStage stage) {
    // Arrange - verify IMessageContextAccessor also gets TenantId/UserId from the hop scope
    const string expectedTenantId = "msg-context-tenant";
    const string expectedUserId = "msg-context-user";

    // Use capturing accessor to snapshot the value at assignment time (AsyncLocal doesn't flow back from child async methods)
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var registry = new StageTestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(JsonElement),
      ReceptorId: $"test_msg_context_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithScope(expectedUserId, expectedTenantId);

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert - MessageContext should contain UserId and TenantId from envelope scope
    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext!.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturingMessageAccessor.CapturedContext!.UserId).IsEqualTo(expectedUserId);
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithoutScopeDelta_DoesNotSetScopeContext_AtStageAsync(LifecycleStage stage) {
    // Arrange - verify that without ScopeDelta, scope is not established
    IScopeContext? capturedScope = null;
    var registry = new StageTestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(JsonElement),
      ReceptorId: $"test_no_scope_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        var accessor = sp.GetService<IScopeContextAccessor>();
        capturedScope = accessor?.Current;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithoutScope();

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert - no scope should be established
    await Assert.That(capturedScope).IsNull();
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDeltaRoles_PropagatesRoles_AtStageAsync(LifecycleStage stage) {
    // Arrange - verify that roles from ScopeDelta.Collections are extracted
    // by MessageHopSecurityExtractor and available via IScopeContextAccessor.Current
    IScopeContext? capturedScope = null;
    var registry = new StageTestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(JsonElement),
      ReceptorId: $"test_roles_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        var accessor = sp.GetService<IScopeContextAccessor>();
        capturedScope = accessor?.Current;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithScopeAndRoles("user-456", "tenant-789", ["Admin", "User"]);

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert - scope should include roles from ScopeDelta
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope.TenantId).IsEqualTo("tenant-789");
    await Assert.That(capturedScope!.Scope.UserId).IsEqualTo("user-456");
    await Assert.That(capturedScope!.Roles).Contains("Admin");
    await Assert.That(capturedScope!.Roles).Contains("User");
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_WithScopeDelta_SetsInitiatingContext_AtStageAsync(LifecycleStage stage) {
    // Arrange - verify IScopeContextAccessor.InitiatingContext is set at every stage
    // Use capturing accessor to snapshot the value at assignment time (AsyncLocal doesn't flow back from child async methods)
    var capturingScopeAccessor = new CapturingScopeContextAccessor();

    var registry = new StageTestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(JsonElement),
      ReceptorId: $"test_initiating_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithScope("initiating-user", "initiating-tenant");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert - InitiatingContext should be set with message context from envelope
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId).IsEqualTo("initiating-user");
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.TenantId).IsEqualTo("initiating-tenant");
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId).IsEqualTo(envelope.MessageId);
  }

  #region Data Sources

  public static IEnumerable<LifecycleStage> AllLifecycleStages() {
    yield return LifecycleStage.ImmediateAsync;
    yield return LifecycleStage.LocalImmediateAsync;
    yield return LifecycleStage.LocalImmediateInline;
    yield return LifecycleStage.PreDistributeAsync;
    yield return LifecycleStage.PreDistributeInline;
    yield return LifecycleStage.DistributeAsync;
    yield return LifecycleStage.PostDistributeAsync;
    yield return LifecycleStage.PostDistributeInline;
    yield return LifecycleStage.PreOutboxAsync;
    yield return LifecycleStage.PreOutboxInline;
    yield return LifecycleStage.PostOutboxAsync;
    yield return LifecycleStage.PostOutboxInline;
    yield return LifecycleStage.PreInboxAsync;
    yield return LifecycleStage.PreInboxInline;
    yield return LifecycleStage.PostInboxAsync;
    yield return LifecycleStage.PostInboxInline;
    yield return LifecycleStage.PrePerspectiveAsync;
    yield return LifecycleStage.PrePerspectiveInline;
    yield return LifecycleStage.PostPerspectiveAsync;
    yield return LifecycleStage.PostPerspectiveInline;
    yield return LifecycleStage.PostLifecycleAsync;
    yield return LifecycleStage.PostLifecycleInline;
  }

  #endregion

  #region Helper Methods

  private static MessageEnvelope<JsonElement> _createEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<JsonElement> _createEnvelopeWithScopeAndRoles(
      string userId, string tenantId, string[] roles) {
    var scopeElement = JsonSerializer.SerializeToElement(new { t = tenantId, u = userId });
    var rolesElement = JsonSerializer.SerializeToElement(roles);

    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = new ScopeDelta {
            Values = new Dictionary<ScopeProp, JsonElement> {
              [ScopeProp.Scope] = scopeElement
            },
            Collections = new Dictionary<ScopeProp, CollectionChanges> {
              [ScopeProp.Roles] = new CollectionChanges { Set = rolesElement }
            }
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<JsonElement> _createEnvelopeWithoutScope() {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #endregion

  #region Test Doubles

  /// <summary>
  /// Test receptor registry that supports adding receptors at specific lifecycle stages.
  /// </summary>
  private sealed class StageTestReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(LifecycleStage stage, ReceptorInfo receptor) {
      var key = (receptor.MessageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : Array.Empty<ReceptorInfo>();
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>
  /// Captures IScopeContextAccessor values at assignment time into instance fields.
  /// Required because AsyncLocal values set inside child async methods (like _setMessageContextAsync)
  /// don't flow back to the parent context after the await completes.
  /// </summary>
  private sealed class CapturingScopeContextAccessor : IScopeContextAccessor {
    public IScopeContext? CapturedContext { get; private set; }
    public IMessageContext? CapturedInitiatingContext { get; private set; }

    public IScopeContext? Current {
      get => ScopeContextAccessor.CurrentContext;
      set {
        CapturedContext = value;
        ScopeContextAccessor.CurrentContext = value;
      }
    }

    public IMessageContext? InitiatingContext {
      get => ScopeContextAccessor.CurrentInitiatingContext;
      set {
        CapturedInitiatingContext = value;
        ScopeContextAccessor.CurrentInitiatingContext = value;
      }
    }
  }

  /// <summary>
  /// Captures IMessageContextAccessor values at assignment time into instance fields.
  /// </summary>
  private sealed class CapturingMessageContextAccessor : IMessageContextAccessor {
    public IMessageContext? CapturedContext { get; private set; }

    public IMessageContext? Current {
      get => MessageContextAccessor.CurrentContext;
      set {
        CapturedContext = value;
        MessageContextAccessor.CurrentContext = value;
      }
    }
  }

  #endregion
}
