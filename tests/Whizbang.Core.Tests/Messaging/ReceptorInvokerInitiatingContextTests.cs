using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
/// Tests for InitiatingContext establishment in ReceptorInvoker.
/// Verifies that ReceptorInvoker sets IScopeContextAccessor.InitiatingContext
/// with a reference to the IMessageContext derived from the envelope.
/// </summary>
/// <remarks>
/// <para>
/// These tests ensure the architectural principle: IMessageContext is the source of truth.
/// When ReceptorInvoker processes an envelope, it should set InitiatingContext so that
/// all downstream code can access the originating message context.
/// </para>
/// </remarks>
/// <docs>core-concepts/cascade-context#initiating-context</docs>
/// <tests>Whizbang.Core/Messaging/ReceptorInvoker.cs:InvokeAsync</tests>
public class ReceptorInvokerInitiatingContextTests {

  /// <summary>
  /// Verifies that ReceptorInvoker sets InitiatingContext on IScopeContextAccessor
  /// from the envelope before invoking receptors.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ShouldSetInitiatingContextFromEnvelopeAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var registry = new TestReceptorRegistry();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);
    services.AddSingleton<IReceptorRegistry>(registry);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

    // Assert - InitiatingContext should be set with the message context from envelope
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId)
      .IsEqualTo(testMessageId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId)
      .IsEqualTo(testUserId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.TenantId)
      .IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies that the InitiatingContext set by ReceptorInvoker is the same instance
  /// as the IMessageContext set on IMessageContextAccessor.
  /// </summary>
  [Test]
  public async Task InvokeAsync_InitiatingContext_ShouldBeSameAsMessageContextAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var registry = new TestReceptorRegistry();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);
    services.AddSingleton<IReceptorRegistry>(registry);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

    // Assert - Both should be the same instance (reference equality)
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(ReferenceEquals(
      capturingScopeAccessor.CapturedInitiatingContext,
      capturingMessageAccessor.CapturedContext)).IsTrue();
  }

  /// <summary>
  /// Verifies that when receptor returns cascaded events, child dispatch
  /// can access the parent's InitiatingContext via AsyncLocal.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WhenReceptorCascades_ChildShouldInheritInitiatingContextAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    IMessageContext? capturedInitiating = null;
    var registry = new TestReceptorRegistry();

    // Register a receptor that captures the InitiatingContext during execution
    registry.AddReceptor(
      new ReceptorInfo(
        MessageType: typeof(JsonElement),
        ReceptorId: "TestReceptor",
        InvokeAsync: async (provider, message, envelope, callerInfo, ct) => {
          // Capture InitiatingContext during receptor execution
          capturedInitiating = ScopeContextAccessor.CurrentInitiatingContext;
          await Task.CompletedTask;
          return null;
        }
      ),
      LifecycleStage.LocalImmediateInline);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

    // Assert - Receptor should have been able to access InitiatingContext
    await Assert.That(capturedInitiating).IsNotNull();
    await Assert.That(capturedInitiating!.MessageId).IsEqualTo(testMessageId);
    await Assert.That(capturedInitiating!.UserId).IsEqualTo(testUserId);
    await Assert.That(capturedInitiating!.TenantId).IsEqualTo(testTenantId);

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    MessageContextAccessor.CurrentContext = null;
  }

  /// <summary>
  /// Verifies that InitiatingContext contains CorrelationId for tracing.
  /// </summary>
  [Test]
  public async Task InvokeAsync_InitiatingContext_ShouldContainCorrelationIdAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();
    var testCorrelationId = CorrelationId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var registry = new TestReceptorRegistry();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);
    services.AddSingleton<IReceptorRegistry>(registry);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithFullContext(
      testMessageId, testUserId, testTenantId, testCorrelationId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

    // Assert - InitiatingContext should contain CorrelationId
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.CorrelationId)
      .IsEqualTo(testCorrelationId);
  }

  /// <summary>
  /// Verifies that without security context, InitiatingContext is still set
  /// with valid MessageId but null UserId/TenantId.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithoutSecurityContext_ShouldSetInitiatingContextWithNullSecurityAsync() {
    // Arrange
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var registry = new TestReceptorRegistry();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => {
      options.AllowAnonymous = true;
    });
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);
    services.AddSingleton<IReceptorRegistry>(registry);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithoutSecurityContext(testMessageId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

    // Assert - InitiatingContext should be set with null security values
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId)
      .IsEqualTo(testMessageId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId).IsNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.TenantId).IsNull();
  }

  /// <summary>
  /// INTEGRATION TEST: Verifies that ScopedMessageContext reads from InitiatingContext
  /// when injected into a receptor. This tests the full flow:
  /// Envelope → ReceptorInvoker sets InitiatingContext → ScopedMessageContext reads from it.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ScopedMessageContext_ShouldReadFromInitiatingContextAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    string? capturedUserId = null;
    string? capturedTenantId = null;

    var registry = new TestReceptorRegistry();

    // Register a receptor that uses ScopedMessageContext to get security values
    registry.AddReceptor(
      new ReceptorInfo(
        MessageType: typeof(JsonElement),
        ReceptorId: "TestScopedContextReceptor",
        InvokeAsync: async (provider, message, envelope, callerInfo, ct) => {
          // Get ScopedMessageContext from DI - this is what real receptors do
          var scopedMessageContext = provider.GetRequiredService<IMessageContext>();

          // Capture the security values
          capturedUserId = scopedMessageContext.UserId;
          capturedTenantId = scopedMessageContext.TenantId;

          await Task.CompletedTask;
          return null;
        }
      ),
      LifecycleStage.LocalImmediateInline);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);

    // Register ScopedMessageContext as IMessageContext - matches production DI
    services.AddScoped<IMessageContext, ScopedMessageContext>();

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

    // Assert - ScopedMessageContext should have read from InitiatingContext
    await Assert.That(capturedUserId).IsEqualTo(testUserId);
    await Assert.That(capturedTenantId).IsEqualTo(testTenantId);

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    MessageContextAccessor.CurrentContext = null;
  }

  /// <summary>
  /// INTEGRATION TEST: Verifies that ScopedMessageContext correctly prioritizes InitiatingContext
  /// over IScopeContext when both are set with different values.
  /// </summary>
  [Test]
  public async Task InvokeAsync_ScopedMessageContext_ShouldPrioritizeInitiatingContextOverScopeContextAsync() {
    // Arrange
    var initiatingUserId = "initiating-user@example.com";
    var initiatingTenantId = "initiating-tenant-123";
    var scopeUserId = "scope-user@example.com";
    var scopeTenantId = "scope-tenant-456";
    var testMessageId = MessageId.New();

    string? capturedUserId = null;
    string? capturedTenantId = null;

    var registry = new TestReceptorRegistry();

    // Register a receptor that checks the priority
    registry.AddReceptor(
      new ReceptorInfo(
        MessageType: typeof(JsonElement),
        ReceptorId: "TestPriorityReceptor",
        InvokeAsync: async (provider, message, envelope, callerInfo, ct) => {
          // First, set a conflicting IScopeContext (should NOT be used)
          var scopeAccessor = provider.GetRequiredService<IScopeContextAccessor>();
          var extraction = new SecurityExtraction {
            Scope = new PerspectiveScope { UserId = scopeUserId, TenantId = scopeTenantId },
            Roles = new HashSet<string>(),
            Permissions = new HashSet<Permission>(),
            SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
            Claims = new Dictionary<string, string>(),
            Source = "Test"
          };
          scopeAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

          // Get ScopedMessageContext - should still use InitiatingContext values
          var scopedMessageContext = provider.GetRequiredService<IMessageContext>();

          capturedUserId = scopedMessageContext.UserId;
          capturedTenantId = scopedMessageContext.TenantId;

          await Task.CompletedTask;
          return null;
        }
      ),
      LifecycleStage.LocalImmediateInline);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IMessageContext, ScopedMessageContext>();

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var invoker = new ReceptorInvoker(registry, scope.ServiceProvider);
    var envelope = _createEnvelopeWithSecurityContext(testMessageId, initiatingUserId, initiatingTenantId);

    // Act
    await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);

    // Assert - InitiatingContext should win over IScopeContext
    await Assert.That(capturedUserId).IsEqualTo(initiatingUserId);
    await Assert.That(capturedTenantId).IsEqualTo(initiatingTenantId);

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    MessageContextAccessor.CurrentContext = null;
  }

  #region Helper Methods

  private static MessageEnvelope<JsonElement> _createEnvelopeWithSecurityContext(
      MessageId messageId,
      string userId,
      string tenantId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
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
      ]
    };
  }

  private static MessageEnvelope<JsonElement> _createEnvelopeWithFullContext(
      MessageId messageId,
      string userId,
      string tenantId,
      CorrelationId correlationId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = correlationId,
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

  private static MessageEnvelope<JsonElement> _createEnvelopeWithoutSecurityContext(
      MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
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
      ]
    };
  }

  #endregion

  #region Test Doubles

  /// <summary>
  /// Accessor that captures both Current and InitiatingContext for testing.
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
  /// Message context accessor that captures the value set.
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

  /// <summary>
  /// Simple test receptor registry for testing.
  /// </summary>
  private sealed class TestReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(ReceptorInfo receptor, LifecycleStage stage) {
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
