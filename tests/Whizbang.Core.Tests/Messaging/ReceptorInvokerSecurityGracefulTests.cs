using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests verifying that ReceptorInvoker handles security context correctly for messages
/// that lack security extractors. After lifecycle invoker convergence, all stages go through
/// ReceptorInvoker which calls EstablishContextAsync — so messages must either:
/// 1. Be registered as ExemptMessageTypes (e.g., LoginAttemptEvent)
/// 2. Have scope on the envelope (transport-deserialized messages)
/// 3. Have AllowAnonymous=true
/// </summary>
public class ReceptorInvokerSecurityGracefulTests {

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_ExemptMessage_WithoutSecurityContext_DoesNotThrow_AtStageAsync(LifecycleStage stage) {
    // Arrange: A strongly-typed message registered as exempt — the proper mechanism
    // for messages like LoginAttemptEvent that inherently lack security context.
    var receptorInvoked = false;
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestLoginEvent),
      ReceptorId: $"test_exempt_receptor_{stage}",
      InvokeAsync: (sp, msg, ct) => {
        receptorInvoked = true;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => {
      options.ExemptMessageTypes.Add(typeof(TestLoginEvent));
    });
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithoutScope();

    // Act & Assert: Should NOT throw — message type is exempt
    await invoker.InvokeAsync(envelope, stage);
    await Assert.That(receptorInvoked).IsTrue();
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_TypedMessage_WithEnvelopeScope_DoesNotThrow_AtStageAsync(LifecycleStage stage) {
    // Arrange: A typed message with no extractor BUT envelope carries scope from upstream.
    // This is the common case after transport deserialization — the provider returns null
    // instead of throwing because envelope.GetCurrentScope() has data.
    var receptorInvoked = false;
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestLoginEvent),
      ReceptorId: $"test_envelope_scope_receptor_{stage}",
      InvokeAsync: (sp, msg, ct) => {
        receptorInvoked = true;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope("user-123", "tenant-456");

    // Act & Assert: Should NOT throw — envelope has scope data
    await invoker.InvokeAsync(envelope, stage);
    await Assert.That(receptorInvoked).IsTrue();
  }

  [Test]
  [MethodDataSource(nameof(AllLifecycleStages))]
  public async Task InvokeAsync_TypedMessage_WithSecurityContext_EstablishesScope_AtStageAsync(LifecycleStage stage) {
    // Arrange: A strongly-typed message WITH security context on hops.
    // This should work and properly establish the scope context.
    IScopeContext? capturedScope = null;
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestLoginEvent),
      ReceptorId: $"test_with_security_receptor_{stage}",
      InvokeAsync: (sp, msg, ct) => {
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
    var envelope = _createTypedEnvelopeWithScope("user-123", "tenant-456");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Security context should be established from envelope fallback
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope.TenantId).IsEqualTo("tenant-456");
    await Assert.That(capturedScope!.Scope.UserId).IsEqualTo("user-123");
  }

  [Test]
  public async Task InvokeAsync_ExemptMessage_NoReceptors_DoesNotThrowAsync() {
    // Arrange: Even with no receptors, should not throw for exempt message types
    var registry = new TestReceptorRegistry();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => {
      options.ExemptMessageTypes.Add(typeof(TestLoginEvent));
    });
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithoutScope();

    // Act & Assert: Should NOT throw
    await invoker.InvokeAsync(envelope, LifecycleStage.PreDistributeAsync);
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
  }

  #endregion

  #region Helper Methods

  private static MessageEnvelope<TestLoginEvent> _createTypedEnvelopeWithoutScope() {
    return new MessageEnvelope<TestLoginEvent> {
      MessageId = MessageId.New(),
      Payload = new TestLoginEvent("test-user"),
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
      ]
    };
  }

  private static MessageEnvelope<TestLoginEvent> _createTypedEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<TestLoginEvent> {
      MessageId = MessageId.New(),
      Payload = new TestLoginEvent("test-user"),
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
      ]
    };
  }

  #endregion

  #region Test Types

  /// <summary>
  /// A strongly-typed message that simulates a login event — inherently has no
  /// security context because the user hasn't authenticated yet.
  /// </summary>
  private sealed record TestLoginEvent(string Username) : IEvent;

  #endregion

  #region Test Doubles

  private sealed class TestReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = new();

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

  #endregion
}
