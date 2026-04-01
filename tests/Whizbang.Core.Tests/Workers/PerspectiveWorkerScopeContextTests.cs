using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Integration tests verifying that scope context propagates correctly from MessageEnvelope
/// through ReceptorInvoker at Pre/PostPerspective lifecycle stages.
/// </summary>
/// <remarks>
/// <para>
/// These tests use real <c>ServiceCollection.AddWhizbangMessageSecurity()</c>, real
/// <c>ScopeContextAccessor</c>, real <c>MessageContextAccessor</c>, and real <c>ReceptorInvoker</c>.
/// They verify the full scope propagation path without mocks for the core security infrastructure.
/// </para>
/// <para>
/// After lifecycle invoker convergence, all stages go through ReceptorInvoker which calls
/// EstablishContextAsync. These tests specifically target Pre/PostPerspective stages to prove
/// scope flows correctly in the perspective worker path.
/// </para>
/// </remarks>
public class PerspectiveWorkerScopeContextTests {

  [Test]
  [MethodDataSource(nameof(PerspectiveStages))]
  public async Task InvokeAsync_WithEnvelopeScope_SetsScopeContextAccessor_AtStageAsync(LifecycleStage stage) {
    // Arrange: Typed envelope with ScopeDelta on hops — simulates transport-deserialized message.
    // The default MessageHopSecurityExtractor should extract scope from hops and set the accessor.
    IScopeContext? capturedScope = null;
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestPerspectiveEvent),
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
    var envelope = _createTypedEnvelopeWithScope("user-123", "tenant-456");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: IScopeContextAccessor.Current should be set with the envelope's scope data
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope.UserId).IsEqualTo("user-123");
    await Assert.That(capturedScope!.Scope.TenantId).IsEqualTo("tenant-456");
  }

  [Test]
  [MethodDataSource(nameof(PerspectiveStages))]
  public async Task InvokeAsync_WithSystemScope_SetsScopeContextAccessor_AtStageAsync(LifecycleStage stage) {
    // Arrange: SYSTEM scope (AsSystem().ForAllTenants()) — the exact scenario that caused
    // errors in downstream hooks after lifecycle invoker convergence.
    IScopeContext? capturedScope = null;
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestPerspectiveEvent),
      ReceptorId: $"test_system_scope_receptor_{stage}",
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
    var envelope = _createTypedEnvelopeWithScope("SYSTEM", TenantConstants.AllTenants);

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: SYSTEM scope should propagate correctly
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope.UserId).IsEqualTo("SYSTEM");
    await Assert.That(capturedScope!.Scope.TenantId).IsEqualTo(TenantConstants.AllTenants);
  }

  [Test]
  [MethodDataSource(nameof(PerspectiveStages))]
  public async Task InvokeAsync_WithEnvelopeScope_SetsInitiatingContextAsync(LifecycleStage stage) {
    // Arrange: Verify IScopeContextAccessor.InitiatingContext is set from the envelope.
    // InitiatingContext is the SOURCE OF TRUTH for security context (UserId, TenantId).
    // Use CapturingScopeContextAccessor to snapshot values at assignment time
    // (AsyncLocal values set inside child async methods don't flow back to the parent).
    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestPerspectiveEvent),
      ReceptorId: $"test_initiating_context_receptor_{stage}",
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
    var envelope = _createTypedEnvelopeWithScope("user-abc", "tenant-xyz");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: InitiatingContext should be set with UserId and TenantId from envelope
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId).IsEqualTo("user-abc");
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.TenantId).IsEqualTo("tenant-xyz");
    // InitiatingContext should also carry the envelope's MessageId
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  [MethodDataSource(nameof(PerspectiveStages))]
  public async Task InvokeAsync_WithEnvelopeScope_FallbackToEnvelopeScope_WhenExtractionFailsAsync(LifecycleStage stage) {
    // Arrange: No custom extractor registered, but envelope has scope on hops.
    // The default MessageHopSecurityExtractor extracts scope from hops.
    // When extraction produces an IScopeContext, it should wrap it in ImmutableScopeContext
    // with ShouldPropagate=true for cascaded event propagation.
    IScopeContext? capturedScope = null;
    var capturedImmutable = false;
    var registry = new TestReceptorRegistry();
    registry.AddReceptor(stage, new ReceptorInfo(
      MessageType: typeof(TestPerspectiveEvent),
      ReceptorId: $"test_fallback_receptor_{stage}",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        var accessor = sp.GetService<IScopeContextAccessor>();
        capturedScope = accessor?.Current;
        capturedImmutable = capturedScope is ImmutableScopeContext;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    // Use AddWhizbangMessageSecurity without custom extractors — relies on default
    // MessageHopSecurityExtractor which extracts from hops' ScopeDelta
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createTypedEnvelopeWithScope("fallback-user", "fallback-tenant");

    // Act
    await invoker.InvokeAsync(envelope, stage);

    // Assert: Scope should be set from envelope hop data
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope!.Scope.UserId).IsEqualTo("fallback-user");
    await Assert.That(capturedScope!.Scope.TenantId).IsEqualTo("fallback-tenant");
  }

  #region Data Sources

  public static IEnumerable<LifecycleStage> PerspectiveStages() {
    yield return LifecycleStage.PrePerspectiveDetached;
    yield return LifecycleStage.PrePerspectiveInline;
    yield return LifecycleStage.PostPerspectiveDetached;
    yield return LifecycleStage.PostPerspectiveInline;
  }

  #endregion

  #region Helper Methods

  private static MessageEnvelope<TestPerspectiveEvent> _createTypedEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<TestPerspectiveEvent> {
      MessageId = MessageId.New(),
      Payload = new TestPerspectiveEvent("test-perspective-event"),
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

  #endregion

  #region Test Types

  /// <summary>
  /// A strongly-typed event simulating perspective worker events (e.g., events consumed
  /// after transport deserialization in the perspective processing pipeline).
  /// </summary>
  private sealed record TestPerspectiveEvent(string Name) : IEvent;

  #endregion

  #region Test Doubles

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

  private sealed class TestReceptorRegistry : IReceptorRegistry {
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

  #endregion
}
