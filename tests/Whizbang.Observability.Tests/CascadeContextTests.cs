using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
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
  // RESOLVE CASCADE IDENTITY TESTS
  // ========================================
  // ResolveCascadeIdentity is the tracing-axis mirror of GetSecurityFromAmbient: it supplies the
  // correlation/causation a "publish/store" hop builder must stamp so identity flows wherever scope flows.

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveCascadeIdentity_WithAmbientInitiatingContext_UsesItsCorrelationAndCausationAsync() {
    // Arrange — an inbound message is being handled (its context is the ambient initiating context).
    var expectedCorrelation = CorrelationId.New();
    var initiatingMessageId = MessageId.New();
    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      MessageId = initiatingMessageId,
      CorrelationId = expectedCorrelation,
      CausationId = MessageId.New()
    };

    try {
      // Act
      var (correlation, causation) = CascadeContext.ResolveCascadeIdentity(sourceEnvelope: null);

      // Assert — the emitted hop inherits the inbound correlation, and causation is the inbound message id.
      await Assert.That(correlation).IsEqualTo(expectedCorrelation);
      await Assert.That(causation).IsEqualTo(initiatingMessageId);
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveCascadeIdentity_NoAmbient_FallsBackToSourceEnvelopeAsync() {
    // Arrange — no ambient initiating context, but a cascaded event carries a source (parent) envelope.
    ScopeContextAccessor.CurrentInitiatingContext = null;
    var sourceCorrelation = CorrelationId.New();
    var sourceCausation = MessageId.New();
    var sourceEnvelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "parent",
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = sourceCorrelation,
          CausationId = sourceCausation
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var (correlation, causation) = CascadeContext.ResolveCascadeIdentity(sourceEnvelope);

    // Assert — the parent's correlation and causation flow onto the emitted hop.
    await Assert.That(correlation).IsEqualTo(sourceCorrelation);
    await Assert.That(causation).IsEqualTo(sourceCausation);
  }

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveCascadeIdentity_NoAmbientNoSource_MintsTraceAlignedRootAsync() {
    // Arrange — a top-level publish with neither ambient context nor a source envelope.
    ScopeContextAccessor.CurrentInitiatingContext = null;

    // Act
    var (correlation, causation) = CascadeContext.ResolveCascadeIdentity(sourceEnvelope: null);

    // Assert — a fresh, non-default correlation is minted (never null on the hop); no parent => null causation.
    await Assert.That(correlation.Value).IsNotEqualTo(Guid.Empty)
      .Because("A published/stored hop must always carry a correlation, even at a root with no inbound context.");
    await Assert.That(causation).IsNull()
      .Because("A root emission has no parent message, so causation is null.");
  }

  // ========================================
  // RESOLVE HOP-FIRST TESTS (the shared publish-side precedence used by every hop builder)
  // ========================================
  // These directly guard the hop-first PRECEDENCE — "source hop wins over a DIFFERENT present ambient" — that
  // survives detached/worker/collective boundaries. The boundary integration tests clear ambient, so ONLY these
  // catch a regression from hop-first back to ambient-first (which would silently reappear a bug seen in production).

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveHopFirstIdentity_HopPresent_WinsOverDifferentAmbientAsync() {
    // Arrange — ambient carries correlation A; the source hop carries a DIFFERENT correlation B + causation C.
    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),   // ambient A — must be ignored
      CausationId = MessageId.New()
    };
    var hopCorrelation = CorrelationId.New();  // B
    var hopCausation = MessageId.New();        // C
    var sourceEnvelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "parent",
      Hops = [
        new MessageHop {
          Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = hopCorrelation, CausationId = hopCausation
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    try {
      // Act
      var (correlation, causation) = CascadeContext.ResolveHopFirstIdentity(sourceEnvelope);

      // Assert — the source HOP wins over the different ambient (hop-first).
      await Assert.That(correlation).IsEqualTo(hopCorrelation)
        .Because("Hop-first: the source hop's correlation must win over a different present ambient (survives detached/worker boundaries).");
      await Assert.That(causation).IsEqualTo(hopCausation)
        .Because("Hop-first: causation comes from the source hop, not ambient.");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveHopFirstIdentity_NoHop_FallsBackToAmbientAsync() {
    // Arrange — no source hop; ambient carries correlation A + message id M.
    var ambientCorrelation = CorrelationId.New();
    var ambientMessageId = MessageId.New();
    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      MessageId = ambientMessageId,
      CorrelationId = ambientCorrelation,
      CausationId = MessageId.New()
    };

    try {
      var (correlation, causation) = CascadeContext.ResolveHopFirstIdentity(sourceEnvelope: null);
      await Assert.That(correlation).IsEqualTo(ambientCorrelation)
        .Because("With no source hop, hop-first falls back to the ambient initiating context.");
      await Assert.That(causation).IsEqualTo(ambientMessageId)
        .Because("Ambient-fallback causation is the initiating message id.");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveHopFirstScope_HopPresent_WinsOverDifferentAmbientAsync() {
    // Arrange — ambient scope is tenant-A/user-A; the source hop carries a DIFFERENT tenant-B/user-B scope.
    ScopeContextAccessor.CurrentContext = _createTestScopeContext("user-A", "tenant-A", shouldPropagate: true);
    var sourceEnvelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "parent",
      Hops = [
        new MessageHop {
          Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-B", UserId = "user-B" })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    try {
      // Act
      var resolved = CascadeContext.ResolveHopFirstScope(sourceEnvelope)!.ApplyTo(null);

      // Assert — the source hop's tenant/user win over the different present ambient.
      await Assert.That(resolved.Scope.TenantId).IsEqualTo("tenant-B")
        .Because("Hop-first: the source hop's tenant scope must win over a different present ambient.");
      await Assert.That(resolved.Scope.UserId).IsEqualTo("user-B")
        .Because("Hop-first: the source hop's user scope wins over ambient too.");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveHopFirstScope_NoHop_FallsBackToAmbientAsync() {
    // Arrange — no source hop; ambient (propagating) scope is tenant-A/user-A.
    ScopeContextAccessor.CurrentContext = _createTestScopeContext("user-A", "tenant-A", shouldPropagate: true);

    try {
      var resolved = CascadeContext.ResolveHopFirstScope(sourceEnvelope: null)!.ApplyTo(null);
      await Assert.That(resolved.Scope.TenantId).IsEqualTo("tenant-A")
        .Because("With no source hop, hop-first scope falls back to the ambient propagating scope.");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  [NotInParallel("AmbientInitiatingContext")]
  public async Task ResolveHopFirstScope_NoHop_NonPropagatingAmbient_ReturnsNullAsync() {
    // Arrange — ambient scope exists but is marked ShouldPropagate=false (the PropagateToOutgoingMessages=false
    // opt-out). It must NOT flow onto an outgoing/child hop. Guards the unified ambient-scope rule after the
    // _captureAmbientSourceEnvelope fallback (which bypassed this) was removed.
    ScopeContextAccessor.CurrentContext = _createTestScopeContext("user-A", "tenant-A", shouldPropagate: false);

    try {
      var result = CascadeContext.ResolveHopFirstScope(sourceEnvelope: null);
      await Assert.That(result).IsNull()
        .Because("Scope marked ShouldPropagate=false must never propagate onto an outgoing/child hop — the contract GetSecurityFromAmbient enforces, honored identically by every hop builder AND the ambient-capture path.");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

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
    const string testUserId = "test-user@example.com";
    const string testTenantId = "test-tenant-123";

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
    const string testUserId = "propagate-user";
    const string testTenantId = "propagate-tenant";

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
