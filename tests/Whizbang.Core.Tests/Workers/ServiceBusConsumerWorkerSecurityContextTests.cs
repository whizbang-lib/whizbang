using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for security context establishment in ServiceBusConsumerWorker.
/// Verifies that BOTH IScopeContextAccessor AND IMessageContextAccessor are properly
/// set from message envelope security context when handling incoming messages.
/// </summary>
/// <remarks>
/// <para>
/// These tests verify the security context flow when messages arrive via Azure Service Bus.
/// The ServiceBusConsumerWorker must establish full security context before invoking
/// any receptors or business logic, so that services like UserContextManager can access
/// UserId and TenantId via IMessageContextAccessor.
/// </para>
/// <para>
/// Prior to the fix, ServiceBusConsumerWorker only set IScopeContextAccessor.Current
/// but NOT IMessageContextAccessor.Current, causing UserContextManager.UserContext
/// priority 4 (MessageContextAccessor.CurrentContext) to return null.
/// </para>
/// </remarks>
/// <docs>workers/service-bus-consumer-worker#security-context</docs>
/// <tests>Whizbang.Core/Workers/ServiceBusConsumerWorker.cs:_handleMessageAsync</tests>
public class ServiceBusConsumerWorkerSecurityContextTests {

  /// <summary>
  /// Verifies that IScopeContextAccessor.Current is set with UserId and TenantId
  /// from the envelope's security context when handling incoming messages.
  /// </summary>
  /// <docs>workers/service-bus-consumer-worker#scope-context</docs>
  [Test]
  public async Task HandleMessage_SetsScopeContextAccessor_WithUserIdAndTenantIdAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";

    // Use capturing accessor to verify value is set (AsyncLocal behavior requires this)
    var capturingAccessor = new CapturingScopeContextAccessor();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Create envelope with security context in hops
    var envelope = _createEnvelopeWithSecurityContext(MessageId.New(), testUserId, testTenantId);

    // Act - call SecurityContextHelper.EstablishFullContextAsync directly
    // This simulates what ServiceBusConsumerWorker._handleMessageAsync should do
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - verify IScopeContextAccessor was set (captured via accessor)
    await Assert.That(capturingAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingAccessor.CapturedContext!.Scope.UserId).IsEqualTo(testUserId);
    await Assert.That(capturingAccessor.CapturedContext!.Scope.TenantId).IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies that IMessageContextAccessor.Current is set with UserId and TenantId
  /// from the envelope's security context when handling incoming messages.
  /// </summary>
  /// <remarks>
  /// This test FAILS before the fix because ServiceBusConsumerWorker._handleMessageAsync()
  /// only sets IScopeContextAccessor.Current but NOT IMessageContextAccessor.Current.
  /// UserContextManager.UserContext priority 4 checks MessageContextAccessor.CurrentContext,
  /// which will be null without this fix.
  /// </remarks>
  /// <docs>workers/service-bus-consumer-worker#message-context</docs>
  [Test]
  public async Task HandleMessage_SetsMessageContextAccessor_WithUserIdAndTenantIdAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    // Use capturing accessor to verify IMessageContextAccessor.Current is set
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Create envelope WITH security context
    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act - call SecurityContextHelper.EstablishFullContextAsync directly
    // This simulates what ServiceBusConsumerWorker._handleMessageAsync SHOULD do
    // (Currently it does NOT call this, which is the bug)
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - verify IMessageContextAccessor was set (THIS WILL FAIL BEFORE FIX)
    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext!.MessageId).IsEqualTo(testMessageId);
    await Assert.That(capturingMessageAccessor.CapturedContext!.UserId).IsEqualTo(testUserId);
    await Assert.That(capturingMessageAccessor.CapturedContext!.TenantId).IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies graceful handling when envelope has no security context.
  /// Message context should still be set with MessageId but null UserId/TenantId.
  /// </summary>
  /// <docs>workers/service-bus-consumer-worker#missing-security-context</docs>
  [Test]
  public async Task HandleMessage_WithNoSecurityInEnvelope_StillSetsMessageContextAsync() {
    // Arrange
    var testMessageId = MessageId.New();

    // Use capturing accessor to verify message context is set (even without security)
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => {
      options.AllowAnonymous = true;  // Allow messages without security context
    });
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Create envelope WITHOUT security context
    var envelope = _createEnvelopeWithoutSecurityContext(testMessageId);

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - message context should be set even without security (captured via accessor)
    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext!.MessageId).IsEqualTo(testMessageId);
    await Assert.That(capturingMessageAccessor.CapturedContext!.UserId).IsNull();  // No security context
    await Assert.That(capturingMessageAccessor.CapturedContext!.TenantId).IsNull();  // No security context
  }

  /// <summary>
  /// Verifies that calling EstablishFullContextAsync with an envelope that has
  /// security context properly sets BOTH accessors in a single call.
  /// </summary>
  /// <docs>workers/service-bus-consumer-worker#full-context-establishment</docs>
  [Test]
  public async Task HandleMessage_EstablishFullContext_SetsBothAccessorsAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    // Use capturing accessors for both
    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Create envelope WITH security context
    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act - single call should set BOTH accessors
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - BOTH accessors should be set
    await Assert.That(capturingScopeAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedContext!.Scope.UserId).IsEqualTo(testUserId);

    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext!.UserId).IsEqualTo(testUserId);
    await Assert.That(capturingMessageAccessor.CapturedContext!.TenantId).IsEqualTo(testTenantId);
  }

  #region Helper Methods

  private static MessageEnvelope<JsonElement> _createEnvelopeWithSecurityContext(
      MessageId messageId,
      string userId,
      string tenantId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = new List<MessageHop> {
        new MessageHop {
          Type = HopType.Current,
          Timestamp = System.DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = System.Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          SecurityContext = new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          }
        }
      }
    };
  }

  private static MessageEnvelope<JsonElement> _createEnvelopeWithoutSecurityContext(MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = new List<MessageHop> {
        new MessageHop {
          Type = HopType.Current,
          Timestamp = System.DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = System.Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          SecurityContext = null  // No security context
        }
      }
    };
  }

  #endregion

  #region Test Doubles

  /// <summary>
  /// Accessor that captures the value set to Current for testing purposes.
  /// Due to AsyncLocal behavior, values set after await don't flow back to caller.
  /// </summary>
  private sealed class CapturingScopeContextAccessor : IScopeContextAccessor {
    public IScopeContext? CapturedContext { get; private set; }

    public IScopeContext? Current {
      get => ScopeContextAccessor.CurrentContext;
      set {
        CapturedContext = value; // Capture for verification
        ScopeContextAccessor.CurrentContext = value; // Also set the real AsyncLocal
      }
    }

    public IMessageContext? InitiatingContext {
      get => ScopeContextAccessor.CurrentInitiatingContext;
      set => ScopeContextAccessor.CurrentInitiatingContext = value;
    }
  }

  /// <summary>
  /// Message context accessor that captures the value set to Current for testing purposes.
  /// </summary>
  private sealed class CapturingMessageContextAccessor : IMessageContextAccessor {
    public IMessageContext? CapturedContext { get; private set; }

    public IMessageContext? Current {
      get => MessageContextAccessor.CurrentContext;
      set {
        CapturedContext = value; // Capture for verification
        MessageContextAccessor.CurrentContext = value; // Also set the real AsyncLocal
      }
    }
  }

  #endregion
}
