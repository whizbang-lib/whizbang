using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for security context establishment in WorkCoordinatorPublisherWorker.
/// Verifies that security context is properly extracted from message envelopes
/// and made available to lifecycle receptors.
/// </summary>
/// <docs>workers/work-coordinator-publisher-worker#security-context</docs>
/// <tests>Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs</tests>
public class WorkCoordinatorPublisherWorkerSecurityContextTests {

  /// <summary>
  /// Verifies that security context is established from the envelope
  /// before lifecycle receptors are invoked.
  /// </summary>
  /// <docs>workers/work-coordinator-publisher-worker#security-context-establishment</docs>
  [Test]
  public async Task PublisherLoop_EstablishesSecurityContext_BeforeInvokingReceptorsAsync() {
    // Arrange
    const string testUserId = "test-user@example.com";
    const string testTenantId = "test-tenant-123";

    // Use capturing accessor to verify value is set (AsyncLocal behavior requires this)
    var capturingAccessor = new CapturingScopeContextAccessor();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor>(capturingAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Create envelope with security context in hops
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = testUserId,
            TenantId = testTenantId
          })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act - establish security context using helper
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - verify accessor's setter was called (due to AsyncLocal, can't read back after await)
    await Assert.That(capturingAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingAccessor.CapturedContext!.Scope.UserId).IsEqualTo(testUserId);
    await Assert.That(capturingAccessor.CapturedContext!.Scope.TenantId).IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies that message context is set with UserId and TenantId from
  /// the envelope's security context.
  /// </summary>
  /// <docs>workers/work-coordinator-publisher-worker#message-context</docs>
  [Test]
  public async Task PublisherLoop_SetsMessageContext_WithUserIdAndTenantIdAsync() {
    // Arrange
    const string testUserId = "test-user@example.com";
    const string testTenantId = "test-tenant-123";
    var testMessageId = MessageId.New();

    // Use capturing accessor to verify message context is set
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IMessageContextAccessor>(capturingMessageAccessor);

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Create envelope with security context
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = testMessageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = testUserId,
            TenantId = testTenantId
          })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - verify message context was set (captured via accessor)
    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext!.MessageId).IsEqualTo(testMessageId);
    await Assert.That(capturingMessageAccessor.CapturedContext!.UserId).IsEqualTo(testUserId);
    await Assert.That(capturingMessageAccessor.CapturedContext!.TenantId).IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies graceful handling when envelope has no security context.
  /// IScopeContextAccessor.Current should be null, but no exception should be thrown.
  /// </summary>
  /// <docs>workers/work-coordinator-publisher-worker#missing-security-context</docs>
  [Test]
  public async Task PublisherLoop_WithNoSecurityInEnvelope_DoesNotThrowAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => {
      options.AllowAnonymous = true;  // Allow messages without security context
    });

    var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();

    // Create envelope WITHOUT security context
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = null  // No security context
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act - should not throw
    await SecurityContextHelper.EstablishFullContextAsync(
      envelope,
      scope.ServiceProvider,
      CancellationToken.None);

    // Assert - verify no security context was established (null is acceptable)
    var scopeAccessor = scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
    // Either null or an empty context is acceptable
    if (scopeAccessor.Current is not null) {
      await Assert.That(scopeAccessor.Current.Scope.UserId).IsNull();
      await Assert.That(scopeAccessor.Current.Scope.TenantId).IsNull();
    }
  }

  /// <summary>
  /// Verifies that EstablishFullContextAsync succeeds even when there's no security context.
  /// Message context should still be set with MessageId but null UserId/TenantId.
  /// </summary>
  /// <docs>workers/work-coordinator-publisher-worker#message-context-without-security</docs>
  [Test]
  public async Task PublisherLoop_WithNoSecurity_StillSetsMessageContextAsync() {
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
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = testMessageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = null  // No security context
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

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
}
