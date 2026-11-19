// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>
/// The security scope a multi-stream command carries from the instance that received it to the instances that work its
/// streams (#1358). It round-trips through the same delta a message hop carries.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#security-scope</docs>
[Category("MultiStream")]
public class MultiStreamScopeTests {
  private static ScopeContext _context(PerspectiveScope scope, params string[] roles) => new() {
    Scope = scope,
    Roles = new HashSet<string>(roles),
    Permissions = new HashSet<Permission>(),
    SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    Claims = new Dictionary<string, string>()
  };

  [Test]
  public async Task CaptureThenRestore_KeepsTenantUserAndRolesAsync() {
    var captured = MultiStreamScope.Capture(_context(new PerspectiveScope { TenantId = "tenant-b", UserId = "user-b" }, "role-1"));

    var restored = MultiStreamScope.Restore(captured);

    await Assert.That(restored!.Scope.TenantId).IsEqualTo("tenant-b");
    await Assert.That(restored.Scope.UserId).IsEqualTo("user-b");
    await Assert.That(restored.Roles).Contains("role-1");
  }

  [Test]
  public async Task Capture_NoContext_IsNullAsync() {
    await Assert.That(MultiStreamScope.Capture(null)).IsNull();
  }

  [Test]
  public async Task CaptureThenRestore_AnEmptyScope_RestoresAnEmptyScopeAsync() {
    var restored = MultiStreamScope.Restore(MultiStreamScope.Capture(_context(new PerspectiveScope())));

    await Assert.That(restored).IsNotNull()
      .Because("a command sent under an empty scope is worked under an empty scope, not under whatever is ambient later");
    await Assert.That(restored!.Scope.TenantId).IsNull();
    await Assert.That(restored.Roles).IsEmpty();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  public async Task Restore_NothingStored_IsNullAsync(string? stored) {
    await Assert.That(MultiStreamScope.Restore(stored)).IsNull();
  }

  [Test]
  public async Task Restore_AStoredNull_IsNullAsync() {
    await Assert.That(MultiStreamScope.Restore("null")).IsNull();
  }
}
