using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Integration tests for the message security context establishment flow.
/// Tests the complete pipeline: Provider -> Extractors -> Callbacks -> IScopeContextAccessor.
/// </summary>
/// <docs>core-concepts/message-security#integration</docs>
public class MessageSecurityIntegrationTests {
  // ========================================
  // End-to-End Flow Tests
  // ========================================

  [Test]
  public async Task EndToEnd_MessageWithSecurityContext_EstablishesContextAndInvokesCallbacksAsync() {
    // Arrange
    var callbackInvoked = false;
    IScopeContext? callbackContext = null;

    var callback = new TestSecurityContextCallback(ctx => {
      callbackInvoked = true;
      callbackContext = ctx;
    });

    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [callback],
      options: options
    );

    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext {
      TenantId = "integration-tenant",
      UserId = "integration-user"
    });

    var services = _createServiceProvider();

    // Act
    var result = await provider.EstablishContextAsync(envelope, services, CancellationToken.None);

    // Assert - context established
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("integration-tenant");
    await Assert.That(result.Scope.UserId).IsEqualTo("integration-user");

    // Assert - callback invoked
    await Assert.That(callbackInvoked).IsTrue();
    await Assert.That(callbackContext).IsNotNull();
    await Assert.That(callbackContext!.Scope.TenantId).IsEqualTo("integration-tenant");
  }

  [Test]
  public async Task EndToEnd_MessageWithoutSecurityContext_AllowAnonymousTrue_ReturnsNullAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions { AllowAnonymous = true };

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );

    var envelope = _createEnvelopeWithoutSecurityContext();
    var services = _createServiceProvider();

    // Act
    var result = await provider.EstablishContextAsync(envelope, services, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task EndToEnd_MessageWithoutSecurityContext_AllowAnonymousFalse_ThrowsAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions { AllowAnonymous = false };

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );

    var envelope = _createEnvelopeWithoutSecurityContext();
    var services = _createServiceProvider();

    // Act & Assert
    await Assert.That(async () =>
      await provider.EstablishContextAsync(envelope, services, CancellationToken.None)
    ).ThrowsExactly<SecurityContextRequiredException>();
  }

  // ========================================
  // Multiple Extractors Tests
  // ========================================

  [Test]
  public async Task MultipleExtractors_FirstSuccessfulExtraction_UsedAsync() {
    // Arrange
    var lowPriorityExtractor = new TestExtractor(priority: 50, extraction: new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "low-priority-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "LowPriority"
    });

    var highPriorityExtractor = new TestExtractor(priority: 100, extraction: new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "high-priority-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "HighPriority"
    });

    var options = new MessageSecurityOptions();

    // Note: Provider sorts by priority (lower = earlier)
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [highPriorityExtractor, lowPriorityExtractor],
      callbacks: [],
      options: options
    );

    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext { TenantId = "test" });
    var services = _createServiceProvider();

    // Act
    var result = await provider.EstablishContextAsync(envelope, services, CancellationToken.None);

    // Assert - low priority (50) runs before high priority (100)
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("low-priority-tenant");
  }

  [Test]
  public async Task MultipleExtractors_FirstReturnsNull_FallsToSecondAsync() {
    // Arrange
    var nullExtractor = new TestExtractor(priority: 10, extraction: null);
    var validExtractor = new TestExtractor(priority: 20, extraction: new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "fallback-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Fallback"
    });

    var options = new MessageSecurityOptions();

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [nullExtractor, validExtractor],
      callbacks: [],
      options: options
    );

    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext { TenantId = "test" });
    var services = _createServiceProvider();

    // Act
    var result = await provider.EstablishContextAsync(envelope, services, CancellationToken.None);

    // Assert - falls back to second extractor
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("fallback-tenant");
  }

  // ========================================
  // Multiple Callbacks Tests
  // ========================================

  [Test]
  public async Task MultipleCallbacks_AllInvokedInOrderAsync() {
    // Arrange
    var callbackOrder = new List<string>();

    var callback1 = new TestSecurityContextCallback(_ => callbackOrder.Add("callback1"));
    var callback2 = new TestSecurityContextCallback(_ => callbackOrder.Add("callback2"));
    var callback3 = new TestSecurityContextCallback(_ => callbackOrder.Add("callback3"));

    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [callback1, callback2, callback3],
      options: options
    );

    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext { TenantId = "test" });
    var services = _createServiceProvider();

    // Act
    await provider.EstablishContextAsync(envelope, services, CancellationToken.None);

    // Assert - all callbacks invoked in order
    await Assert.That(callbackOrder).IsEquivalentTo(["callback1", "callback2", "callback3"]);
  }

  // ========================================
  // Exempt Message Types Tests
  // ========================================

  [Test]
  public async Task ExemptMessageType_BypassesSecurityExtraction_ReturnsNullAsync() {
    // Arrange
    var callbackInvoked = false;
    var callback = new TestSecurityContextCallback(_ => callbackInvoked = true);

    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions {
      AllowAnonymous = false // Would normally throw
    };
    options.ExemptMessageTypes.Add(typeof(HealthCheckMessage));

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [callback],
      options: options
    );

    var exemptEnvelope = _createEnvelope(new HealthCheckMessage());
    var services = _createServiceProvider();

    // Act - exempt message type should return null without throwing
    var result = await provider.EstablishContextAsync(exemptEnvelope, services, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(callbackInvoked).IsFalse();
  }

  // ========================================
  // ImmutableScopeContext Tests
  // ========================================

  [Test]
  public async Task ImmutableScopeContext_ContainsCorrectMetadataAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions { PropagateToOutgoingMessages = true };

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );

    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext {
      TenantId = "meta-tenant",
      UserId = "meta-user"
    });
    var services = _createServiceProvider();

    // Act
    var result = await provider.EstablishContextAsync(envelope, services, CancellationToken.None);

    // Assert - verify ImmutableScopeContext properties
    await Assert.That(result).IsNotNull();

    var immutableContext = result as ImmutableScopeContext;
    await Assert.That(immutableContext).IsNotNull();
    await Assert.That(immutableContext!.Source).IsEqualTo("MessageHop");
    await Assert.That(immutableContext.ShouldPropagate).IsTrue();
    await Assert.That(immutableContext.EstablishedAt).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  // ========================================
  // Audit Event Tests
  // ========================================

  [Test]
  public async Task AuditLoggingEnabled_EmitsAuditEventAsync() {
    // Arrange
    Whizbang.Core.SystemEvents.Security.ScopeContextEstablished? capturedEvent = null;

    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions { EnableAuditLogging = true };

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options,
      onAuditEvent: evt => capturedEvent = evt
    );

    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext {
      TenantId = "audit-tenant",
      UserId = "audit-user"
    });
    var services = _createServiceProvider();

    // Act
    await provider.EstablishContextAsync(envelope, services, CancellationToken.None);

    // Assert
    await Assert.That(capturedEvent).IsNotNull();
    await Assert.That(capturedEvent!.Scope.TenantId).IsEqualTo("audit-tenant");
    await Assert.That(capturedEvent.Source).IsEqualTo("MessageHop");
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static ServiceInstanceInfo _createServiceInstance() => new() {
    ServiceName = "test-service",
    InstanceId = Guid.NewGuid(),
    HostName = "test-host",
    ProcessId = 1234
  };

  private static MessageEnvelope<T> _createEnvelope<T>(T payload) where T : notnull {
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = null
    };

    return new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [hop]
    };
  }

  private static MessageEnvelope<TestMessage> _createEnvelopeWithSecurityContext(SecurityContext securityContext) {
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = securityContext
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private static MessageEnvelope<TestMessage> _createEnvelopeWithoutSecurityContext() {
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = null
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private static ServiceProvider _createServiceProvider() {
    var services = new ServiceCollection();
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    return services.BuildServiceProvider();
  }

  // ========================================
  // Test Doubles
  // ========================================

  private sealed record TestMessage(string Value);
  private sealed record HealthCheckMessage;

  private sealed class TestExtractor(int priority, SecurityExtraction? extraction) : ISecurityContextExtractor {
    public int Priority => priority;

    public ValueTask<SecurityExtraction?> ExtractAsync(
      IMessageEnvelope envelope,
      MessageSecurityOptions options,
      CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(extraction);
    }
  }

  private sealed class TestSecurityContextCallback(Action<IScopeContext> onEstablished) : ISecurityContextCallback {
    public ValueTask OnContextEstablishedAsync(
      IScopeContext context,
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
      onEstablished(context);
      return ValueTask.CompletedTask;
    }
  }
}
