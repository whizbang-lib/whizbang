using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// TDD tests for ScopeContextAccessor.InitiatingContext property.
/// InitiatingContext stores the IMessageContext that initiated the current scope,
/// making IMessageContext the source of truth for security (UserId, TenantId).
/// </summary>
/// <tests>ScopeContextAccessor.InitiatingContext</tests>
[Category("Security")]
public class ScopeContextAccessorInitiatingContextTests {
  // === InitiatingContext Property Tests ===

  [Test]
  public async Task InitiatingContext_Initially_IsNullAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();

    // Act & Assert
    await Assert.That(accessor.InitiatingContext).IsNull();
  }

  [Test]
  public async Task InitiatingContext_AfterSet_ReturnsContextAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-123",
      TenantId = "tenant-456"
    };

    // Act
    accessor.InitiatingContext = messageContext;

    // Assert
    await Assert.That(accessor.InitiatingContext).IsNotNull();
    await Assert.That(accessor.InitiatingContext!.UserId).IsEqualTo("user-123");
    await Assert.That(accessor.InitiatingContext!.TenantId).IsEqualTo("tenant-456");
  }

  [Test]
  public async Task InitiatingContext_CanBeSetToNull_ReturnsNullAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-123",
      TenantId = "tenant-456"
    };
    accessor.InitiatingContext = messageContext;

    // Act
    accessor.InitiatingContext = null;

    // Assert
    await Assert.That(accessor.InitiatingContext).IsNull();
  }

  [Test]
  public async Task InitiatingContext_PreservesAllMessageContextProperties_WhenSetAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var timestamp = DateTimeOffset.UtcNow;

    var messageContext = new MessageContext {
      MessageId = messageId,
      CorrelationId = correlationId,
      CausationId = causationId,
      Timestamp = timestamp,
      UserId = "user-123",
      TenantId = "tenant-456"
    };

    // Act
    accessor.InitiatingContext = messageContext;

    // Assert
    await Assert.That(accessor.InitiatingContext!.MessageId).IsEqualTo(messageId);
    await Assert.That(accessor.InitiatingContext!.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(accessor.InitiatingContext!.CausationId).IsEqualTo(causationId);
    await Assert.That(accessor.InitiatingContext!.UserId).IsEqualTo("user-123");
    await Assert.That(accessor.InitiatingContext!.TenantId).IsEqualTo("tenant-456");
  }

  // === AsyncLocal Propagation Tests ===

  [Test]
  public async Task InitiatingContext_AcrossAsyncCalls_PropagatesAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-async",
      TenantId = "tenant-async"
    };
    accessor.InitiatingContext = messageContext;

    // Act - access from async method
    var userId = await _getUserIdFromInitiatingContextAsync(accessor);

    // Assert
    await Assert.That(userId).IsEqualTo("user-async");
  }

  [Test]
  public async Task InitiatingContext_InParallelTasks_HasIsolatedContextsAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var results = new List<string?>();
    var syncLock = new object();

    // Act - run parallel tasks that each set their own InitiatingContext
    var tasks = Enumerable.Range(1, 5).Select(async i => {
      var messageContext = new MessageContext {
        MessageId = MessageId.New(),
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        UserId = $"user-{i}",
        TenantId = $"tenant-{i}"
      };
      accessor.InitiatingContext = messageContext;

      // Simulate async work
      await Task.Delay(10);

      // Get the user ID from this context
      var userId = accessor.InitiatingContext?.UserId;
      lock (syncLock) {
        results.Add(userId);
      }
    }).ToArray();

    await Task.WhenAll(tasks);

    // Assert - each task should see its own user ID
    await Assert.That(results).Contains("user-1");
    await Assert.That(results).Contains("user-2");
    await Assert.That(results).Contains("user-3");
    await Assert.That(results).Contains("user-4");
    await Assert.That(results).Contains("user-5");
  }

  [Test]
  public async Task InitiatingContext_ChildTaskInheritsParentContext_ReturnsSameContextAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var parentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "parent-user",
      TenantId = "parent-tenant"
    };
    accessor.InitiatingContext = parentContext;

    // Act - child task should see parent's InitiatingContext
    var childUserId = await Task.Run(() => accessor.InitiatingContext?.UserId);

    // Assert
    await Assert.That(childUserId).IsEqualTo("parent-user");
  }

  [Test]
  public async Task InitiatingContext_ChildModification_DoesNotAffectParentAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var parentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "parent-user",
      TenantId = "parent-tenant"
    };
    accessor.InitiatingContext = parentContext;

    // Act - child task modifies InitiatingContext
    await Task.Run(() => {
      accessor.InitiatingContext = new MessageContext {
        MessageId = MessageId.New(),
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        UserId = "child-user",
        TenantId = "child-tenant"
      };
    });

    // Assert - parent should still see original InitiatingContext
    await Assert.That(accessor.InitiatingContext?.UserId).IsEqualTo("parent-user");
  }

  // === Static Accessor Tests ===

  [Test]
  public async Task CurrentInitiatingContext_Initially_IsNullAsync() {
    // Arrange - clear any existing context
    ScopeContextAccessor.CurrentInitiatingContext = null;

    // Act & Assert
    await Assert.That(ScopeContextAccessor.CurrentInitiatingContext).IsNull();
  }

  [Test]
  public async Task CurrentInitiatingContext_AfterSet_ReturnsContextAsync() {
    // Arrange
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "static-user",
      TenantId = "static-tenant"
    };

    // Act
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;

    // Assert
    await Assert.That(ScopeContextAccessor.CurrentInitiatingContext).IsNotNull();
    await Assert.That(ScopeContextAccessor.CurrentInitiatingContext!.UserId).IsEqualTo("static-user");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  [Test]
  public async Task CurrentInitiatingContext_MatchesInstanceAccessor_WhenSetViaInstanceAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "sync-user",
      TenantId = "sync-tenant"
    };

    // Act
    accessor.InitiatingContext = messageContext;

    // Assert - static accessor should see the same value
    await Assert.That(ScopeContextAccessor.CurrentInitiatingContext?.UserId).IsEqualTo("sync-user");

    // Cleanup
    accessor.InitiatingContext = null;
  }

  [Test]
  public async Task InitiatingContext_MatchesStaticAccessor_WhenSetViaStaticAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "reverse-sync-user",
      TenantId = "reverse-sync-tenant"
    };

    // Act
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;

    // Assert - instance accessor should see the same value
    await Assert.That(accessor.InitiatingContext?.UserId).IsEqualTo("reverse-sync-user");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  // === Debugging Support Tests ===

  [Test]
  public async Task InitiatingContext_ExposesFullMessageContext_ForDebuggingAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    var messageContext = new MessageContext {
      MessageId = messageId,
      CorrelationId = correlationId,
      CausationId = causationId,
      UserId = "debug-user",
      TenantId = "debug-tenant"
    };
    accessor.InitiatingContext = messageContext;

    // Act - access for debugging purposes
    var initiating = accessor.InitiatingContext;

    // Assert - all properties accessible for debugging
    await Assert.That(initiating).IsNotNull();
    await Assert.That(initiating!.MessageId).IsEqualTo(messageId);
    await Assert.That(initiating.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(initiating.CausationId).IsEqualTo(causationId);
    await Assert.That(initiating.UserId).IsEqualTo("debug-user");
    await Assert.That(initiating.TenantId).IsEqualTo("debug-tenant");
    // Timestamp also accessible
    await Assert.That(initiating.Timestamp).IsNotDefault();
  }

  // === CurrentContext Reading from InitiatingContext.ScopeContext Tests ===

  [Test]
  public async Task CurrentContext_WhenInitiatingContextHasScopeContext_ReadsScopeFromItAsync() {
    // Arrange - Create message context with scope
    var scopeContext = new TestScopeContext("scope-user", "scope-tenant");
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "scope-user",
      TenantId = "scope-tenant",
      ScopeContext = scopeContext
    };

    // Act - Set as initiating context
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;
    ScopeContextAccessor.CurrentContext = null; // No fallback

    try {
      // Assert - CurrentContext reads FROM InitiatingContext.ScopeContext
      await Assert.That(object.ReferenceEquals(ScopeContextAccessor.CurrentContext, scopeContext)).IsTrue();
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  [Test]
  public async Task CurrentContext_WhenInitiatingContextHasNullScopeContext_FallsBackToAmbientAsync() {
    // Arrange - InitiatingContext without ScopeContext
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-123",
      TenantId = "tenant-456",
      ScopeContext = null
    };
    var ambientScope = new TestScopeContext("ambient-user", "ambient-tenant");

    // Act
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;
    ScopeContextAccessor.CurrentContext = ambientScope;

    try {
      // Assert - Falls back to ambient when InitiatingContext has no ScopeContext
      await Assert.That(object.ReferenceEquals(ScopeContextAccessor.CurrentContext, ambientScope)).IsTrue();
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task CurrentContext_WhenNoInitiatingContext_UsesAmbientAsync() {
    // Arrange - No initiating context, only ambient
    var ambientScope = new TestScopeContext("ambient-user", "ambient-tenant");
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = ambientScope;

    try {
      // Assert - Uses ambient when no initiating context
      await Assert.That(object.ReferenceEquals(ScopeContextAccessor.CurrentContext, ambientScope)).IsTrue();
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task CurrentContext_InitiatingContextScopeContext_TakesPrecedenceOverAmbientAsync() {
    // Arrange - Both initiating and ambient have scope contexts
    var initiatingScope = new TestScopeContext("initiating-user", "initiating-tenant");
    var ambientScope = new TestScopeContext("ambient-user", "ambient-tenant");

    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "initiating-user",
      TenantId = "initiating-tenant",
      ScopeContext = initiatingScope
    };

    // Act
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;
    ScopeContextAccessor.CurrentContext = ambientScope;

    try {
      // Assert - InitiatingContext.ScopeContext takes precedence
      await Assert.That(object.ReferenceEquals(ScopeContextAccessor.CurrentContext, initiatingScope)).IsTrue();
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task CurrentUserId_ReadsFromInitiatingContext_NotFromScopeContextAsync() {
    // Arrange - InitiatingContext has UserId, ScopeContext has different UserId
    var scopeContext = new TestScopeContext("scope-user", "scope-tenant");
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "initiating-user",
      TenantId = "initiating-tenant",
      ScopeContext = scopeContext
    };

    // Act
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;

    try {
      // Assert - CurrentUserId reads from InitiatingContext, not ScopeContext
      await Assert.That(ScopeContextAccessor.CurrentUserId).IsEqualTo("initiating-user");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  [Test]
  public async Task CurrentTenantId_ReadsFromInitiatingContext_NotFromScopeContextAsync() {
    // Arrange - InitiatingContext has TenantId, ScopeContext has different TenantId
    var scopeContext = new TestScopeContext("scope-user", "scope-tenant");
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "initiating-user",
      TenantId = "initiating-tenant",
      ScopeContext = scopeContext
    };

    // Act
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;

    try {
      // Assert - CurrentTenantId reads from InitiatingContext, not ScopeContext
      await Assert.That(ScopeContextAccessor.CurrentTenantId).IsEqualTo("initiating-tenant");
    } finally {
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  [Test]
  public async Task InstanceAccessor_Current_ReadsFromInitiatingContextScopeContextAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var scopeContext = new TestScopeContext("instance-user", "instance-tenant");
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "instance-user",
      TenantId = "instance-tenant",
      ScopeContext = scopeContext
    };

    // Act
    accessor.InitiatingContext = messageContext;

    try {
      // Assert - Instance accessor.Current also reads from InitiatingContext.ScopeContext
      await Assert.That(object.ReferenceEquals(accessor.Current, scopeContext)).IsTrue();
    } finally {
      accessor.InitiatingContext = null;
    }
  }

  // === Helper Methods ===

  private static async Task<string?> _getUserIdFromInitiatingContextAsync(ScopeContextAccessor accessor) {
    await Task.Delay(1); // Simulate async work
    return accessor.InitiatingContext?.UserId;
  }

  /// <summary>
  /// Test implementation of IScopeContext.
  /// </summary>
  private sealed class TestScopeContext : IScopeContext {
    public TestScopeContext(string? userId, string? tenantId) {
      Scope = new Core.Lenses.PerspectiveScope {
        UserId = userId,
        TenantId = tenantId
      };
    }

    public Core.Lenses.PerspectiveScope Scope { get; }
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
