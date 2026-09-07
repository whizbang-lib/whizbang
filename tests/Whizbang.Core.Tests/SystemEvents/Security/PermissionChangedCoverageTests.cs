using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Tests.SystemEvents.Security;

/// <summary>
/// Covers <see cref="PermissionChanged.PermissionsAdded"/>. Its siblings each have a dedicated
/// test in <c>SecuritySystemEventTests</c> (<c>RolesAdded</c> is set and asserted,
/// <c>PermissionsRemoved</c> has its own test) but <c>PermissionsAdded</c> itself is never set
/// anywhere in the suite.
/// </summary>
public class PermissionChangedCoverageTests {

  /// <summary>
  /// If this property stopped round-tripping correctly, a permission-grant audit event would
  /// report the wrong (or no) permissions actually added — the exact record compliance tooling
  /// reads to answer "what was this user granted, and when."
  /// </summary>
  [Test]
  public async Task PermissionChanged_PermissionsAdded_HasCorrectTypeAsync() {
    var @event = new PermissionChanged {
      UserId = "user-1",
      TenantId = "tenant-1",
      ChangeType = PermissionChangeType.PermissionsAdded,
      PermissionsAdded = new HashSet<Permission> { Permission.Write("orders") },
      ChangedBy = "admin-user",
      Timestamp = DateTimeOffset.UtcNow
    };

    await Assert.That(@event.ChangeType).IsEqualTo(PermissionChangeType.PermissionsAdded);
    await Assert.That(@event.PermissionsAdded).Contains(Permission.Write("orders"));
    await Assert.That(@event.PermissionsRemoved).IsNull();
  }
}
