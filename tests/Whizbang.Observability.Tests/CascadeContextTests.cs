using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for CascadeContext - encapsulates context to propagate between messages.
/// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs</tests>
/// </summary>
public class CascadeContextTests {

  // ========================================
  // RECORD INITIALIZATION TESTS
  // ========================================

  [Test]
  public async Task Constructor_WithRequiredProperties_InitializesCorrectlyAsync() {
    // Arrange
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var securityContext = new SecurityContext { UserId = "user-123", TenantId = "tenant-abc" };

    // Act
    var context = new CascadeContext {
      CorrelationId = correlationId,
      CausationId = causationId,
      SecurityContext = securityContext
    };

    // Assert
    await Assert.That(context.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(context.CausationId).IsEqualTo(causationId);
    await Assert.That(context.SecurityContext).IsEqualTo(securityContext);
    await Assert.That(context.Metadata).IsNull();
  }

  [Test]
  public async Task Constructor_WithNullSecurityContext_AllowsNullAsync() {
    // Arrange
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    // Act
    var context = new CascadeContext {
      CorrelationId = correlationId,
      CausationId = causationId,
      SecurityContext = null
    };

    // Assert
    await Assert.That(context.SecurityContext).IsNull();
  }

  [Test]
  public async Task Constructor_WithMetadata_SetsMetadataAsync() {
    // Arrange
    var metadata = new Dictionary<string, object> { ["key1"] = "value1", ["key2"] = 42 };

    // Act
    var context = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Metadata = metadata
    };

    // Assert
    await Assert.That(context.Metadata).IsNotNull();
    await Assert.That(context.Metadata!.Count).IsEqualTo(2);
    await Assert.That(context.Metadata["key1"]).IsEqualTo("value1");
    await Assert.That(context.Metadata["key2"]).IsEqualTo(42);
  }

  // ========================================
  // STATIC FACTORY METHOD TESTS
  // ========================================

  [Test]
  public async Task NewRoot_GeneratesNewCorrelationIdAsync() {
    // Arrange - Ensure no ambient context
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = CascadeContext.NewRoot();

    // Assert
    await Assert.That(context.CorrelationId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(context.CausationId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(context.SecurityContext).IsNull();
    await Assert.That(context.Metadata).IsNull();
  }

  [Test]
  public async Task NewRoot_GeneratesUniqueIds_AcrossMultipleCallsAsync() {
    // Arrange
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context1 = CascadeContext.NewRoot();
    var context2 = CascadeContext.NewRoot();

    // Assert
    await Assert.That(context1.CorrelationId).IsNotEqualTo(context2.CorrelationId);
    await Assert.That(context1.CausationId).IsNotEqualTo(context2.CausationId);
  }

  [Test]
  public async Task NewRootWithAmbientSecurity_NoAmbientContext_ReturnsNullSecurityAsync() {
    // Arrange
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = CascadeContext.NewRootWithAmbientSecurity();

    // Assert
    await Assert.That(context.SecurityContext).IsNull();
    await Assert.That(context.CorrelationId.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task NewRootWithAmbientSecurity_WithAmbientContext_InheritsSecurityAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";

    var scopeContext = _createTestScopeContext(testUserId, testTenantId, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      var context = CascadeContext.NewRootWithAmbientSecurity();

      // Assert
      await Assert.That(context.SecurityContext).IsNotNull();
      await Assert.That(context.SecurityContext!.UserId).IsEqualTo(testUserId);
      await Assert.That(context.SecurityContext.TenantId).IsEqualTo(testTenantId);
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task NewRootWithAmbientSecurity_WithAmbientContextButPropagationDisabled_ReturnsNullSecurityAsync() {
    // Arrange
    var scopeContext = _createTestScopeContext("user", "tenant", shouldPropagate: false);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      var context = CascadeContext.NewRootWithAmbientSecurity();

      // Assert
      await Assert.That(context.SecurityContext).IsNull();
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  // ========================================
  // GetSecurityFromAmbient TESTS
  // ========================================

  [Test]
  public async Task GetSecurityFromAmbient_NoAmbientContext_ReturnsNullAsync() {
    // Arrange
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var result = CascadeContext.GetSecurityFromAmbient();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetSecurityFromAmbient_WithNonImmutableContext_ReturnsNullAsync() {
    // Arrange - Use a non-ImmutableScopeContext
    var simpleContext = _createNonImmutableScopeContext("user", "tenant");
    ScopeContextAccessor.CurrentContext = simpleContext;

    try {
      // Act
      var result = CascadeContext.GetSecurityFromAmbient();

      // Assert - Should return null since it's not ImmutableScopeContext
      await Assert.That(result).IsNull();
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task GetSecurityFromAmbient_WithImmutableContextAndPropagation_ReturnsSecurityAsync() {
    // Arrange
    var testUserId = "propagate-user";
    var testTenantId = "propagate-tenant";

    var scopeContext = _createTestScopeContext(testUserId, testTenantId, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      var result = CascadeContext.GetSecurityFromAmbient();

      // Assert
      await Assert.That(result).IsNotNull();
      await Assert.That(result!.UserId).IsEqualTo(testUserId);
      await Assert.That(result.TenantId).IsEqualTo(testTenantId);
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task GetSecurityFromAmbient_WithImmutableContextButNoPropagation_ReturnsNullAsync() {
    // Arrange
    var scopeContext = _createTestScopeContext("user", "tenant", shouldPropagate: false);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      var result = CascadeContext.GetSecurityFromAmbient();

      // Assert
      await Assert.That(result).IsNull();
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  // ========================================
  // WithMetadata TESTS
  // ========================================

  [Test]
  public async Task WithMetadata_SingleKey_AddsMetadataAsync() {
    // Arrange
    var original = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    };

    // Act
    var updated = original.WithMetadata("key1", "value1");

    // Assert
    await Assert.That(updated.Metadata).IsNotNull();
    await Assert.That(updated.Metadata!["key1"]).IsEqualTo("value1");
    // Original unchanged
    await Assert.That(original.Metadata).IsNull();
  }

  [Test]
  public async Task WithMetadata_ExistingMetadata_MergesCorrectlyAsync() {
    // Arrange
    var original = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Metadata = new Dictionary<string, object> { ["existing"] = "value" }
    };

    // Act
    var updated = original.WithMetadata("new", "newValue");

    // Assert
    await Assert.That(updated.Metadata!.Count).IsEqualTo(2);
    await Assert.That(updated.Metadata["existing"]).IsEqualTo("value");
    await Assert.That(updated.Metadata["new"]).IsEqualTo("newValue");
  }

  [Test]
  public async Task WithMetadata_Dictionary_MergesAllEntriesAsync() {
    // Arrange
    var original = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Metadata = new Dictionary<string, object> { ["a"] = 1 }
    };
    var additional = new Dictionary<string, object> { ["b"] = 2, ["c"] = 3 };

    // Act
    var updated = original.WithMetadata(additional);

    // Assert
    await Assert.That(updated.Metadata!.Count).IsEqualTo(3);
    await Assert.That(updated.Metadata["a"]).IsEqualTo(1);
    await Assert.That(updated.Metadata["b"]).IsEqualTo(2);
    await Assert.That(updated.Metadata["c"]).IsEqualTo(3);
  }

  [Test]
  public async Task WithMetadata_OverwritesExistingKey_WhenSameKeyProvidedAsync() {
    // Arrange
    var original = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Metadata = new Dictionary<string, object> { ["key"] = "oldValue" }
    };

    // Act
    var updated = original.WithMetadata("key", "newValue");

    // Assert
    await Assert.That(updated.Metadata!["key"]).IsEqualTo("newValue");
  }

  // ========================================
  // RECORD EQUALITY TESTS
  // ========================================

  [Test]
  public async Task RecordEquality_SameValues_AreEqualAsync() {
    // Arrange
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var securityContext = new SecurityContext { UserId = "user", TenantId = "tenant" };

    var context1 = new CascadeContext {
      CorrelationId = correlationId,
      CausationId = causationId,
      SecurityContext = securityContext
    };

    var context2 = new CascadeContext {
      CorrelationId = correlationId,
      CausationId = causationId,
      SecurityContext = securityContext
    };

    // Assert
    await Assert.That(context1).IsEqualTo(context2);
    await Assert.That(context1.GetHashCode()).IsEqualTo(context2.GetHashCode());
  }

  [Test]
  public async Task RecordEquality_DifferentCorrelationId_AreNotEqualAsync() {
    // Arrange
    var causationId = MessageId.New();

    var context1 = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = causationId
    };

    var context2 = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = causationId
    };

    // Assert
    await Assert.That(context1).IsNotEqualTo(context2);
  }

  [Test]
  public async Task RecordEquality_DifferentCausationId_AreNotEqualAsync() {
    // Arrange
    var correlationId = CorrelationId.New();

    var context1 = new CascadeContext {
      CorrelationId = correlationId,
      CausationId = MessageId.New()
    };

    var context2 = new CascadeContext {
      CorrelationId = correlationId,
      CausationId = MessageId.New()
    };

    // Assert
    await Assert.That(context1).IsNotEqualTo(context2);
  }

  // ========================================
  // IMMUTABILITY TESTS
  // ========================================

  [Test]
  public async Task WithExpression_CreatesNewInstance_PreservesOriginalAsync() {
    // Arrange
    var original = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      SecurityContext = new SecurityContext { UserId = "user1", TenantId = "tenant1" }
    };
    var newSecurityContext = new SecurityContext { UserId = "user2", TenantId = "tenant2" };

    // Act
    var updated = original with { SecurityContext = newSecurityContext };

    // Assert - Updated has new security
    await Assert.That(updated.SecurityContext!.UserId).IsEqualTo("user2");
    // Original unchanged
    await Assert.That(original.SecurityContext!.UserId).IsEqualTo("user1");
    // Correlation and Causation preserved
    await Assert.That(updated.CorrelationId).IsEqualTo(original.CorrelationId);
    await Assert.That(updated.CausationId).IsEqualTo(original.CausationId);
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static ImmutableScopeContext _createTestScopeContext(string? userId, string? tenantId, bool shouldPropagate) {
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope {
        UserId = userId,
        TenantId = tenantId
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "test"
    };
    return new ImmutableScopeContext(extraction, shouldPropagate);
  }

  private static SimpleScopeContext _createNonImmutableScopeContext(string? userId, string? tenantId) {
    return new SimpleScopeContext(userId, tenantId);
  }

  /// <summary>
  /// Simple non-ImmutableScopeContext for testing the type check in GetSecurityFromAmbient.
  /// </summary>
  private sealed class SimpleScopeContext : IScopeContext {
    public SimpleScopeContext(string? userId, string? tenantId) {
      Scope = new PerspectiveScope {
        UserId = userId,
        TenantId = tenantId
      };
    }

    public PerspectiveScope Scope { get; }
    public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>();
    public IReadOnlySet<string> Roles => new HashSet<string>();
    public IReadOnlySet<Permission> Permissions => new HashSet<Permission>();
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => new HashSet<SecurityPrincipalId>();
    public string? ActualPrincipal => null;
    public string? EffectivePrincipal => null;
    public SecurityContextType ContextType => SecurityContextType.User;
    public string? Source => "test";
    public bool PropagateToOutgoingMessages => true;

    public bool HasPermission(Permission permission) => false;
    public bool HasAnyPermission(params Permission[] permissions) => false;
    public bool HasAllPermissions(params Permission[] permissions) => false;
    public bool HasRole(string role) => false;
    public bool HasAnyRole(params string[] roles) => false;
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => false;
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => false;
  }
}
