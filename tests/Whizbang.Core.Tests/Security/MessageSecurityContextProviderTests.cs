using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the IMessageSecurityContextProvider and DefaultMessageSecurityContextProvider.
/// TDD: Tests written first, implementation follows.
/// </summary>
/// <tests>IMessageSecurityContextProvider</tests>
/// <tests>DefaultMessageSecurityContextProvider</tests>
[Category("Security")]
public class MessageSecurityContextProviderTests {
  // === Provider with No Extractors Tests ===

  [Test]
  public async Task EstablishContextAsync_NoExtractors_AllowAnonymousFalse_ThrowsSecurityContextRequiredExceptionAsync() {
    // Arrange
    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act & Assert
    var exception = await Assert.That(async () =>
      await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None)
    ).ThrowsExactly<SecurityContextRequiredException>();

    await Assert.That(exception!.Message).Contains("Security context");
  }

  [Test]
  public async Task EstablishContextAsync_NoExtractors_AllowAnonymousTrue_ReturnsNullAsync() {
    // Arrange
    var options = new MessageSecurityOptions { AllowAnonymous = true };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    var result = await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  // === Extractor Priority Order Tests ===

  [Test]
  public async Task EstablishContextAsync_MultipleExtractors_CallsInPriorityOrderAsync() {
    // Arrange
    var callOrder = new List<string>();
    var extractor1 = new TestExtractor(
      priority: 100,
      onExtract: () => callOrder.Add("100"),
      extraction: null
    );
    var extractor2 = new TestExtractor(
      priority: 50,
      onExtract: () => callOrder.Add("50"),
      extraction: null
    );
    var extractor3 = new TestExtractor(
      priority: 200,
      onExtract: () => callOrder.Add("200"),
      extraction: null
    );

    var options = new MessageSecurityOptions { AllowAnonymous = true };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor1, extractor2, extractor3],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert - Should be called in priority order (lower first)
    await Assert.That(callOrder).IsEquivalentTo(["50", "100", "200"]);
  }

  [Test]
  public async Task EstablishContextAsync_MultipleExtractors_StopsAfterFirstSuccessfulExtractionAsync() {
    // Arrange
    var callOrder = new List<string>();
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Roles = new HashSet<string> { "Admin" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestExtractor"
    };

    var extractor1 = new TestExtractor(
      priority: 50,
      onExtract: () => callOrder.Add("50"),
      extraction: extraction // Returns successfully
    );
    var extractor2 = new TestExtractor(
      priority: 100,
      onExtract: () => callOrder.Add("100"),
      extraction: null
    );

    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor1, extractor2],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    var result = await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert - Only first extractor should be called (it succeeded)
    await Assert.That(callOrder).IsEquivalentTo(["50"]);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("tenant-1");
  }

  [Test]
  public async Task EstablishContextAsync_FirstExtractorReturnsNull_TriesNextExtractorAsync() {
    // Arrange
    var callOrder = new List<string>();
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-2" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "SecondExtractor"
    };

    var extractor1 = new TestExtractor(
      priority: 50,
      onExtract: () => callOrder.Add("50"),
      extraction: null // Returns null
    );
    var extractor2 = new TestExtractor(
      priority: 100,
      onExtract: () => callOrder.Add("100"),
      extraction: extraction // Returns successfully
    );

    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor1, extractor2],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    var result = await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(callOrder).IsEquivalentTo(["50", "100"]);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("tenant-2");
  }

  // === Callback Tests ===

  [Test]
  public async Task EstablishContextAsync_WithCallbacks_CallsAllCallbacksAfterContextEstablishedAsync() {
    // Arrange
    var callbackOrder = new List<string>();
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestExtractor"
    };

    var extractor = new TestExtractor(priority: 100, extraction: extraction);
    var callback1 = new TestCallback(onCallback: (ctx) => callbackOrder.Add($"callback1:{ctx.Scope.TenantId}"));
    var callback2 = new TestCallback(onCallback: (ctx) => callbackOrder.Add($"callback2:{ctx.Scope.TenantId}"));

    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [callback1, callback2],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(callbackOrder).IsEquivalentTo(["callback1:tenant-1", "callback2:tenant-1"]);
  }

  [Test]
  public async Task EstablishContextAsync_NoContextEstablished_DoesNotCallCallbacksAsync() {
    // Arrange
    var callbackCalled = false;
    var callback = new TestCallback(onCallback: (_) => callbackCalled = true);

    var options = new MessageSecurityOptions { AllowAnonymous = true };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [],
      callbacks: [callback],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(callbackCalled).IsFalse();
  }

  // === Exempt Message Types Tests ===

  [Test]
  public async Task EstablishContextAsync_ExemptMessageType_BypassesSecurityAsync() {
    // Arrange
    var extractorCalled = false;
    var extractor = new TestExtractor(
      priority: 100,
      onExtract: () => extractorCalled = true,
      extraction: null
    );

    var options = new MessageSecurityOptions {
      AllowAnonymous = false,
      ExemptMessageTypes = { typeof(HealthCheckMessage) }
    };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new HealthCheckMessage());

    // Act
    var result = await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert - Extractor should not be called for exempt types
    await Assert.That(extractorCalled).IsFalse();
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task EstablishContextAsync_NonExemptMessageType_EnforcesSecurityAsync() {
    // Arrange
    var options = new MessageSecurityOptions {
      AllowAnonymous = false,
      ExemptMessageTypes = { typeof(HealthCheckMessage) }
    };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("not-exempt"));

    // Act & Assert - Non-exempt message should throw
    await Assert.That(async () =>
      await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None)
    ).ThrowsExactly<SecurityContextRequiredException>();
  }

  // === Timeout Tests ===

  [Test]
  public async Task EstablishContextAsync_ExtractorExceedsTimeout_ThrowsTimeoutExceptionAsync() {
    // Arrange
    var extractor = new TestExtractor(
      priority: 100,
      onExtractAsync: async ct => await Task.Delay(TimeSpan.FromSeconds(10), ct),
      extraction: null
    );

    var options = new MessageSecurityOptions {
      AllowAnonymous = false,
      Timeout = TimeSpan.FromMilliseconds(50)
    };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act & Assert
    await Assert.That(async () =>
      await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None)
    ).Throws<TimeoutException>();
  }

  // === ImmutableScopeContext Tests ===

  [Test]
  public async Task EstablishContextAsync_ReturnsImmutableScopeContextAsync() {
    // Arrange
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string> { "Admin" },
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string> { ["key"] = "value" },
      Source = "TestExtractor"
    };

    var extractor = new TestExtractor(priority: 100, extraction: extraction);
    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    var result = await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<ImmutableScopeContext>();

    var immutable = (ImmutableScopeContext)result!;
    await Assert.That(immutable.Source).IsEqualTo("TestExtractor");
    await Assert.That(immutable.EstablishedAt).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  // === Cancellation Tests ===

  [Test]
  public async Task EstablishContextAsync_CancellationRequested_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var extractor = new TestExtractor(
      priority: 100,
      onExtractAsync: async ct => await Task.Delay(TimeSpan.FromSeconds(10), ct),
      extraction: null
    );

    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () =>
      await provider.EstablishContextAsync(envelope, _createServiceProvider(), cts.Token)
    ).ThrowsExactly<OperationCanceledException>();
  }

  // === Validate Credentials Tests ===

  [Test]
  public async Task EstablishContextAsync_ValidateCredentialsTrue_PassesValidationFlagToExtractorAsync() {
    // Arrange
    var receivedValidateFlag = false;
    var extractor = new TestExtractor(
      priority: 100,
      onExtract: () => { },
      extraction: null,
      onExtractWithContext: (envelope, options) => {
        receivedValidateFlag = options.ValidateCredentials;
      }
    );

    var options = new MessageSecurityOptions {
      AllowAnonymous = true,
      ValidateCredentials = true
    };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(receivedValidateFlag).IsTrue();
  }

  // === Propagate to Outgoing Messages Tests ===

  [Test]
  public async Task EstablishContextAsync_PropagateToOutgoingTrue_SetsContextForPropagationAsync() {
    // Arrange
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestExtractor"
    };

    var extractor = new TestExtractor(priority: 100, extraction: extraction);
    var options = new MessageSecurityOptions {
      AllowAnonymous = false,
      PropagateToOutgoingMessages = true
    };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    var result = await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    var immutable = (ImmutableScopeContext)result!;
    await Assert.That(immutable.ShouldPropagate).IsTrue();
  }

  // === Audit Logging Tests ===

  [Test]
  public async Task EstablishContextAsync_EnableAuditLoggingTrue_EmitsAuditEventAsync() {
    // Arrange
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestExtractor"
    };

    var auditEvents = new List<ScopeContextEstablished>();
    var extractor = new TestExtractor(priority: 100, extraction: extraction);
    var options = new MessageSecurityOptions {
      AllowAnonymous = false,
      EnableAuditLogging = true
    };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options,
      onAuditEvent: auditEvents.Add
    );
    var envelope = _createTestEnvelope(new TestMessage("test"));

    // Act
    await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(auditEvents.Count).IsEqualTo(1);
    await Assert.That(auditEvents[0].Source).IsEqualTo("TestExtractor");
    await Assert.That(auditEvents[0].Scope.TenantId).IsEqualTo("tenant-1");
  }

  // === Events Without Security Context Tests ===

  /// <summary>
  /// CRITICAL: Verifies that events published without security context (e.g., from system seeding)
  /// throw SecurityContextRequiredException when AllowAnonymous is false.
  /// This reproduces the issue where system events like FilterSubscriptionTemplateCreatedEvent
  /// fail during PerspectiveWorker or ReceptorInvoker processing.
  ///
  /// FIX: Use dispatcher.AsSystem().PublishAsync() for system-initiated events.
  /// </summary>
  /// <tests>DefaultMessageSecurityContextProvider.EstablishContextAsync</tests>
  [Test]
  public async Task EstablishContextAsync_EventWithoutSecurityContext_ThrowsSecurityContextRequiredExceptionAsync() {
    // Arrange - Simulates an event envelope that was published without security context
    // This happens when code calls dispatcher.PublishAsync() without AsSystem()
    // during system seeding or background jobs
    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [],  // No extractors that can extract context from envelope
      callbacks: [],
      options: options
    );

    // Create envelope with NO scope in hops (simulating event published without security context)
    var envelope = new MessageEnvelope<SystemSeedingEvent> {
      MessageId = MessageId.New(),
      Payload = new SystemSeedingEvent("FilterSubscriptionTemplateCreated"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 1234
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "system-seeding",
          Scope = null  // NO security context - this is the problem!
        }
      ]
    };

    // Act & Assert - Should throw because no security context and AllowAnonymous = false
    var exception = await Assert.That(async () =>
      await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None)
    ).ThrowsExactly<SecurityContextRequiredException>();

    await Assert.That(exception!.Message).Contains("SystemSeedingEvent");
  }

  /// <summary>
  /// Verifies that events published WITH security context (via AsSystem()) are processed successfully.
  /// This demonstrates the correct pattern for system-initiated events.
  /// </summary>
  /// <tests>DefaultMessageSecurityContextProvider.EstablishContextAsync</tests>
  [Test]
  public async Task EstablishContextAsync_EventWithSystemSecurityContext_SucceedsAsync() {
    // Arrange - Add an extractor that can read security context from envelope hops
    var extractor = new HopSecurityContextExtractor();
    var options = new MessageSecurityOptions { AllowAnonymous = false };
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [extractor],
      callbacks: [],
      options: options
    );

    // Create envelope WITH security context in hops (simulating AsSystem().PublishAsync())
    var envelope = new MessageEnvelope<SystemSeedingEvent> {
      MessageId = MessageId.New(),
      Payload = new SystemSeedingEvent("FilterSubscriptionTemplateCreated"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 1234
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "system-seeding",
          // Security context IS set - this is what AsSystem() does
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = "SYSTEM",
            TenantId = null  // System operations may not have tenant
          })
        }
      ]
    };

    // Act - Should succeed because security context is present
    var result = await provider.EstablishContextAsync(envelope, _createServiceProvider(), CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.UserId).IsEqualTo("SYSTEM");
  }

  // === Helper Methods ===

  private static ServiceProvider _createServiceProvider() {
    var services = new ServiceCollection();
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<TMessage> _createTestEnvelope<TMessage>(TMessage payload) where TMessage : notnull {
    return new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 1234
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic"
        }
      ]
    };
  }

  // === Test Types ===

  private sealed record TestMessage(string Value);

  private sealed record HealthCheckMessage;

  /// <summary>
  /// Test double for ISecurityContextExtractor.
  /// </summary>
  private sealed class TestExtractor : ISecurityContextExtractor {
    private readonly Action? _onExtract;
    private readonly Func<CancellationToken, Task>? _onExtractAsync;
    private readonly Action<IMessageEnvelope, MessageSecurityOptions>? _onExtractWithContext;
    private readonly SecurityExtraction? _extraction;

    public int Priority { get; }

    public TestExtractor(
      int priority,
      SecurityExtraction? extraction = null,
      Action? onExtract = null,
      Func<CancellationToken, Task>? onExtractAsync = null,
      Action<IMessageEnvelope, MessageSecurityOptions>? onExtractWithContext = null) {
      Priority = priority;
      _extraction = extraction;
      _onExtract = onExtract;
      _onExtractAsync = onExtractAsync;
      _onExtractWithContext = onExtractWithContext;
    }

    public async ValueTask<SecurityExtraction?> ExtractAsync(
      IMessageEnvelope envelope,
      MessageSecurityOptions options,
      CancellationToken cancellationToken = default) {
      _onExtract?.Invoke();
      _onExtractWithContext?.Invoke(envelope, options);

      if (_onExtractAsync != null) {
        await _onExtractAsync(cancellationToken);
      }

      return _extraction;
    }
  }

  /// <summary>
  /// Test double for ISecurityContextCallback.
  /// </summary>
  private sealed class TestCallback : ISecurityContextCallback {
    private readonly Action<IScopeContext>? _onCallback;

    public TestCallback(Action<IScopeContext>? onCallback = null) {
      _onCallback = onCallback;
    }

    public ValueTask OnContextEstablishedAsync(
      IScopeContext context,
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
      _onCallback?.Invoke(context);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Test event simulating system seeding events like FilterSubscriptionTemplateCreatedEvent.
  /// </summary>
  private sealed record SystemSeedingEvent(string EventName) : IEvent;

  /// <summary>
  /// Test extractor that reads security context from envelope hops.
  /// This simulates how the real system extracts security context from message hops.
  /// </summary>
  private sealed class HopSecurityContextExtractor : ISecurityContextExtractor {
    public int Priority => 100;

    public ValueTask<SecurityExtraction?> ExtractAsync(
      IMessageEnvelope envelope,
      MessageSecurityOptions options,
      CancellationToken cancellationToken = default) {
      // Try to extract security context from envelope hops (like the real system does)
      var scopeContext = envelope.GetCurrentScope();
      if (scopeContext?.Scope is null) {
        return ValueTask.FromResult<SecurityExtraction?>(null);
      }

      var extraction = new SecurityExtraction {
        Scope = scopeContext.Scope,
        Roles = scopeContext.Roles ?? new HashSet<string>(),
        Permissions = scopeContext.Permissions ?? new HashSet<Permission>(),
        SecurityPrincipals = scopeContext.SecurityPrincipals ?? new HashSet<SecurityPrincipalId>(),
        Claims = scopeContext.Claims ?? new Dictionary<string, string>(),
        Source = "HopSecurityContextExtractor"
      };

      return ValueTask.FromResult<SecurityExtraction?>(extraction);
    }
  }
}
