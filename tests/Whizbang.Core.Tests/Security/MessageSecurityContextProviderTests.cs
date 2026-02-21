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
}
