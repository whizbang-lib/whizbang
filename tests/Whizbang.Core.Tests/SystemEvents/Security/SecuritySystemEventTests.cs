using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Tests.SystemEvents.Security;

/// <summary>
/// Tests for security system events.
/// </summary>
/// <tests>AccessDenied, AccessGranted, PermissionChanged, ScopeContextEstablished</tests>
public class SecuritySystemEventTests {
  // === AccessDenied Tests ===

  [Test]
  public async Task AccessDenied_Constructor_AllPropertiesSetAsync() {
    // Arrange & Act
    var @event = new AccessDenied {
      ResourceType = "Order",
      ResourceId = "order-123",
      RequiredPermission = Permission.Read("orders"),
      CallerPermissions = new HashSet<Permission> { Permission.Read("customers") },
      CallerRoles = new HashSet<string> { "User" },
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Reason = AccessDenialReason.InsufficientPermission,
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event.ResourceType).IsEqualTo("Order");
    await Assert.That(@event.ResourceId).IsEqualTo("order-123");
    await Assert.That(@event.RequiredPermission.Value).IsEqualTo("orders:read");
    await Assert.That(@event.CallerPermissions.Count).IsEqualTo(1);
    await Assert.That(@event.CallerRoles.Count).IsEqualTo(1);
    await Assert.That(@event.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(@event.Reason).IsEqualTo(AccessDenialReason.InsufficientPermission);
  }

  [Test]
  public async Task AccessDenied_IsSystemEvent_ReturnsTrueAsync() {
    // Arrange
    var @event = new AccessDenied {
      ResourceType = "Order",
      RequiredPermission = Permission.Read("orders"),
      CallerPermissions = new HashSet<Permission>(),
      CallerRoles = new HashSet<string>(),
      Scope = new PerspectiveScope(),
      Reason = AccessDenialReason.InsufficientPermission,
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event is ISystemEvent).IsTrue();
  }

  // === AccessGranted Tests ===

  [Test]
  public async Task AccessGranted_Constructor_AllPropertiesSetAsync() {
    // Arrange & Act
    var @event = new AccessGranted {
      ResourceType = "Order",
      ResourceId = "order-123",
      UsedPermission = Permission.Read("orders"),
      AccessFilter = ScopeFilter.Tenant | ScopeFilter.User,
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event.ResourceType).IsEqualTo("Order");
    await Assert.That(@event.ResourceId).IsEqualTo("order-123");
    await Assert.That(@event.UsedPermission.Value).IsEqualTo("orders:read");
    await Assert.That(@event.AccessFilter.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(@event.AccessFilter.HasFlag(ScopeFilter.User)).IsTrue();
    await Assert.That(@event.Scope.TenantId).IsEqualTo("tenant-1");
  }

  [Test]
  public async Task AccessGranted_IsSystemEvent_ReturnsTrueAsync() {
    // Arrange
    var @event = new AccessGranted {
      ResourceType = "Order",
      UsedPermission = Permission.Read("orders"),
      AccessFilter = ScopeFilter.Tenant,
      Scope = new PerspectiveScope(),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event is ISystemEvent).IsTrue();
  }

  // === PermissionChanged Tests ===

  [Test]
  public async Task PermissionChanged_RolesAdded_HasCorrectTypeAsync() {
    // Arrange & Act
    var @event = new PermissionChanged {
      UserId = "user-1",
      TenantId = "tenant-1",
      ChangeType = PermissionChangeType.RolesAdded,
      RolesAdded = new HashSet<string> { "Admin", "Supervisor" },
      ChangedBy = "admin-user",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event.ChangeType).IsEqualTo(PermissionChangeType.RolesAdded);
    await Assert.That(@event.RolesAdded).Contains("Admin");
    await Assert.That(@event.RolesAdded).Contains("Supervisor");
    await Assert.That(@event.RolesRemoved).IsNull();
  }

  [Test]
  public async Task PermissionChanged_PermissionsRemoved_HasCorrectTypeAsync() {
    // Arrange & Act
    var @event = new PermissionChanged {
      UserId = "user-1",
      TenantId = "tenant-1",
      ChangeType = PermissionChangeType.PermissionsRemoved,
      PermissionsRemoved = new HashSet<Permission> { Permission.Delete("orders") },
      ChangedBy = "admin-user",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event.ChangeType).IsEqualTo(PermissionChangeType.PermissionsRemoved);
    await Assert.That(@event.PermissionsRemoved).Contains(Permission.Delete("orders"));
  }

  [Test]
  public async Task PermissionChanged_IsSystemEvent_ReturnsTrueAsync() {
    // Arrange
    var @event = new PermissionChanged {
      UserId = "user-1",
      TenantId = "tenant-1",
      ChangeType = PermissionChangeType.RolesAdded,
      ChangedBy = "admin-user",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event is ISystemEvent).IsTrue();
  }

  // === ScopeContextEstablished Tests ===

  [Test]
  public async Task ScopeContextEstablished_HasAllFieldsAsync() {
    // Arrange & Act
    var @event = new ScopeContextEstablished {
      Scope = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" },
      Roles = new HashSet<string> { "Admin" },
      Permissions = new HashSet<Permission> { Permission.All("*") },
      Source = "JWT",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(@event.Scope.UserId).IsEqualTo("user-1");
    await Assert.That(@event.Roles).Contains("Admin");
    await Assert.That(@event.Permissions.Count).IsEqualTo(1);
    await Assert.That(@event.Source).IsEqualTo("JWT");
  }

  [Test]
  public async Task ScopeContextEstablished_IsSystemEvent_ReturnsTrueAsync() {
    // Arrange
    var @event = new ScopeContextEstablished {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      Source = "API Key",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(@event is ISystemEvent).IsTrue();
  }

  // === AccessDenialReason Tests ===

  [Test]
  public async Task AccessDenialReason_AllValuesDefinedAsync() {
    // Assert - verify all enum values exist
    await Assert.That(Enum.IsDefined(AccessDenialReason.InsufficientPermission)).IsTrue();
    await Assert.That(Enum.IsDefined(AccessDenialReason.InsufficientRole)).IsTrue();
    await Assert.That(Enum.IsDefined(AccessDenialReason.ScopeViolation)).IsTrue();
    await Assert.That(Enum.IsDefined(AccessDenialReason.PolicyRejected)).IsTrue();
  }

  // === PermissionChangeType Tests ===

  [Test]
  public async Task PermissionChangeType_AllValuesDefinedAsync() {
    // Assert - verify all enum values exist
    await Assert.That(Enum.IsDefined(PermissionChangeType.RolesAdded)).IsTrue();
    await Assert.That(Enum.IsDefined(PermissionChangeType.RolesRemoved)).IsTrue();
    await Assert.That(Enum.IsDefined(PermissionChangeType.PermissionsAdded)).IsTrue();
    await Assert.That(Enum.IsDefined(PermissionChangeType.PermissionsRemoved)).IsTrue();
    await Assert.That(Enum.IsDefined(PermissionChangeType.FullReassignment)).IsTrue();
  }
}
