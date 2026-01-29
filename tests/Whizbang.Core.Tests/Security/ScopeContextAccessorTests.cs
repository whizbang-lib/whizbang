using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the ScopeContextAccessor implementation.
/// </summary>
/// <tests>ScopeContextAccessor</tests>
public class ScopeContextAccessorTests {
  // === Current Property Tests ===

  [Test]
  public async Task ScopeContextAccessor_Current_InitiallyNull_ReturnsNullAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();

    // Act & Assert
    await Assert.That(accessor.Current).IsNull();
  }

  [Test]
  public async Task ScopeContextAccessor_Current_AfterSet_ReturnsContextAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string> { "User" },
      Permissions = new HashSet<Permission> { Permission.Read("orders") },
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act
    accessor.Current = context;

    // Assert
    await Assert.That(accessor.Current).IsNotNull();
    await Assert.That(accessor.Current!.Scope.TenantId).IsEqualTo("tenant-1");
  }

  [Test]
  public async Task ScopeContextAccessor_Current_CanBeSetToNull_ReturnsNullAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    accessor.Current = context;

    // Act
    accessor.Current = null;

    // Assert
    await Assert.That(accessor.Current).IsNull();
  }

  // === AsyncLocal Propagation Tests ===

  [Test]
  public async Task ScopeContextAccessor_Current_AcrossAsyncCalls_PropagatesAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var context = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-async" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    accessor.Current = context;

    // Act - access from async method
    var tenantId = await _getTenantIdAsync(accessor);

    // Assert
    await Assert.That(tenantId).IsEqualTo("tenant-async");
  }

  [Test]
  public async Task ScopeContextAccessor_Current_InParallelTasks_HasIsolatedContextsAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var results = new List<string?>();
    var syncLock = new object();

    // Act - run parallel tasks that each set their own context
    var tasks = Enumerable.Range(1, 5).Select(async i => {
      var context = new ScopeContext {
        Scope = new PerspectiveScope { TenantId = $"tenant-{i}" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>()
      };
      accessor.Current = context;

      // Simulate async work
      await Task.Delay(10);

      // Get the tenant ID from this context
      var tenantId = accessor.Current?.Scope.TenantId;
      lock (syncLock) {
        results.Add(tenantId);
      }
    }).ToArray();

    await Task.WhenAll(tasks);

    // Assert - each task should see its own tenant ID
    await Assert.That(results).Contains("tenant-1");
    await Assert.That(results).Contains("tenant-2");
    await Assert.That(results).Contains("tenant-3");
    await Assert.That(results).Contains("tenant-4");
    await Assert.That(results).Contains("tenant-5");
  }

  [Test]
  public async Task ScopeContextAccessor_Current_ChildTaskInheritsParentContext_ReturnsSameContextAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var parentContext = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "parent-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    accessor.Current = parentContext;

    // Act - child task should see parent's context
    var childTenantId = await Task.Run(() => accessor.Current?.Scope.TenantId);

    // Assert
    await Assert.That(childTenantId).IsEqualTo("parent-tenant");
  }

  [Test]
  public async Task ScopeContextAccessor_Current_ChildModification_DoesNotAffectParentAsync() {
    // Arrange
    var accessor = new ScopeContextAccessor();
    var parentContext = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "parent-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    accessor.Current = parentContext;

    // Act - child task modifies context
    await Task.Run(() => {
      accessor.Current = new ScopeContext {
        Scope = new PerspectiveScope { TenantId = "child-tenant" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>()
      };
    });

    // Assert - parent should still see original context
    await Assert.That(accessor.Current?.Scope.TenantId).IsEqualTo("parent-tenant");
  }

  private static async Task<string?> _getTenantIdAsync(ScopeContextAccessor accessor) {
    await Task.Delay(1); // Simulate async work
    return accessor.Current?.Scope.TenantId;
  }
}
