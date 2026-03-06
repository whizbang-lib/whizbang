using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for SecurityContextHelper.
/// Ensures consistent security context establishment across all message processing paths.
/// </summary>
/// <tests>SecurityContextHelper</tests>
[Category("Security")]
public class SecurityContextHelperTests {
  // === Baseline AsyncLocal Test ===

  [Test]
  public async Task AsyncLocal_BaselineTest_ValuePersistsAfterAwaitAsync() {
    // This test verifies our understanding of AsyncLocal behavior
    var scopeAccessor = new ScopeContextAccessor();
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "test" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set value
    scopeAccessor.Current = context;

    // Await something
    await Task.Yield();

    // Should still have value
    await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull().Because("AsyncLocal should persist after await");
    await Assert.That(scopeAccessor.Current).IsNotNull().Because("Instance accessor should have value");
  }

  [Test]
  public async Task AsyncLocal_SetInHelperAfterAwait_DoesNotPersistAsync() {
    // This test demonstrates that AsyncLocal values set AFTER await in called method
    // do NOT flow back to caller - this is expected .NET behavior!
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "test" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set value in a helper method AFTER await
    await _setCurrentContextAfterAwaitHelperAsync(context);

    // Value does NOT persist after returning from helper (expected behavior!)
    await Assert.That(ScopeContextAccessor.CurrentContext).IsNull()
      .Because("AsyncLocal set after await in helper does NOT flow back to caller");
  }

  [Test]
  public async Task AsyncLocal_SetInHelperBeforeAwait_DoesPersistAsync() {
    // This test demonstrates that AsyncLocal values set BEFORE await in called method
    // DO flow back to caller
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "test" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set value in a helper method BEFORE await
    await _setCurrentContextBeforeAwaitHelperAsync(context);

    // Value DOES persist (assuming no await after setting, or sync completion)
    await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull()
      .Because("AsyncLocal set before await in sync-completing helper DOES flow back");
  }

  private static async ValueTask _setCurrentContextAfterAwaitHelperAsync(IScopeContext context) {
    await Task.Yield(); // Yields control
    ScopeContextAccessor.CurrentContext = context; // Set AFTER yield - won't flow back
  }

  private static ValueTask _setCurrentContextBeforeAwaitHelperAsync(IScopeContext context) {
    ScopeContextAccessor.CurrentContext = context; // Set synchronously
    return ValueTask.CompletedTask; // No actual await
  }

  [Test]
  public async Task AsyncLocal_ProductionPattern_ValueVisibleToNestedCallAsync() {
    // This simulates the production pattern: set value after await, then invoke nested async method
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "test" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Simulate ReceptorInvoker: await something, set value, then invoke receptor
    IScopeContext? valueReadByReceptor = null;
    await _simulateReceptorInvokerPatternAsync(context, () => {
      valueReadByReceptor = ScopeContextAccessor.CurrentContext;
      return ValueTask.CompletedTask;
    });

    // The receptor should have seen the value!
    await Assert.That(valueReadByReceptor).IsNotNull()
      .Because("Receptor invoked after setting value should see it");
  }

  private static async ValueTask _simulateReceptorInvokerPatternAsync(
      IScopeContext context,
      Func<ValueTask> receptor) {
    // Simulate awaiting the security provider
    await Task.Yield();

    // Set the value (after the await, just like SecurityContextHelper)
    ScopeContextAccessor.CurrentContext = context;

    // Now invoke the receptor - it should see the value!
    await receptor();
  }

  [Test]
  public async Task AsyncLocal_NestedHelperPattern_ValueNotVisibleToSiblingAsync() {
    // This verifies that when a helper sets a value, sibling calls don't see it
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "test" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    var context = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Call helper that sets value after yield
    await _setContextAfterYieldAsync(context);

    // Now call another method - it should NOT see the value
    IScopeContext? valueSeenBySibling = null;
    await _readContextAsync(v => valueSeenBySibling = v);

    // Sibling does NOT see the value (because it was set in helper's child context)
    await Assert.That(valueSeenBySibling).IsNull()
      .Because("Value set in helper after yield doesn't flow to sibling calls");
  }

  private static async ValueTask _setContextAfterYieldAsync(IScopeContext context) {
    await Task.Yield();
    ScopeContextAccessor.CurrentContext = context;
  }

  private static async ValueTask _readContextAsync(Action<IScopeContext?> callback) {
    await Task.Yield();
    callback(ScopeContextAccessor.CurrentContext);
  }

  // === EstablishScopeContextAsync Tests ===

  [Test]
  public async Task EstablishScopeContextAsync_WithProvider_ReturnsContextAndSetsAccessorAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));
    var expectedTenantId = "tenant-123";
    var expectedUserId = "user-456";

    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = expectedTenantId, UserId = expectedUserId },
      Roles = new HashSet<string> { "User" },
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };

    // Use a capturing accessor to verify the setter was called
    var capturingAccessor = new CapturingScopeContextAccessor();
    var services = _createServiceProviderWithSecurity(extraction, capturingAccessor);

    // Act
    var result = await SecurityContextHelper.EstablishScopeContextAsync(envelope, services, CancellationToken.None);

    // Assert - helper returns the correct context
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(result.Scope.UserId).IsEqualTo(expectedUserId);

    // Assert - accessor's Current setter was called with the correct value
    // (Note: Due to AsyncLocal behavior, we can't read the value back from the accessor
    // after the helper returns. Instead, we verify via the capturing accessor.)
    await Assert.That(capturingAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingAccessor.CapturedContext!.Scope.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(capturingAccessor.CapturedContext.Scope.UserId).IsEqualTo(expectedUserId);
  }

  /// <summary>
  /// Accessor that captures the value set to Current (for testing purposes).
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
  }

  [Test]
  public async Task EstablishScopeContextAsync_NoProvider_ReturnsNullAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));
    var services = new ServiceCollection().BuildServiceProvider();

    // Act
    var result = await SecurityContextHelper.EstablishScopeContextAsync(envelope, services, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task EstablishScopeContextAsync_ProviderReturnsNull_DoesNotSetAccessorAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));
    var scopeAccessor = new ScopeContextAccessor();
    var services = _createServiceProviderWithSecurity(extraction: null, scopeAccessor);

    // Act
    var result = await SecurityContextHelper.EstablishScopeContextAsync(envelope, services, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(scopeAccessor.Current).IsNull();
  }

  [Test]
  public async Task EstablishScopeContextAsync_NoScopeAccessor_GracefulNoOpAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };

    // Build services WITHOUT ScopeContextAccessor
    var services = new ServiceCollection();
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new TestExtractor(100, extraction)],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    var sp = services.BuildServiceProvider();

    // Act - should not throw
    var result = await SecurityContextHelper.EstablishScopeContextAsync(envelope, sp, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull(); // Context was established
  }

  // === SetMessageContextFromEnvelope Tests ===

  [Test]
  public async Task SetMessageContextFromEnvelope_WithSecurityContext_SetsUserIdAsync() {
    // Arrange
    var expectedUserId = "user-789";
    var envelope = _createEnvelopeWithSecurityContext(new TestSecurityMessage("test"), expectedUserId);
    var messageContextAccessor = new MessageContextAccessor();
    var services = _createServiceProviderWithMessageAccessor(messageContextAccessor);

    // Act
    SecurityContextHelper.SetMessageContextFromEnvelope(envelope, services);

    // Assert
    await Assert.That(messageContextAccessor.Current).IsNotNull();
    await Assert.That(messageContextAccessor.Current!.UserId).IsEqualTo(expectedUserId);
    await Assert.That(messageContextAccessor.Current.MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task SetMessageContextFromEnvelope_WithSecurityContext_SetsTenantIdAsync() {
    // Arrange
    var expectedTenantId = "tenant-from-hop";
    var envelope = _createEnvelopeWithSecurityContextAndTenant(new TestSecurityMessage("test"), "user-1", expectedTenantId);
    var messageContextAccessor = new MessageContextAccessor();
    var services = _createServiceProviderWithMessageAccessor(messageContextAccessor);

    // Act
    SecurityContextHelper.SetMessageContextFromEnvelope(envelope, services);

    // Assert
    await Assert.That(messageContextAccessor.Current).IsNotNull();
    await Assert.That(messageContextAccessor.Current!.TenantId).IsEqualTo(expectedTenantId);
  }

  [Test]
  public async Task SetMessageContextFromEnvelope_NoSecurityContext_SetsNullUserIdAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));
    var messageContextAccessor = new MessageContextAccessor();
    var services = _createServiceProviderWithMessageAccessor(messageContextAccessor);

    // Act
    SecurityContextHelper.SetMessageContextFromEnvelope(envelope, services);

    // Assert
    await Assert.That(messageContextAccessor.Current).IsNotNull();
    await Assert.That(messageContextAccessor.Current!.UserId).IsNull();
    await Assert.That(messageContextAccessor.Current.MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task SetMessageContextFromEnvelope_NoAccessor_GracefulNoOpAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));
    var services = new ServiceCollection().BuildServiceProvider();

    // Act - should not throw
    SecurityContextHelper.SetMessageContextFromEnvelope(envelope, services);

    // Assert - no exception thrown
    await Task.CompletedTask;
  }

  [Test]
  public async Task SetMessageContextFromEnvelope_SetsCorrectTimestampAsync() {
    // Arrange
    var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"), timestamp);
    var messageContextAccessor = new MessageContextAccessor();
    var services = _createServiceProviderWithMessageAccessor(messageContextAccessor);

    // Act
    SecurityContextHelper.SetMessageContextFromEnvelope(envelope, services);

    // Assert
    await Assert.That(messageContextAccessor.Current!.Timestamp).IsEqualTo(timestamp);
  }

  // === EstablishFullContextAsync Tests ===

  [Test]
  public async Task EstablishFullContextAsync_SetsBothContextsAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));
    var expectedTenantId = "tenant-full";
    var expectedUserId = "user-full";

    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = expectedTenantId, UserId = expectedUserId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };

    // Use capturing accessors to verify setters were called
    var capturingScopeAccessor = new CapturingScopeContextAccessor();
    var capturingMessageAccessor = new CapturingMessageContextAccessor();
    var services = _createServiceProviderWithCapturingAccessors(extraction, capturingScopeAccessor, capturingMessageAccessor);

    // Act
    await SecurityContextHelper.EstablishFullContextAsync(envelope, services, CancellationToken.None);

    // Assert - Both accessors had their Current property set correctly
    await Assert.That(capturingScopeAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingScopeAccessor.CapturedContext!.Scope.TenantId).IsEqualTo(expectedTenantId);

    await Assert.That(capturingMessageAccessor.CapturedContext).IsNotNull();
    await Assert.That(capturingMessageAccessor.CapturedContext!.MessageId).IsEqualTo(envelope.MessageId);
  }

  /// <summary>
  /// Accessor that captures the value set to Current (for testing purposes).
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

  private static ServiceProvider _createServiceProviderWithCapturingAccessors(
      SecurityExtraction extraction,
      CapturingScopeContextAccessor scopeAccessor,
      CapturingMessageContextAccessor messageContextAccessor) {
    var services = new ServiceCollection();

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new TestExtractor(100, extraction)],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    services.AddSingleton<IScopeContextAccessor>(scopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(messageContextAccessor);

    return services.BuildServiceProvider();
  }

  // === EstablishMessageContextForCascade Tests ===

  [Test]
  public async Task EstablishMessageContextForCascade_WithScopeContext_PropagatesUserIdAsync() {
    // Arrange
    var expectedUserId = "user-cascade";
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = expectedUserId, TenantId = "tenant-cascade" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    var scopeContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert
      await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(MessageContextAccessor.CurrentContext!.UserId).IsEqualTo(expectedUserId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithScopeContext_PropagatesTenantIdAsync() {
    // Arrange
    var expectedTenantId = "tenant-cascade";
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "user-cascade", TenantId = expectedTenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    var scopeContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert
      await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(MessageContextAccessor.CurrentContext!.TenantId).IsEqualTo(expectedTenantId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_NoScopeContext_SetsNullUserIdAsync() {
    // Arrange
    ScopeContextAccessor.CurrentContext = null;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert
      await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(MessageContextAccessor.CurrentContext!.UserId).IsNull();
      // Should still have a valid MessageId
      await Assert.That(MessageContextAccessor.CurrentContext.MessageId.Value).IsNotEqualTo(Guid.Empty);
    } finally {
      // Cleanup
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_CreatesNewMessageIdAsync() {
    // Arrange
    ScopeContextAccessor.CurrentContext = null;

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert
      var messageId = MessageContextAccessor.CurrentContext!.MessageId;
      await Assert.That(messageId.Value).IsNotEqualTo(Guid.Empty);
    } finally {
      // Cleanup
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_NonImmutableContext_SetsNullUserIdAsync() {
    // Arrange - Set a non-ImmutableScopeContext (plain ScopeContext)
    ScopeContextAccessor.CurrentContext = new ScopeContext {
      Scope = new PerspectiveScope { UserId = "should-not-be-used" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    try {
      // Act
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert - Should NOT propagate UserId from non-immutable context
      // The cascade path only reads from ImmutableScopeContext (which has ShouldPropagate flag)
      await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(MessageContextAccessor.CurrentContext!.UserId).IsNull();
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  // === NEW TDD Tests for ScopeContextAccessor Establishment (RED Phase) ===

  [Test]
  public async Task EstablishMessageContextForCascade_WithParentContext_SetsScopeContextAccessorCurrentAsync() {
    // Arrange: Set MessageContextAccessor.CurrentContext with UserId/TenantId (simulate parent)
    var expectedUserId = "user-new-test-123";
    var expectedTenantId = "tenant-new-test-456";
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = expectedUserId,
      TenantId = expectedTenantId
    };

    try {
      // Act: Call EstablishMessageContextForCascade()
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert: ScopeContextAccessor.CurrentContext is NOT NULL
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(ScopeContextAccessor.CurrentContext!.Scope.UserId).IsEqualTo(expectedUserId);
      await Assert.That(ScopeContextAccessor.CurrentContext.Scope.TenantId).IsEqualTo(expectedTenantId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithParentContext_SetsScopeContextShouldPropagateTrueAsync() {
    // Arrange: Set MessageContextAccessor.CurrentContext with UserId/TenantId
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = "user-prop-test",
      TenantId = "tenant-prop-test"
    };

    try {
      // Act: Call EstablishMessageContextForCascade()
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert: ScopeContextAccessor.CurrentContext.ShouldPropagate is TRUE
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
      var immutableContext = ScopeContextAccessor.CurrentContext as ImmutableScopeContext;
      await Assert.That(immutableContext).IsNotNull();
      await Assert.That(immutableContext!.ShouldPropagate).IsTrue();
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithNoUserId_DoesNotSetScopeContextAccessorAsync() {
    // Arrange: Set MessageContextAccessor.CurrentContext with NULL UserId and TenantId
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = null,
      TenantId = null
    };

    try {
      // Act: Call EstablishMessageContextForCascade()
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert: ScopeContextAccessor.CurrentContext remains NULL (no context created)
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNull();
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithOnlyTenantId_SetsScopeContextAccessorCurrentAsync() {
    // Arrange: Set MessageContextAccessor.CurrentContext with NULL UserId, valid TenantId
    var expectedTenantId = "tenant-only-test";
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = null,
      TenantId = expectedTenantId
    };

    try {
      // Act: Call EstablishMessageContextForCascade()
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert: ScopeContextAccessor.CurrentContext is NOT NULL
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(ScopeContextAccessor.CurrentContext!.Scope.UserId).IsNull();
      await Assert.That(ScopeContextAccessor.CurrentContext.Scope.TenantId).IsEqualTo(expectedTenantId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithOnlyUserId_SetsScopeContextAccessorCurrentAsync() {
    // Arrange: Set MessageContextAccessor.CurrentContext with valid UserId, NULL TenantId
    var expectedUserId = "user-only-test";
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = expectedUserId,
      TenantId = null
    };

    try {
      // Act: Call EstablishMessageContextForCascade()
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert: ScopeContextAccessor.CurrentContext is NOT NULL
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(ScopeContextAccessor.CurrentContext!.Scope.UserId).IsEqualTo(expectedUserId);
      await Assert.That(ScopeContextAccessor.CurrentContext.Scope.TenantId).IsNull();
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithScopeContextFallback_SetsScopeContextAccessorCurrentAsync() {
    // Arrange: Set ScopeContextAccessor.CurrentContext with UserId/TenantId (NO MessageContextAccessor)
    var expectedUserId = "user-fallback-test";
    var expectedTenantId = "tenant-fallback-test";
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = expectedUserId, TenantId = expectedTenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(extraction, shouldPropagate: true);

    try {
      // Act: Call EstablishMessageContextForCascade()
      SecurityContextHelper.EstablishMessageContextForCascade();

      // Assert: ScopeContextAccessor.CurrentContext is NOT NULL (reads from existing, creates new)
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(ScopeContextAccessor.CurrentContext!.Scope.UserId).IsEqualTo(expectedUserId);
      await Assert.That(ScopeContextAccessor.CurrentContext.Scope.TenantId).IsEqualTo(expectedTenantId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithExplicitContext_TakesPriorityOverAsyncLocalAsync() {
    // Arrange: Set BOTH explicit context (via IScopeContextAccessor.Current) AND AsyncLocal context
    // This simulates AsSystem/RunAs scenario where explicit context should take priority
    var explicitUserId = "explicit-user-id";
    var explicitTenantId = "explicit-tenant-id";
    var asyncLocalUserId = "asynclocal-user-id";
    var asyncLocalTenantId = "asynclocal-tenant-id";

    // Set AsyncLocal context (this would normally come from parent receptor)
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = asyncLocalUserId,
      TenantId = asyncLocalTenantId
    };

    // Create explicit context (this would be set by AsSystem/RunAs)
    var explicitExtraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = explicitUserId, TenantId = explicitTenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "ExplicitContext"
    };
    var explicitContext = new ImmutableScopeContext(explicitExtraction, shouldPropagate: true);

    // Create a scoped service provider with IScopeContextAccessor set to explicit context
    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor>(new MockScopeContextAccessor(explicitContext));
    var serviceProvider = services.BuildServiceProvider();

    try {
      // Act: Call EstablishMessageContextForCascade with service provider that has explicit context
      SecurityContextHelper.EstablishMessageContextForCascade(serviceProvider);

      // Assert: MessageContextAccessor should use EXPLICIT context values, not AsyncLocal
      var result = MessageContextAccessor.CurrentContext;
      await Assert.That(result).IsNotNull();
      await Assert.That(result!.UserId).IsEqualTo(explicitUserId);
      await Assert.That(result.TenantId).IsEqualTo(explicitTenantId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task EstablishMessageContextForCascade_WithExplicitContext_DoesNotOverwriteScopeContextAccessorAsync() {
    // Arrange: Set explicit context (via IScopeContextAccessor.Current)
    // Verify that EstablishMessageContextForCascade does NOT overwrite ScopeContextAccessor.CurrentContext
    var explicitUserId = "explicit-user-id";
    var explicitTenantId = "explicit-tenant-id";

    // Create explicit context (this would be set by AsSystem/RunAs)
    var explicitExtraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = explicitUserId, TenantId = explicitTenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "ExplicitContext"
    };
    var explicitContext = new ImmutableScopeContext(explicitExtraction, shouldPropagate: true);

    // Create a scoped service provider with IScopeContextAccessor set to explicit context
    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor>(new MockScopeContextAccessor(explicitContext));
    var serviceProvider = services.BuildServiceProvider();

    // Set AsyncLocal to a different value
    var asyncLocalExtraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "asynclocal-user", TenantId = "asynclocal-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "AsyncLocal"
    };
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(asyncLocalExtraction, shouldPropagate: true);

    try {
      // Act: Call EstablishMessageContextForCascade with service provider that has explicit context
      SecurityContextHelper.EstablishMessageContextForCascade(serviceProvider);

      // Assert: ScopeContextAccessor.CurrentContext should still be the AsyncLocal value
      // because we don't overwrite when explicit context exists (hasExplicitContext = true)
      var result = ScopeContextAccessor.CurrentContext;
      await Assert.That(result).IsNotNull();
      // The value should be the AsyncLocal one, not modified
      await Assert.That(result!.Scope.UserId).IsEqualTo("asynclocal-user");
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      MessageContextAccessor.CurrentContext = null;
    }
  }

  // Mock implementation for IScopeContextAccessor used in explicit context tests
  private sealed class MockScopeContextAccessor : IScopeContextAccessor {
    private readonly IScopeContext? _explicitContext;

    public MockScopeContextAccessor(IScopeContext? explicitContext) {
      _explicitContext = explicitContext;
    }

    public IScopeContext? Current {
      get => _explicitContext;
      set { /* Ignore sets - we're simulating explicit context that shouldn't be overwritten */ }
    }
  }

  // === Argument Validation Tests ===

  [Test]
  public async Task EstablishScopeContextAsync_NullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection().BuildServiceProvider();

    // Act & Assert
    await Assert.That(async () =>
      await SecurityContextHelper.EstablishScopeContextAsync(null!, services, CancellationToken.None)
    ).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task EstablishScopeContextAsync_NullProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));

    // Act & Assert
    await Assert.That(async () =>
      await SecurityContextHelper.EstablishScopeContextAsync(envelope, null!, CancellationToken.None)
    ).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SetMessageContextFromEnvelope_NullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection().BuildServiceProvider();

    // Act & Assert
    ArgumentNullException? caught = null;
    try {
      SecurityContextHelper.SetMessageContextFromEnvelope(null!, services);
    } catch (ArgumentNullException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
  }

  [Test]
  public async Task SetMessageContextFromEnvelope_NullProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var envelope = _createTestEnvelope(new TestSecurityMessage("test"));

    // Act & Assert
    ArgumentNullException? caught = null;
    try {
      SecurityContextHelper.SetMessageContextFromEnvelope(envelope, null!);
    } catch (ArgumentNullException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
  }

  // === Helper Methods ===

  private static ServiceProvider _createServiceProviderWithSecurity(
      SecurityExtraction? extraction,
      IScopeContextAccessor scopeAccessor) {
    var services = new ServiceCollection();

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: extraction is null ? [] : [new TestExtractor(100, extraction)],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    services.AddSingleton<IScopeContextAccessor>(scopeAccessor);

    return services.BuildServiceProvider();
  }

  private static ServiceProvider _createServiceProviderWithMessageAccessor(MessageContextAccessor accessor) {
    var services = new ServiceCollection();
    services.AddSingleton<IMessageContextAccessor>(accessor);
    return services.BuildServiceProvider();
  }

  private static ServiceProvider _createServiceProviderWithBothAccessors(
      SecurityExtraction extraction,
      ScopeContextAccessor scopeAccessor,
      MessageContextAccessor messageContextAccessor) {
    var services = new ServiceCollection();

    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [new TestExtractor(100, extraction)],
      callbacks: [],
      options: new MessageSecurityOptions { AllowAnonymous = true }
    );
    services.AddSingleton<IMessageSecurityContextProvider>(provider);
    services.AddSingleton<IScopeContextAccessor>(scopeAccessor);
    services.AddSingleton<IMessageContextAccessor>(messageContextAccessor);

    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<TMessage> _createTestEnvelope<TMessage>(
      TMessage payload,
      DateTimeOffset? timestamp = null) where TMessage : notnull {
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
          Timestamp = timestamp ?? DateTimeOffset.UtcNow,
          Topic = "test-topic"
        }
      ]
    };
  }

  private static MessageEnvelope<TMessage> _createEnvelopeWithSecurityContext<TMessage>(
      TMessage payload,
      string userId) where TMessage : notnull {
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
          Topic = "test-topic",
          SecurityContext = new SecurityContext { UserId = userId, TenantId = "test-tenant" }
        }
      ]
    };
  }

  private static MessageEnvelope<TMessage> _createEnvelopeWithSecurityContextAndTenant<TMessage>(
      TMessage payload,
      string userId,
      string tenantId) where TMessage : notnull {
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
          Topic = "test-topic",
          SecurityContext = new SecurityContext { UserId = userId, TenantId = tenantId }
        }
      ]
    };
  }

  // Test message types
  private sealed record TestSecurityMessage(string Value);

  // Test extractor for mocking security extraction
  private sealed class TestExtractor : ISecurityContextExtractor {
    private readonly int _priority;
    private readonly SecurityExtraction? _extraction;

    public TestExtractor(int priority, SecurityExtraction? extraction) {
      _priority = priority;
      _extraction = extraction;
    }

    public int Priority => _priority;

    public ValueTask<SecurityExtraction?> ExtractAsync(
        IMessageEnvelope envelope,
        MessageSecurityOptions options,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(_extraction);
    }
  }
}
