// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// The scope an event audit record carries: tenant, user, correlation and claims, or nothing at all.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/EventAuditScope.cs</code-under-test>
public class EventAuditScopeTests {
  [Test]
  public async Task Build_Everything_CarriesItAllAsync() {
    var correlation = CorrelationId.New();
    var context = _context(new PerspectiveScope { TenantId = "t", UserId = "u" }, new Dictionary<string, string> { ["email"] = "e" });

    var scope = EventAuditScope.Build(context, correlation)!;

    await Assert.That(scope["TenantId"]).IsEqualTo("t");
    await Assert.That(scope["UserId"]).IsEqualTo("u");
    await Assert.That(scope["CorrelationId"]).IsEqualTo(correlation.ToString());
    await Assert.That(scope["email"]).IsEqualTo("e");
  }

  [Test]
  public async Task Build_Nothing_NullAsync() {
    await Assert.That(EventAuditScope.Build(null, null)).IsNull()
      .Because("an audit record with no scope stores none rather than an empty dictionary");
  }

  [Test]
  public async Task Build_ScopeWithoutIds_EmptyNullAsync() {
    var context = _context(new PerspectiveScope(), new Dictionary<string, string>());

    await Assert.That(EventAuditScope.Build(context, null)).IsNull();
  }

  private static ScopeContext _context(PerspectiveScope scope, Dictionary<string, string> claims) => new() {
    Scope = scope,
    Roles = new HashSet<string>(),
    Permissions = new HashSet<Permission>(),
    SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    Claims = claims,
  };
}
