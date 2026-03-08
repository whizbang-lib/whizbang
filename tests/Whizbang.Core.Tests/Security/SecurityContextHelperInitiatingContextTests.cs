using System;
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

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for InitiatingContext property establishment in SecurityContextHelper.
/// Verifies that IMessageContext is set as the InitiatingContext on IScopeContextAccessor
/// when context is established from message envelopes.
/// </summary>
/// <remarks>
/// <para>
/// These tests ensure the architectural principle: IMessageContext is the source of truth.
/// The IScopeContextAccessor.InitiatingContext should carry a REFERENCE to the IMessageContext
/// that initiated the scope, not a copy of the data.
/// </para>
/// </remarks>
/// <docs>core-concepts/cascade-context#initiating-context</docs>
/// <tests>Whizbang.Core/Security/SecurityContextHelper.cs:EstablishFullContextAsync</tests>
public class SecurityContextHelperInitiatingContextTests {

  /// <summary>
  /// Verifies that EstablishFullContextAsync sets InitiatingContext on the accessor
  /// with a reference to the IMessageContext created from the envelope.
  /// </summary>
  [Test]
  public async Task EstablishFullContextAsync_ShouldSetInitiatingContextAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - InitiatingContext should be set
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();

    // InitiatingContext should reference the same IMessageContext that was set
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId)
      .IsEqualTo(testMessageId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId)
      .IsEqualTo(testUserId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.TenantId)
      .IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies that SetMessageContextFromEnvelope sets InitiatingContext
  /// in addition to setting the message context accessor.
  /// </summary>
  [Test]
  public async Task SetMessageContextFromEnvelope_ShouldSetInitiatingContextAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    SecurityContextHelper.SetMessageContextFromEnvelope(envelope, scope.ServiceProvider);

    // Assert - InitiatingContext should be set with the same IMessageContext
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId)
      .IsEqualTo(testMessageId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId)
      .IsEqualTo(testUserId);
  }

  /// <summary>
  /// Verifies that InitiatingContext and the message context accessor
  /// reference the same IMessageContext instance (not copies).
  /// </summary>
  [Test]
  public async Task EstablishFullContextAsync_InitiatingContextAndMessageContext_ShouldBeTheSameInstanceAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - Both should reference the same instance
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();

    // Should be the exact same instance (reference equality)
    await Assert.That(ReferenceEquals(
      capturingScopeAccessor.CapturedInitiatingContext,
      capturingMessageAccessor.CapturedContext)).IsTrue();
  }

  /// <summary>
  /// Verifies that when no security context is in envelope, InitiatingContext
  /// is still set with null UserId/TenantId but valid MessageId.
  /// </summary>
  [Test]
  public async Task EstablishFullContextAsync_WithNoSecurityContext_ShouldStillSetInitiatingContextAsync() {
    // Arrange
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => {
      options.AllowAnonymous = true;
    });
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var envelope = _createEnvelopeWithoutSecurityContext(testMessageId);

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - InitiatingContext should be set even without security
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId)
      .IsEqualTo(testMessageId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId).IsNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.TenantId).IsNull();
  }

  /// <summary>
  /// Verifies that EstablishMessageContextForCascade sets InitiatingContext
  /// when called with existing parent context in AsyncLocal.
  /// </summary>
  [Test]
  public async Task EstablishMessageContextForCascade_ShouldSetInitiatingContextAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";

    // Pre-set parent context in AsyncLocal (simulates parent receptor having set context)
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = testUserId,
      TenantId = testTenantId
    };

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade(serviceProvider: null);

      // Assert - InitiatingContext should be set with the new cascade context
      var initiatingContext = ScopeContextAccessor.CurrentInitiatingContext;
      await Assert.That(initiatingContext).IsNotNull();
      await Assert.That(initiatingContext!.UserId).IsEqualTo(testUserId);
      await Assert.That(initiatingContext!.TenantId).IsEqualTo(testTenantId);
    } finally {
      // Cleanup
      MessageContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  /// <summary>
  /// Verifies that InitiatingContext is set during context establishment.
  /// This is critical for services that need to access the initiating message context.
  /// </summary>
  /// <remarks>
  /// Note: This test verifies the value is SET correctly. The AsyncLocal flow to child
  /// tasks is verified in the existing ScopeContextAccessorInitiatingContextTests which
  /// tests the AsyncLocal mechanism directly without the complexity of ConfigureAwait(false).
  /// </remarks>
  [Test]
  public async Task EstablishFullContextAsync_InitiatingContext_ShouldBeSetWithFullContextAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();

    var envelope = _createEnvelopeWithSecurityContext(testMessageId, testUserId, testTenantId);

    // Act
    using var scope = serviceProvider.CreateScope();
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - verify InitiatingContext was set correctly
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.MessageId).IsEqualTo(testMessageId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.UserId).IsEqualTo(testUserId);
    await Assert.That(capturingScopeAccessor.CapturedInitiatingContext!.TenantId).IsEqualTo(testTenantId);

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    MessageContextAccessor.CurrentContext = null;
  }

  /// <summary>
  /// Verifies that InitiatingContext can be used for debugging to see the full
  /// message context that initiated the current scope.
  /// </summary>
  [Test]
  public async Task InitiatingContext_ShouldExposeFullMessageContextForDebuggingAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();
    var testCorrelationId = CorrelationId.New();
    var testCausationId = MessageId.New();

    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingScopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    var envelope = _createEnvelopeWithFullContext(
      testMessageId, testUserId, testTenantId, testCorrelationId, testCausationId);

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    var initiatingContext = capturingScopeAccessor.CapturedInitiatingContext;

    // Assert - All message context fields should be accessible for debugging
    await Assert.That(initiatingContext).IsNotNull();
    await Assert.That(initiatingContext!.MessageId).IsEqualTo(testMessageId);
    await Assert.That(initiatingContext!.CorrelationId).IsEqualTo(testCorrelationId);
    await Assert.That(initiatingContext!.CausationId).IsEqualTo(testCausationId);
    await Assert.That(initiatingContext!.UserId).IsEqualTo(testUserId);
    await Assert.That(initiatingContext!.TenantId).IsEqualTo(testTenantId);
    await Assert.That(initiatingContext!.Timestamp).IsNotDefault();

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
      Hops = new List<MessageHop> {
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
          SecurityContext = new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          }
        }
      }
    };
  }

  private static MessageEnvelope<JsonElement> _createEnvelopeWithoutSecurityContext(
      MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = new List<MessageHop> {
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
          SecurityContext = null
        }
      }
    };
  }

  private static MessageEnvelope<JsonElement> _createEnvelopeWithFullContext(
      MessageId messageId,
      string userId,
      string tenantId,
      CorrelationId correlationId,
      MessageId causationId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = new List<MessageHop> {
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = correlationId,
          CausationId = causationId,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
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

  #endregion
}
