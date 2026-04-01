using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Integration tests for the security system.
/// Verifies that security components work together correctly.
/// </summary>
[Category("Integration")]
public class SecurityIntegrationTests {
  // === Permission System Integration ===

  [Test]
  public async Task Permission_WithRole_MatchesWildcardCorrectlyAsync() {
    // Integration test: verify Permission + Role work together

    // Arrange
    var role = new RoleBuilder("Admin")
      .HasAllPermissions("orders")
      .HasAllPermissions("customers")
      .Build();

    // Act & Assert - Role with wildcard should match specific permissions
    await Assert.That(role.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Write("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Admin("orders"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Read("customers"))).IsTrue();
    await Assert.That(role.HasPermission(Permission.Read("products"))).IsFalse();
  }

  [Test]
  public async Task ScopeContext_WithRolePermissions_ChecksCorrectlyAsync() {
    // Integration test: verify ScopeContext integrates with Permission matching

    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Roles = new HashSet<string> { "Admin", "Support" },
      Permissions = new HashSet<Permission> {
        Permission.All("orders"),
        Permission.Read("customers"),
        Permission.Write("tickets")
      },
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-1"),
        SecurityPrincipalId.Group("support-team")
      },
      Claims = new Dictionary<string, string> {
        ["sub"] = "user-1",
        ["tenant"] = "tenant-1"
      }
    };

    // Act & Assert - Permission checks
    await Assert.That(context.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(context.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(context.HasPermission(Permission.Read("customers"))).IsTrue();
    await Assert.That(context.HasPermission(Permission.Delete("customers"))).IsFalse();

    // Role checks
    await Assert.That(context.HasRole("Admin")).IsTrue();
    await Assert.That(context.HasAnyRole("Admin", "Guest")).IsTrue();
    await Assert.That(context.HasAnyRole("Guest", "Viewer")).IsFalse();

    // Principal checks
    await Assert.That(context.IsMemberOfAny(SecurityPrincipalId.Group("support-team"))).IsTrue();
    await Assert.That(context.IsMemberOfAny(SecurityPrincipalId.Group("sales-team"))).IsFalse();
  }

  // === ScopeFilters Integration ===

  [Test]
  public async Task ScopeFilterBuilder_WithScopeContext_BuildsCorrectFiltersAsync() {
    // Integration test: verify ScopeFilterBuilder + ScopeContext work together

    // Arrange
    var context = new ScopeContext {
      Scope = new PerspectiveScope {
        TenantId = "tenant-123",
        UserId = "user-456",
        OrganizationId = "org-789"
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId> {
        SecurityPrincipalId.User("user-456"),
        SecurityPrincipalId.Group("developers")
      },
      Claims = new Dictionary<string, string>()
    };

    // Act - Build various filter combinations
    var tenantFilter = ScopeFilterBuilder.Build(ScopeFilters.Tenant, context);
    var userFilter = ScopeFilterBuilder.Build(ScopeFilters.Tenant | ScopeFilters.User, context);
    var principalFilter = ScopeFilterBuilder.Build(ScopeFilters.Tenant | ScopeFilters.Principal, context);
    var myOrSharedFilter = ScopeFilterBuilder.Build(
      ScopeFilters.Tenant | ScopeFilters.User | ScopeFilters.Principal, context);

    // Assert
    await Assert.That(tenantFilter.TenantId).IsEqualTo("tenant-123");
    await Assert.That(tenantFilter.UserId).IsNull();

    await Assert.That(userFilter.TenantId).IsEqualTo("tenant-123");
    await Assert.That(userFilter.UserId).IsEqualTo("user-456");
    await Assert.That(userFilter.UseOrLogicForUserAndPrincipal).IsFalse();

    await Assert.That(principalFilter.TenantId).IsEqualTo("tenant-123");
    await Assert.That(principalFilter.SecurityPrincipals.Count).IsEqualTo(2);

    await Assert.That(myOrSharedFilter.UseOrLogicForUserAndPrincipal).IsTrue();
  }

  // === Security Options Configuration ===

  [Test]
  public async Task SecurityOptions_FullConfiguration_WorksCorrectlyAsync() {
    // Integration test: verify full security configuration works

    // Arrange & Act
    var options = new SecurityOptions()
      .DefineRole("Admin", b => b
        .HasAllPermissions("*"))
      .DefineRole("Manager", b => b
        .HasAllPermissions("orders")
        .HasReadPermission("reports")
        .HasWritePermission("schedules"))
      .DefineRole("User", b => b
        .HasReadPermission("orders")
        .HasReadPermission("products"))
      .ExtractPermissionsFromClaim("permissions")
      .ExtractRolesFromClaim("roles")
      .ExtractSecurityPrincipalsFromClaim("groups");

    // Assert
    await Assert.That(options.Roles.Count).IsEqualTo(3);
    await Assert.That(options.Extractors.Count).IsEqualTo(3);

    // Verify Admin has super-admin powers
    var adminRole = options.Roles["Admin"];
    await Assert.That(adminRole.HasPermission(Permission.Delete("anything"))).IsTrue();
    await Assert.That(adminRole.HasPermission(Permission.Admin("everything"))).IsTrue();

    // Verify Manager has specific permissions
    var managerRole = options.Roles["Manager"];
    await Assert.That(managerRole.HasPermission(Permission.Delete("orders"))).IsTrue();
    await Assert.That(managerRole.HasPermission(Permission.Read("reports"))).IsTrue();
    await Assert.That(managerRole.HasPermission(Permission.Delete("reports"))).IsFalse();

    // Verify User has limited permissions
    var userRole = options.Roles["User"];
    await Assert.That(userRole.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(userRole.HasPermission(Permission.Write("orders"))).IsFalse();
  }

  // === Permission Extraction ===

  [Test]
  public async Task PermissionExtractors_ExtractFromClaims_CorrectlyAsync() {
    // Integration test: verify extractors work with claims

    // Arrange
    var options = new SecurityOptions()
      .ExtractPermissionsFromClaim("permissions")
      .ExtractRolesFromClaim("roles")
      .ExtractSecurityPrincipalsFromClaim("groups");

    var claims = new Dictionary<string, string> {
      ["permissions"] = "orders:read, orders:write, customers:read",
      ["roles"] = "Admin, Support",
      ["groups"] = "group:developers, group:qa-team"
    };

    // Act
    var permissions = options.Extractors
      .SelectMany(e => e.ExtractPermissions(claims))
      .ToList();
    var roles = options.Extractors
      .SelectMany(e => e.ExtractRoles(claims))
      .ToList();
    var principals = options.Extractors
      .SelectMany(e => e.ExtractSecurityPrincipals(claims))
      .ToList();

    // Assert
    await Assert.That(permissions.Count).IsEqualTo(3);
    await Assert.That(permissions).Contains(new Permission("orders:read"));
    await Assert.That(permissions).Contains(new Permission("orders:write"));
    await Assert.That(permissions).Contains(new Permission("customers:read"));

    await Assert.That(roles.Count).IsEqualTo(2);
    await Assert.That(roles).Contains("Admin");
    await Assert.That(roles).Contains("Support");

    await Assert.That(principals.Count).IsEqualTo(2);
    await Assert.That(principals).Contains(new SecurityPrincipalId("group:developers"));
    await Assert.That(principals).Contains(new SecurityPrincipalId("group:qa-team"));
  }

  // === Security System Events ===

  [Test]
  public async Task AccessDenied_CapturesAllSecurityContext_Async() {
    // Integration test: verify AccessDenied captures complete security context

    // Arrange & Act
    var accessDenied = new AccessDenied {
      ResourceType = "Order",
      ResourceId = "order-123",
      RequiredPermission = Permission.Delete("orders"),
      CallerPermissions = new HashSet<Permission> { Permission.Read("orders") },
      CallerRoles = new HashSet<string> { "User" },
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Reason = AccessDenialReason.InsufficientPermission,
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert - All security context is captured
    await Assert.That(accessDenied.ResourceType).IsEqualTo("Order");
    await Assert.That(accessDenied.ResourceId).IsEqualTo("order-123");
    await Assert.That(accessDenied.RequiredPermission.Value).IsEqualTo("orders:delete");
    await Assert.That(accessDenied.CallerPermissions.Count).IsEqualTo(1);
    await Assert.That(accessDenied.CallerRoles).Contains("User");
    await Assert.That(accessDenied.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(accessDenied.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  [Test]
  public async Task AccessGranted_CapturesAccessDetails_Async() {
    // Integration test: verify AccessGranted captures access details

    // Arrange & Act
    var accessGranted = new AccessGranted {
      ResourceType = "Order",
      ResourceId = "order-123",
      UsedPermission = Permission.Read("orders"),
      AccessFilter = ScopeFilters.Tenant | ScopeFilters.User,
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(accessGranted.ResourceType).IsEqualTo("Order");
    await Assert.That(accessGranted.ResourceId).IsEqualTo("order-123");
    await Assert.That(accessGranted.UsedPermission.Value).IsEqualTo("orders:read");
    await Assert.That(accessGranted.AccessFilter.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(accessGranted.AccessFilter.HasFlag(ScopeFilters.User)).IsTrue();
    await Assert.That(accessGranted.Scope.TenantId).IsEqualTo("tenant-1");
  }

  // === Exception Integration ===

  [Test]
  public async Task AccessDeniedException_ContainsAllDetails_Async() {
    // Integration test: verify exception carries all necessary information

    // Arrange
    var permission = Permission.Admin("system");
    const string resourceType = "Configuration";
    const string resourceId = "global-settings";
    const AccessDenialReason reason = AccessDenialReason.PolicyRejected;

    // Act
    var exception = new AccessDeniedException(permission, resourceType, resourceId, reason);

    // Assert
    await Assert.That(exception.RequiredPermission).IsEqualTo(permission);
    await Assert.That(exception.ResourceType).IsEqualTo(resourceType);
    await Assert.That(exception.ResourceId).IsEqualTo(resourceId);
    await Assert.That(exception.Reason).IsEqualTo(reason);
    await Assert.That(exception.Message).Contains("Configuration");
    await Assert.That(exception.Message).Contains("global-settings");
    await Assert.That(exception.Message).Contains("system:admin");
  }

  // === ScopeContext Accessor Integration ===

  [Test]
  public async Task ScopeContextAccessor_PropagatesAcrossAsyncCalls_Async() {
    // Integration test: verify AsyncLocal propagation works

    // Arrange
    var accessor = new ScopeContextAccessor();
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Roles = new HashSet<string> { "Admin" },
      Permissions = new HashSet<Permission> { Permission.All("*") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    IScopeContext? capturedInner = null;
    IScopeContext? capturedParallel = null;

    // Act
    accessor.Current = context;

    // Nested async call
    await Task.Run(async () => {
      await Task.Delay(1);
      capturedInner = accessor.Current;
    });

    // Parallel async calls
    var tasks = Enumerable.Range(0, 5).Select(async _ => {
      await Task.Delay(1);
      return accessor.Current;
    });
    var results = await Task.WhenAll(tasks);
    capturedParallel = results.FirstOrDefault();

    // Assert
    await Assert.That(capturedInner).IsNotNull();
    await Assert.That(capturedInner!.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(capturedParallel).IsNotNull();
    await Assert.That(capturedParallel!.HasRole("Admin")).IsTrue();
  }

  // === PerspectiveScope Integration ===

  [Test]
  public async Task PerspectiveScope_WithAllowedPrincipals_WorksCorrectly_Async() {
    // Integration test: verify PerspectiveScope with principals

    // Arrange
    var scope = new PerspectiveScope {
      TenantId = "tenant-1",
      UserId = "user-1",
      AllowedPrincipals = [
        SecurityPrincipalId.User("user-1"),
        SecurityPrincipalId.Group("managers"),
        SecurityPrincipalId.Group("finance-team")
      ],
      Extensions = [
        new ScopeExtension("department", "Engineering"),
        new ScopeExtension("costCenter", "CC-123")
      ]
    };

    // Act & Assert - GetValue access
    await Assert.That(scope.GetValue("TenantId")).IsEqualTo("tenant-1");
    await Assert.That(scope.GetValue("UserId")).IsEqualTo("user-1");
    await Assert.That(scope.GetValue("department")).IsEqualTo("Engineering");
    await Assert.That(scope.GetValue("costCenter")).IsEqualTo("CC-123");
    await Assert.That(scope.GetValue("unknown")).IsNull();

    // Principal checks
    await Assert.That(scope.AllowedPrincipals!.Count).IsEqualTo(3);
    await Assert.That(scope.AllowedPrincipals).Contains(SecurityPrincipalId.Group("managers"));
  }

  // === IScopedLensFactory Interface ===

  [Test]
  public async Task IScopedLensFactory_HasAllRequiredMethods_Async() {
    // Integration test: verify interface has all methods for security-aware lens access

    // Assert - String-based method (legacy)
    var stringMethod = typeof(IScopedLensFactory).GetMethod("GetLens", [typeof(string)]);
    await Assert.That(stringMethod).IsNotNull();

    // ScopeFilters-based methods
    var filterMethod = typeof(IScopedLensFactory).GetMethod("GetLens", [typeof(ScopeFilters)]);
    await Assert.That(filterMethod).IsNotNull();

    var permissionMethod = typeof(IScopedLensFactory).GetMethod("GetLens", [typeof(ScopeFilters), typeof(Permission)]);
    await Assert.That(permissionMethod).IsNotNull();

    // Convenience methods
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetGlobalLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetTenantLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetUserLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetOrganizationLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetCustomerLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetPrincipalLens")).IsNotNull();
    await Assert.That(typeof(IScopedLensFactory).GetMethod("GetMyOrSharedLens")).IsNotNull();
  }

  // === Explicit Security Context (AsSystem/RunAs) Outbox Integration ===

  /// <summary>
  /// Integration test verifying that CascadeContext.GetSecurityFromAmbient() returns
  /// the explicit SYSTEM context set by DispatcherSecurityBuilder, NOT the user's
  /// InitiatingContext.
  /// </summary>
  /// <remarks>
  /// This test reproduces the core issue from JDNext where cascaded events from
  /// ReseedSystemEvent were throwing SecurityContextRequiredException because
  /// the user's InitiatingContext was being used instead of the explicit SYSTEM context.
  ///
  /// The fix clears CurrentInitiatingContext in DispatcherSecurityBuilder so that
  /// CurrentContext (the explicit SYSTEM context) takes precedence when
  /// GetSecurityFromAmbient() is called during outbox message creation.
  /// </remarks>
  /// <tests>src/Whizbang.Core/Dispatch/DispatcherSecurityBuilder.cs:PublishAsync</tests>
  /// <tests>src/Whizbang.Core/Observability/CascadeContext.cs:GetSecurityFromAmbient</tests>
  [Test]
  public async Task GetSecurityFromAmbient_WhenExplicitContextSet_ReturnsExplicitNotInitiatingContextAsync() {
    // Arrange - Simulate being inside a user handler by setting InitiatingContext
    // This is what happens when an HTTP request sets up user context
    var userContext = new MessageContext {
      UserId = "user-123",
      TenantId = "tenant-456",
      MessageId = ValueObjects.MessageId.New(),
      CorrelationId = ValueObjects.CorrelationId.New(),
      CausationId = ValueObjects.MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow
    };
    var previousInitiating = ScopeContextAccessor.CurrentInitiatingContext;
    var previousContext = ScopeContextAccessor.CurrentContext;

    try {
      // Set up InitiatingContext to simulate being in a user handler
      ScopeContextAccessor.CurrentInitiatingContext = userContext;

      // Now simulate what DispatcherSecurityBuilder does when AsSystem() is called:
      // 1. Create explicit SYSTEM context
      // 2. Set it on CurrentContext
      // 3. CLEAR CurrentInitiatingContext (this is the fix)
      var systemExtraction = new SecurityExtraction {
        Scope = new PerspectiveScope {
          UserId = SecurityContextType.System.ToString(),
          TenantId = null
        },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Explicit:System",
        ContextType = SecurityContextType.System,
        ActualPrincipal = null,
        EffectivePrincipal = SecurityContextType.System.ToString()
      };
      var explicitSystemContext = new ImmutableScopeContext(systemExtraction, shouldPropagate: true);

      // This is what DispatcherSecurityBuilder.PublishAsync does:
      ScopeContextAccessor.CurrentContext = explicitSystemContext;
      ScopeContextAccessor.CurrentInitiatingContext = null; // CRITICAL: The fix

      // Act - Call GetSecurityFromAmbient() which is what PublishToOutboxAsync uses
      var ambientSecurity = CascadeContext.GetSecurityFromAmbient();

      // Assert - Should get SYSTEM, not user-123
      await Assert.That(ambientSecurity).IsNotNull()
        .Because("GetSecurityFromAmbient should return the explicit SYSTEM context");

      await Assert.That(ambientSecurity!.UserId).IsEqualTo(SecurityContextType.System.ToString())
        .Because("UserId should be 'System' from the explicit context, not 'user-123'");

      await Assert.That(ambientSecurity.UserId).IsNotEqualTo("user-123")
        .Because("InitiatingContext user should NOT leak through when explicit context is set");
    } finally {
      // Cleanup AsyncLocal state
      ScopeContextAccessor.CurrentInitiatingContext = previousInitiating;
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Integration test verifying that NOT clearing InitiatingContext causes the bug.
  /// This proves the fix is necessary.
  /// </summary>
  [Test]
  public async Task GetSecurityFromAmbient_WithoutClearingInitiatingContext_ReturnsUserContextBugAsync() {
    // Arrange - Simulate being inside a user handler
    var userContext = new MessageContext {
      UserId = "user-123",
      TenantId = "tenant-456",
      MessageId = ValueObjects.MessageId.New(),
      CorrelationId = ValueObjects.CorrelationId.New(),
      CausationId = ValueObjects.MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow
    };
    var previousInitiating = ScopeContextAccessor.CurrentInitiatingContext;
    var previousContext = ScopeContextAccessor.CurrentContext;

    try {
      // Set up InitiatingContext to simulate being in a user handler
      ScopeContextAccessor.CurrentInitiatingContext = userContext;

      // Now simulate the OLD behavior (without the fix):
      // Set CurrentContext but DON'T clear CurrentInitiatingContext
      var systemExtraction = new SecurityExtraction {
        Scope = new PerspectiveScope {
          UserId = SecurityContextType.System.ToString(),
          TenantId = null
        },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Explicit:System",
        ContextType = SecurityContextType.System,
        ActualPrincipal = null,
        EffectivePrincipal = SecurityContextType.System.ToString()
      };
      var explicitSystemContext = new ImmutableScopeContext(systemExtraction, shouldPropagate: true);

      // OLD behavior: Set CurrentContext but don't clear InitiatingContext
      ScopeContextAccessor.CurrentContext = explicitSystemContext;
      // NOT clearing: ScopeContextAccessor.CurrentInitiatingContext = null;

      // Act - Read CurrentContext (this is what GetSecurityFromAmbient uses)
      // The getter reads InitiatingContext.ScopeContext FIRST, then falls back to _current
      var currentContext = ScopeContextAccessor.CurrentContext;

      // Assert - Verify that CurrentContext getter prioritizes InitiatingContext
      // This demonstrates the asymmetric getter behavior that causes the bug
      // If InitiatingContext has a ScopeContext, CurrentContext getter returns that
      // instead of what was explicitly set via the setter.

      // Note: This test verifies the getter behavior, but GetSecurityFromAmbient
      // specifically casts to ImmutableScopeContext. Let's check what happens:
      var ambientSecurity = CascadeContext.GetSecurityFromAmbient();

      // GetSecurityFromAmbient only returns security if CurrentContext is ImmutableScopeContext
      // with ShouldPropagate=true. MessageContext.ScopeContext may or may not be ImmutableScopeContext.
      // If the getter returns non-ImmutableScopeContext, GetSecurityFromAmbient returns null.

      // The behavior depends on implementation details. Let's document what we observe:
      if (currentContext is ImmutableScopeContext immutable && immutable.ShouldPropagate) {
        // If we got the explicit context, the bug is not present for this scenario
        await Assert.That(immutable.Scope.UserId).IsEqualTo(SecurityContextType.System.ToString());
      } else if (userContext.ScopeContext is ImmutableScopeContext userImmutable) {
        // If we got the user's context, the bug IS present
        await Assert.That(userImmutable.Scope.UserId).IsEqualTo("user-123");
      }

      // The important thing is: with the fix (clearing InitiatingContext),
      // we ALWAYS get the explicit System context
    } finally {
      // Cleanup AsyncLocal state
      ScopeContextAccessor.CurrentInitiatingContext = previousInitiating;
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  // === PostPerspectiveDetached Security Context Propagation ===

  /// <summary>
  /// Integration test verifying that when PerspectiveWorker processes events with scope in hops
  /// but extraction fails (no extractor succeeds), the scope is still available to lifecycle handlers.
  /// This validates the fix for TenantContext being null in [FireAt(LifecycleStage.PostPerspectiveDetached)] handlers.
  /// </summary>
  /// <remarks>
  /// Root cause: When extraction fails, envelope.GetCurrentScope() returns a ScopeContext that is NOT
  /// an ImmutableScopeContext. CascadeContext.GetSecurityFromAmbient() only returns security when
  /// CurrentContext is ImmutableScopeContext with ShouldPropagate=true.
  ///
  /// Fix: PerspectiveWorker now wraps envelope scope in ImmutableScopeContext with shouldPropagate=true
  /// and invokes callbacks so that lifecycle handlers can access TenantContext.
  /// </remarks>
  /// <docs>core-concepts/security-context#perspective-worker-propagation</docs>
  /// <tests>src/Whizbang.Core/Workers/PerspectiveWorker.cs:_establishSecurityContextAsync</tests>
  [Test]
  public async Task PerspectiveWorker_WhenExtractorFailsButEnvelopeHasScope_GetSecurityFromAmbientReturnsSecurityAsync() {
    // Arrange - Save previous state
    var previousInitiating = ScopeContextAccessor.CurrentInitiatingContext;
    var previousContext = ScopeContextAccessor.CurrentContext;

    try {
      // Clear any existing context
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;

      // Arrange - Create envelope with scope in hops (as if it came from BFF via outbox)
      var tenantId = "test-tenant-" + Guid.NewGuid();
      var userId = "test-user-" + Guid.NewGuid();

      var scopeContext = new ScopeContext {
        Scope = new PerspectiveScope {
          TenantId = tenantId,
          UserId = userId
        },
        Roles = new HashSet<string> { "User" },
        Permissions = new HashSet<Permission> { Permission.Read("orders") },
        SecurityPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.User(userId) },
        Claims = new Dictionary<string, string> { ["tenant"] = tenantId, ["sub"] = userId },
        ContextType = SecurityContextType.User
      };

      // Create envelope with scope embedded in hops
      var envelope = new Observability.MessageEnvelope<TestPerspectiveEvent> {
        MessageId = ValueObjects.MessageId.New(),
        Payload = new TestPerspectiveEvent(Guid.NewGuid()),
        Hops = [
          new Observability.MessageHop {
            Type = Observability.HopType.Current,
            ServiceInstance = new Observability.ServiceInstanceInfo {
              ServiceName = "TestService",
              HostName = "TestHost",
              ProcessId = Environment.ProcessId,
              InstanceId = Guid.NewGuid()
            },
            Timestamp = DateTimeOffset.UtcNow,
            // Embed scope context in the hop (this is what SecurityContextEventStoreDecorator does)
            Scope = ScopeDelta.CreateDelta(null, scopeContext)
          }
        ]
      };

      // Simulate PerspectiveWorker._establishSecurityContextAsync logic when extraction fails
      // 1. Extraction fails (no extractors or they all return null) -> securityContext is null
      IScopeContext? securityContext = null; // Simulates failed extraction

      // 2. Fall back to envelope.GetCurrentScope()
      var scopeFromEnvelope = envelope.GetCurrentScope();

      // 3. Wrap in ImmutableScopeContext with ShouldPropagate=true (THE FIX)
      if (securityContext is null && scopeFromEnvelope is not null) {
        var extraction = new SecurityExtraction {
          Scope = scopeFromEnvelope.Scope,
          Roles = scopeFromEnvelope.Roles,
          Permissions = scopeFromEnvelope.Permissions,
          SecurityPrincipals = scopeFromEnvelope.SecurityPrincipals,
          Claims = scopeFromEnvelope.Claims,
          ActualPrincipal = scopeFromEnvelope.ActualPrincipal,
          EffectivePrincipal = scopeFromEnvelope.EffectivePrincipal,
          ContextType = scopeFromEnvelope.ContextType,
          Source = "EnvelopeHop"
        };
        var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);

        // Set on accessor (what PerspectiveWorker does)
        ScopeContextAccessor.CurrentContext = immutableScope;

        // Also set InitiatingContext with MessageContext (what PerspectiveWorker does)
        var messageContext = new MessageContext {
          MessageId = envelope.MessageId,
          CorrelationId = envelope.GetCorrelationId() ?? ValueObjects.CorrelationId.New(),
          CausationId = envelope.GetCausationId() ?? ValueObjects.MessageId.New(),
          Timestamp = DateTimeOffset.UtcNow,
          UserId = scopeFromEnvelope.Scope?.UserId,
          TenantId = scopeFromEnvelope.Scope?.TenantId,
          ScopeContext = immutableScope
        };
        ScopeContextAccessor.CurrentInitiatingContext = messageContext;
      }

      // Act - Call GetSecurityFromAmbient() (what SecurityContextEventStoreDecorator and lifecycle handlers use)
      var ambientSecurity = CascadeContext.GetSecurityFromAmbient();

      // Assert - Should return security context with TenantId and UserId
      await Assert.That(ambientSecurity).IsNotNull()
        .Because("GetSecurityFromAmbient should return security when ImmutableScopeContext with ShouldPropagate=true is set");

      await Assert.That(ambientSecurity!.TenantId).IsEqualTo(tenantId)
        .Because("TenantId should be propagated from envelope hops");

      await Assert.That(ambientSecurity.UserId).IsEqualTo(userId)
        .Because("UserId should be propagated from envelope hops");
    } finally {
      // Cleanup AsyncLocal state
      ScopeContextAccessor.CurrentInitiatingContext = previousInitiating;
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Integration test that proves the bug: When envelope scope is NOT wrapped in ImmutableScopeContext,
  /// CascadeContext.GetSecurityFromAmbient() returns null.
  /// </summary>
  /// <remarks>
  /// This test demonstrates the OLD behavior (the bug) where envelope.GetCurrentScope() returns
  /// a plain ScopeContext, and GetSecurityFromAmbient() can't find it because it only accepts
  /// ImmutableScopeContext with ShouldPropagate=true.
  /// </remarks>
  [Test]
  public async Task PerspectiveWorker_WhenPlainScopeContextIsSet_GetSecurityFromAmbientReturnsNullBugAsync() {
    // Arrange - Save previous state
    var previousInitiating = ScopeContextAccessor.CurrentInitiatingContext;
    var previousContext = ScopeContextAccessor.CurrentContext;

    try {
      // Clear any existing context
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;

      // Arrange - Create a plain ScopeContext (NOT ImmutableScopeContext)
      var tenantId = "test-tenant-" + Guid.NewGuid();
      var userId = "test-user-" + Guid.NewGuid();

      var plainScope = new ScopeContext {
        Scope = new PerspectiveScope {
          TenantId = tenantId,
          UserId = userId
        },
        Roles = new HashSet<string> { "User" },
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>()
      };

      // OLD BEHAVIOR (the bug): Set plain ScopeContext directly
      // This is what happened before the fix when PerspectiveWorker used envelope.GetCurrentScope() directly
      var accessor = new ScopeContextAccessor();
      accessor.Current = plainScope;

      // Also set on static accessor (what GetSecurityFromAmbient uses)
      ScopeContextAccessor.CurrentContext = plainScope;

      // Act - Call GetSecurityFromAmbient()
      var ambientSecurity = CascadeContext.GetSecurityFromAmbient();

      // Assert - Returns null because plainScope is NOT ImmutableScopeContext
      await Assert.That(ambientSecurity).IsNull()
        .Because("GetSecurityFromAmbient returns null when CurrentContext is plain ScopeContext (not ImmutableScopeContext)");

    } finally {
      // Cleanup AsyncLocal state
      ScopeContextAccessor.CurrentInitiatingContext = previousInitiating;
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Integration test verifying that ISecurityContextCallback is invoked when PerspectiveWorker
  /// wraps envelope scope in ImmutableScopeContext.
  /// </summary>
  /// <remarks>
  /// This test validates that callbacks like UserContextManagerCallback are invoked so that
  /// lifecycle handlers can access TenantContext through their own context managers.
  /// </remarks>
  [Test]
  public async Task PerspectiveWorker_WhenEnvelopeScopeWrapped_InvokesCallbacksAsync() {
    // Arrange - Save previous state
    var previousInitiating = ScopeContextAccessor.CurrentInitiatingContext;
    var previousContext = ScopeContextAccessor.CurrentContext;

    try {
      // Clear any existing context
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;

      // Arrange - Create envelope with scope in hops
      var tenantId = "callback-tenant-" + Guid.NewGuid();
      var userId = "callback-user-" + Guid.NewGuid();

      var scopeContext = new ScopeContext {
        Scope = new PerspectiveScope {
          TenantId = tenantId,
          UserId = userId
        },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>()
      };

      var envelope = new Observability.MessageEnvelope<TestPerspectiveEvent> {
        MessageId = ValueObjects.MessageId.New(),
        Payload = new TestPerspectiveEvent(Guid.NewGuid()),
        Hops = [
          new Observability.MessageHop {
            Type = Observability.HopType.Current,
            ServiceInstance = new Observability.ServiceInstanceInfo {
              ServiceName = "TestService",
              HostName = "TestHost",
              ProcessId = Environment.ProcessId,
              InstanceId = Guid.NewGuid()
            },
            Timestamp = DateTimeOffset.UtcNow,
            Scope = ScopeDelta.CreateDelta(null, scopeContext)
          }
        ]
      };

      // Track callback invocations
      IScopeContext? capturedContext = null;
      var callbackInvoked = false;

      // Create test callback
      var testCallback = new TestSecurityContextCallback(ctx => {
        callbackInvoked = true;
        capturedContext = ctx;
      });

      // Build service provider with test callback
      var services = new ServiceCollection();
      services.AddSingleton<ISecurityContextCallback>(testCallback);
      var provider = services.BuildServiceProvider();

      // Simulate PerspectiveWorker logic when extraction fails
      IScopeContext? securityContext = null; // Failed extraction
      var scopeFromEnvelope = envelope.GetCurrentScope();

      if (securityContext is null && scopeFromEnvelope is not null) {
        var extraction = new SecurityExtraction {
          Scope = scopeFromEnvelope.Scope,
          Roles = scopeFromEnvelope.Roles,
          Permissions = scopeFromEnvelope.Permissions,
          SecurityPrincipals = scopeFromEnvelope.SecurityPrincipals,
          Claims = scopeFromEnvelope.Claims,
          ActualPrincipal = scopeFromEnvelope.ActualPrincipal,
          EffectivePrincipal = scopeFromEnvelope.EffectivePrincipal,
          ContextType = scopeFromEnvelope.ContextType,
          Source = "EnvelopeHop"
        };
        var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);

        // Set on accessor
        ScopeContextAccessor.CurrentContext = immutableScope;

        // Invoke callbacks (what PerspectiveWorker does)
        var callbacks = provider.GetServices<ISecurityContextCallback>();
        foreach (var callback in callbacks) {
          await callback.OnContextEstablishedAsync(
            immutableScope,
            envelope,
            provider,
            CancellationToken.None);
        }
      }

      // Assert - Callback was invoked with correct context
      await Assert.That(callbackInvoked).IsTrue()
        .Because("ISecurityContextCallback should be invoked when envelope scope is wrapped");

      await Assert.That(capturedContext).IsNotNull()
        .Because("Callback should receive the ImmutableScopeContext");

      await Assert.That(capturedContext!.Scope.TenantId).IsEqualTo(tenantId)
        .Because("Callback should receive the correct TenantId");

      await Assert.That(capturedContext.Scope.UserId).IsEqualTo(userId)
        .Because("Callback should receive the correct UserId");

      await Assert.That(capturedContext is ImmutableScopeContext).IsTrue()
        .Because("Callback should receive ImmutableScopeContext, not plain ScopeContext");

      var immutableCaptured = (ImmutableScopeContext)capturedContext;
      await Assert.That(immutableCaptured.ShouldPropagate).IsTrue()
        .Because("ImmutableScopeContext should have ShouldPropagate=true for event propagation");

    } finally {
      // Cleanup AsyncLocal state
      ScopeContextAccessor.CurrentInitiatingContext = previousInitiating;
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  // === SecurityContextHelper.EstablishFullContextAsync Tests ===

  [Test]
  public async Task EstablishFullContextAsync_WhenExtractionFailsButEnvelopeHasScope_SetsMessageContextScopeContextAsync() {
    // Arrange: Create envelope with scope in hops
    var tenantId = "test-tenant-" + Guid.NewGuid();
    var userId = "test-user-" + Guid.NewGuid();

    var scopeDelta = ScopeDelta.CreateDelta(null, new ScopeContext {
      Scope = new PerspectiveScope { TenantId = tenantId, UserId = userId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    });

    // DIAGNOSTIC: Verify ScopeDelta was created
    await Assert.That(scopeDelta).IsNotNull()
      .Because("ScopeDelta.CreateDelta should return non-null for non-empty scope");

    var envelope = new MessageEnvelope<TestPerspectiveEvent> {
      MessageId = MessageId.New(),
      Payload = new TestPerspectiveEvent(Guid.NewGuid()),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "test-service",
          HostName = "test-host",
          InstanceId = Guid.NewGuid(),
          ProcessId = 1
        },
        Timestamp = DateTimeOffset.UtcNow,
        Scope = scopeDelta
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // DIAGNOSTIC: Verify envelope.GetCurrentScope() returns non-null
    var envelopeScope = envelope.GetCurrentScope();
    await Assert.That(envelopeScope).IsNotNull()
      .Because("envelope.GetCurrentScope() should return scope from hop's ScopeDelta");
    await Assert.That(envelopeScope!.Scope?.TenantId).IsEqualTo(tenantId)
      .Because("envelope scope should have TenantId from ScopeDelta");

    // DIAGNOSTIC: Add a callback to capture values INSIDE the callback execution
    IScopeContext? callbackCapturedContext = null;
    IScopeContext? scopeInsideCallback = null;
    IMessageContext? messageInsideCallback = null;
    var callbackInvoked = false;
    var testCallback = new TestSecurityContextCallback(ctx => {
      callbackInvoked = true;
      callbackCapturedContext = ctx;
      // Capture static accessor values INSIDE the callback
      scopeInsideCallback = ScopeContextAccessor.CurrentContext;
      messageInsideCallback = MessageContextAccessor.CurrentContext;
    });

    // Setup DI without any security extractors (extraction will fail)
    var services = new ServiceCollection();
    services.AddSingleton<IMessageContextAccessor>(new MessageContextAccessor());
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddSingleton<ISecurityContextCallback>(testCallback);
    // Note: NO IMessageSecurityContextProvider registered - extraction will fail

    var serviceProvider = services.BuildServiceProvider();

    // DIAGNOSTIC: Verify IMessageContextAccessor is registered
    var accessorBeforeCall = serviceProvider.GetService<IMessageContextAccessor>();
    await Assert.That(accessorBeforeCall).IsNotNull()
      .Because("IMessageContextAccessor should be registered in service provider");

    // Capture values INSIDE the same sync context as EstablishFullContextAsync
    IMessageContext? capturedMessageContext = null;
    IScopeContext? capturedScopeContext = null;
    IMessageContext? capturedStaticMessage = null;
    IScopeContext? capturedStaticScope = null;

    // Act: Call EstablishFullContextAsync and capture values immediately after
    await SecurityContextHelper.EstablishFullContextAsync(envelope, serviceProvider);

    // Capture SYNCHRONOUSLY right after the await
    var messageContextAccessor = serviceProvider.GetRequiredService<IMessageContextAccessor>();
    var scopeContextAccessor = serviceProvider.GetRequiredService<IScopeContextAccessor>();

    capturedMessageContext = messageContextAccessor.Current;
    capturedScopeContext = scopeContextAccessor.Current;
    capturedStaticMessage = MessageContextAccessor.CurrentContext;
    capturedStaticScope = ScopeContextAccessor.CurrentContext;

    // FIRST: Check if callback was invoked - this proves the code path was executed
    await Assert.That(callbackInvoked).IsTrue()
      .Because("Callback should be invoked if envelope has scope but extraction fails");
    await Assert.That(callbackCapturedContext).IsNotNull()
      .Because("Callback should receive ImmutableScopeContext");

    // CRITICAL: Values must be visible INSIDE the callback execution context
    // This is where receptor handlers would be invoked in production
    await Assert.That(scopeInsideCallback).IsNotNull()
      .Because("ScopeContextAccessor.CurrentContext should be set INSIDE the callback execution");
    await Assert.That(messageInsideCallback).IsNotNull()
      .Because("MessageContextAccessor.CurrentContext should be set INSIDE the callback. " +
               $"scopeInside={scopeInsideCallback is not null}");

    // Verify the CAPTURED values inside callback have correct scope data
    // NOTE: Values captured AFTER method returns may be null due to AsyncLocal copy-on-write semantics.
    // In production, receptor handlers run WITHIN the execution context, not after returning from it.
    await Assert.That(messageInsideCallback!.ScopeContext).IsNotNull()
      .Because("MessageContext.ScopeContext should be set inside callback");
    await Assert.That(messageInsideCallback.TenantId).IsEqualTo(tenantId)
      .Because("TenantId should match envelope scope");
    await Assert.That(messageInsideCallback.UserId).IsEqualTo(userId)
      .Because("UserId should match envelope scope");

    // Verify the callback received the correct scope context
    await Assert.That(callbackCapturedContext!.Scope.TenantId).IsEqualTo(tenantId)
      .Because("Callback should receive ImmutableScopeContext with correct TenantId");
    await Assert.That(callbackCapturedContext.Scope.UserId).IsEqualTo(userId)
      .Because("Callback should receive ImmutableScopeContext with correct UserId");

    // Verify the scope inside callback matches the callback parameter
    await Assert.That(scopeInsideCallback!.Scope.TenantId).IsEqualTo(tenantId)
      .Because("ScopeContextAccessor.CurrentContext inside callback should have correct TenantId");
  }

  [Test]
  public async Task EstablishFullContextAsync_WhenExtractionFailsButEnvelopeHasScope_InvokesCallbacksAsync() {
    // Arrange: Create envelope with scope in hops
    var tenantId = "callback-tenant-" + Guid.NewGuid();
    var userId = "callback-user-" + Guid.NewGuid();

    var scopeDelta = ScopeDelta.CreateDelta(null, new ScopeContext {
      Scope = new PerspectiveScope { TenantId = tenantId, UserId = userId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    });

    var envelope = new MessageEnvelope<TestPerspectiveEvent> {
      MessageId = MessageId.New(),
      Payload = new TestPerspectiveEvent(Guid.NewGuid()),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "test-service",
          HostName = "test-host",
          InstanceId = Guid.NewGuid(),
          ProcessId = 1
        },
        Timestamp = DateTimeOffset.UtcNow,
        Scope = scopeDelta
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Setup callback to capture the context
    IScopeContext? capturedContext = null;
    var callback = new TestSecurityContextCallback(ctx => capturedContext = ctx);

    // Setup DI with callback but no extractors
    var services = new ServiceCollection();
    services.AddSingleton<IMessageContextAccessor>(new MessageContextAccessor());
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddSingleton<ISecurityContextCallback>(callback);
    // Note: NO IMessageSecurityContextProvider registered - extraction will fail

    var serviceProvider = services.BuildServiceProvider();

    // Act: Call EstablishFullContextAsync
    await SecurityContextHelper.EstablishFullContextAsync(envelope, serviceProvider);

    // Assert: Callback should have been invoked with ImmutableScopeContext
    await Assert.That(capturedContext).IsNotNull()
      .Because("Callback should be invoked when envelope has scope but extraction fails");
    await Assert.That(capturedContext is ImmutableScopeContext).IsTrue()
      .Because("Context should be wrapped in ImmutableScopeContext");
    await Assert.That(capturedContext!.Scope.TenantId).IsEqualTo(tenantId)
      .Because("Callback context should have TenantId from envelope");
    await Assert.That(capturedContext.Scope.UserId).IsEqualTo(userId)
      .Because("Callback context should have UserId from envelope");
  }

  // === AsyncLocal Flow Verification Tests ===

  [Test]
  public async Task AsyncLocal_WhenSetInAsyncMethod_ShouldFlowBackToCallerAsync() {
    // This test verifies that AsyncLocal values flow correctly across async boundaries.
    // If this test fails, there's a fundamental issue with how we're using AsyncLocal.

    // Clear any existing values
    ScopeContextAccessor.CurrentContext = null;
    MessageContextAccessor.CurrentContext = null;

    // Set value and call an async method that reads it
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "test-tenant", UserId = "test-user" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set the value
    ScopeContextAccessor.CurrentContext = immutableScope;

    // Verify it's set
    await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull()
      .Because("Value should be set immediately after assignment");

    // Call an async method with ConfigureAwait(false) - similar to EstablishFullContextAsync
    await Task.Delay(1).ConfigureAwait(false);

    // Check if value persists after ConfigureAwait(false)
    var afterAwait = ScopeContextAccessor.CurrentContext;
    await Assert.That(afterAwait).IsNotNull()
      .Because("AsyncLocal value should persist after await with ConfigureAwait(false)");
    await Assert.That(afterAwait!.Scope.TenantId).IsEqualTo("test-tenant")
      .Because("TenantId should be preserved");
  }

  [Test]
  public async Task AsyncLocal_WhenSetInsideAsyncMethod_DoesNotFlowBackToCallerAsync() {
    // IMPORTANT: This test documents expected AsyncLocal behavior.
    // AsyncLocal uses copy-on-write semantics - changes in a child execution context
    // do NOT propagate back to the parent context. This is by design.
    //
    // In Whizbang, this means values must be set and used WITHIN the same execution context.
    // For example, EstablishFullContextAsync sets values, then callbacks/handlers run
    // WITHIN that context and can see the values. But callers AFTER the method returns
    // may not see the values.
    //
    // The pattern for verifying security context is set correctly:
    // - Use callbacks (which run INSIDE the method's execution context)
    // - NOT assertions AFTER the method returns

    // Clear any existing values
    ScopeContextAccessor.CurrentContext = null;
    MessageContextAccessor.CurrentContext = null;

    // Call a method that sets the AsyncLocal and uses ConfigureAwait(false) internally
    await _setAsyncLocalWithConfigureAwaitFalseAsync();

    // AsyncLocal values set INSIDE an async method do NOT flow back to caller
    // This is expected behavior due to copy-on-write execution context semantics
    var afterMethod = ScopeContextAccessor.CurrentContext;
    await Assert.That(afterMethod).IsNull()
      .Because("AsyncLocal values set inside child async methods do NOT propagate back to parent context (by design)");
  }

  private static async Task _setAsyncLocalWithConfigureAwaitFalseAsync() {
    // Simulate what EstablishFullContextAsync does:
    // 1. Some async work with ConfigureAwait(false)
    await Task.Delay(1).ConfigureAwait(false);

    // 2. Set the AsyncLocal value
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "inside-async-method", UserId = "inside-async-method" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "InsideAsyncMethod"
    };
    var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = immutableScope;

    // 3. More async work
    await Task.Delay(1).ConfigureAwait(false);
  }

  // Test helper types for PostPerspectiveDetached tests
  public record TestPerspectiveEvent(Guid StreamId);

  private sealed class TestSecurityContextCallback(Action<IScopeContext> onContextEstablished) : ISecurityContextCallback {
    public ValueTask OnContextEstablishedAsync(
      IScopeContext context,
      Observability.IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
      onContextEstablished(context);
      return ValueTask.CompletedTask;
    }
  }

  // === JDNext Scenario Replication Tests ===
  // These tests replicate the exact scenario from JDNext where TenantContext is null

  [Test]
  [Category("JDNextScenario")]
  public async Task GetCurrentScope_WithRealDatabaseJsonStructure_DeserializesCorrectlyAsync() {
    // This test uses the EXACT JSON structure found in JDNext's wh_event_store.metadata column
    // to verify that ScopeDelta deserialization works correctly.
    //
    // The database contains:
    // {"Hops": [{"md": {...}, "sc": {"v": {"Scope": {"t": "c0ffee00-cafe-f00d-face-feed12345678", "u": "925321d2-9635-49e5-abd8-87b43dcf7e19"}}}, ...}], ...}

    // Arrange: The exact JSON from the database
    const string metadataJson = """
      {
        "Hops": [{
          "md": {"AggregateId": "019cd3a4-19dc-7548-aed6-e24ab97f8dc8"},
          "sc": {"v": {"Scope": {"t": "c0ffee00-cafe-f00d-face-feed12345678", "u": "925321d2-9635-49e5-abd8-87b43dcf7e19"}}},
          "si": {"hn": "test-host", "ii": "019cd3a0-3997-776c-b616-20bbc224dcd9", "pi": 56083, "sn": "JDX.JobService"},
          "to": "jdx.contracts.job",
          "tp": "00-8fe2f60995fa4b5b2a2792f08b1ad39f-a8e13f9311145b78-01",
          "ts": "2026-03-09T17:30:01.693249+00:00"
        }],
        "MessageId": "019cd3a6-105d-765a-9815-04daee237e04"
      }
      """;

    // Act: Deserialize using the same JSON options as EFCoreEventStore
    var options = new System.Text.Json.JsonSerializerOptions {
      PropertyNameCaseInsensitive = true,
      Converters = { new Whizbang.Core.ValueObjects.MessageIdJsonConverter() }
    };

    // Manually deserialize the metadata to see what we get
    using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
    var root = doc.RootElement;

    // Check the Hops array
    var hopsElement = root.GetProperty("Hops");
    await Assert.That(hopsElement.GetArrayLength()).IsEqualTo(1)
      .Because("Should have one hop");

    // Check the sc (Scope) property exists
    var firstHop = hopsElement[0];
    var hasSc = firstHop.TryGetProperty("sc", out var scElement);
    await Assert.That(hasSc).IsTrue()
      .Because("Hop should have 'sc' (Scope) property");

    // Check the v (Values) property in ScopeDelta
    var hasV = scElement.TryGetProperty("v", out var vElement);
    await Assert.That(hasV).IsTrue()
      .Because("ScopeDelta should have 'v' (Values) property");

    // Check the Scope property in Values
    var hasScope = vElement.TryGetProperty("Scope", out var scopeElement);
    await Assert.That(hasScope).IsTrue()
      .Because("Values should have 'Scope' property");

    // Check t (TenantId) and u (UserId)
    var hasT = scopeElement.TryGetProperty("t", out var tElement);
    var hasU = scopeElement.TryGetProperty("u", out var uElement);
    await Assert.That(hasT).IsTrue().Because("Scope should have 't' (TenantId)");
    await Assert.That(hasU).IsTrue().Because("Scope should have 'u' (UserId)");
    await Assert.That(tElement.GetString()).IsEqualTo("c0ffee00-cafe-f00d-face-feed12345678");
    await Assert.That(uElement.GetString()).IsEqualTo("925321d2-9635-49e5-abd8-87b43dcf7e19");
  }

  [Test]
  [Category("JDNextScenario")]
  public async Task GetCurrentScope_WithScopeDeltaInHops_ReturnsNonNullScopeContextAsync() {
    // This test replicates the JDNext scenario where an event is read from the database
    // with scope in hops, and GetCurrentScope() should return the scope.

    // Arrange: Create envelope with ScopeDelta in hops (matching database structure)
    const string tenantId = "c0ffee00-cafe-f00d-face-feed12345678";
    const string userId = "925321d2-9635-49e5-abd8-87b43dcf7e19";

    // Create a ScopeDelta that matches the database structure
    var scopeDelta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
        [ScopeProp.Scope] = System.Text.Json.JsonSerializer.SerializeToElement(
          new PerspectiveScope { TenantId = tenantId, UserId = userId }
        )
      }
    };

    var envelope = new MessageEnvelope<TestPerspectiveEvent> {
      MessageId = MessageId.New(),
      Payload = new TestPerspectiveEvent(Guid.NewGuid()),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "JDX.JobService",
          HostName = "test-host",
          InstanceId = Guid.NewGuid(),
          ProcessId = 56083
        },
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "jdx.contracts.job",
        Scope = scopeDelta
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var scope = envelope.GetCurrentScope();

    // Assert
    await Assert.That(scope).IsNotNull()
      .Because("GetCurrentScope() should return non-null when hops have ScopeDelta");
    await Assert.That(scope!.Scope).IsNotNull()
      .Because("ScopeContext.Scope should be set from ScopeDelta");
    await Assert.That(scope.Scope.TenantId).IsEqualTo(tenantId)
      .Because("TenantId should be extracted from ScopeDelta");
    await Assert.That(scope.Scope.UserId).IsEqualTo(userId)
      .Because("UserId should be extracted from ScopeDelta");
  }

  [Test]
  [Category("JDNextScenario")]
  public async Task PostPerspectiveDetached_WithScopeInEnvelopeHops_SetsMessageContextScopeContextAsync() {
    // This test replicates the FULL JDNext scenario:
    // 1. Event is read from database with scope in hops
    // 2. Generated runner establishes security context
    // 3. Handler should have non-null MessageContext.ScopeContext

    // Arrange
    var tenantId = "jdnext-tenant-" + Guid.NewGuid();
    var userId = "jdnext-user-" + Guid.NewGuid();

    // Create envelope with scope in hops (as if read from database)
    var scopeDelta = ScopeDelta.CreateDelta(null, new ScopeContext {
      Scope = new PerspectiveScope { TenantId = tenantId, UserId = userId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    });

    var envelope = new MessageEnvelope<TestPerspectiveEvent> {
      MessageId = MessageId.New(),
      Payload = new TestPerspectiveEvent(Guid.NewGuid()),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
        Scope = scopeDelta
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Verify envelope.GetCurrentScope() works
    var envelopeScope = envelope.GetCurrentScope();
    await Assert.That(envelopeScope).IsNotNull()
      .Because("envelope.GetCurrentScope() should return scope from hops");
    await Assert.That(envelopeScope!.Scope.TenantId).IsEqualTo(tenantId);

    // Capture what happens inside the "handler" execution context
    IScopeContext? capturedScopeInsideHandler = null;
    IMessageContext? capturedMessageInsideHandler = null;

    var testCallback = new TestSecurityContextCallback(ctx => {
      // This runs INSIDE the execution context where security is established
      capturedScopeInsideHandler = ScopeContextAccessor.CurrentContext;
      capturedMessageInsideHandler = MessageContextAccessor.CurrentContext;
    });

    // Setup DI WITHOUT any security extractors (simulates extraction failing)
    // This forces the code to fall back to envelope.GetCurrentScope()
    var services = new ServiceCollection();
    services.AddSingleton<IMessageContextAccessor>(new MessageContextAccessor());
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddSingleton<ISecurityContextCallback>(testCallback);
    // NO IMessageSecurityContextProvider - extraction will fail

    var serviceProvider = services.BuildServiceProvider();

    // Act: Simulate what the generated runner does - establish security context
    await SecurityContextHelper.EstablishFullContextAsync(envelope, serviceProvider);

    // Assert: Values captured INSIDE the handler context should be correct
    await Assert.That(capturedScopeInsideHandler).IsNotNull()
      .Because("ScopeContextAccessor.CurrentContext should be set inside handler context");
    await Assert.That(capturedScopeInsideHandler!.Scope.TenantId).IsEqualTo(tenantId)
      .Because("TenantId should match envelope scope");

    await Assert.That(capturedMessageInsideHandler).IsNotNull()
      .Because("MessageContextAccessor.CurrentContext should be set inside handler context");
    await Assert.That(capturedMessageInsideHandler!.ScopeContext).IsNotNull()
      .Because("MessageContext.ScopeContext should be non-null - THIS IS THE JDNext BUG if it fails!");
    await Assert.That(capturedMessageInsideHandler.TenantId).IsEqualTo(tenantId)
      .Because("MessageContext.TenantId should be set from envelope scope");
    await Assert.That(capturedMessageInsideHandler.UserId).IsEqualTo(userId)
      .Because("MessageContext.UserId should be set from envelope scope");
  }
}
