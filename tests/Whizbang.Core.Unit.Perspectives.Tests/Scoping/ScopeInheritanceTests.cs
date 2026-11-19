// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for <see cref="ScopeInheritance"/> and <see cref="PerspectiveScope.WithFieldsFrom"/>: which scope a
/// perspective write carries, given the row's stored scope, the latest message's scope and the model's
/// <see cref="InheritScopeAttribute"/>.
/// </summary>
public class ScopeInheritanceTests {
  private static PerspectiveScope _creator() => new() {
    TenantId = "tenant-1",
    UserId = "user-creator",
    CustomerId = "customer-1",
    OrganizationId = "org-1",
    AllowedPrincipals = ["group:owners"],
    Extensions = [new ScopeExtension("region", "east")],
  };

  private static PerspectiveScope _editor() => new() {
    TenantId = "tenant-1",
    UserId = "user-editor",
    CustomerId = "customer-2",
    OrganizationId = "org-2",
    AllowedPrincipals = ["group:editors"],
    Extensions = [new ScopeExtension("region", "west")],
  };

  [Test]
  public async Task WithFieldsFrom_ReplacesOnlyTheNamedFieldsAsync() {
    var merged = _creator().WithFieldsFrom(_editor(), ScopeFields.User);

    await Assert.That(merged.UserId).IsEqualTo("user-editor");
    await Assert.That(merged.TenantId).IsEqualTo("tenant-1");
    await Assert.That(merged.CustomerId).IsEqualTo("customer-1");
    await Assert.That(merged.OrganizationId).IsEqualTo("org-1");
    await Assert.That(merged.AllowedPrincipals).IsEquivalentTo(["group:owners"]);
    await Assert.That(merged.Extensions.Single().Value).IsEqualTo("east");
  }

  [Test]
  public async Task WithFieldsFrom_ReplacesEveryNamedFieldAsync() {
    var merged = _creator().WithFieldsFrom(_editor(), ScopeFields.All);

    await Assert.That(merged.TenantId).IsEqualTo("tenant-1");
    await Assert.That(merged.UserId).IsEqualTo("user-editor");
    await Assert.That(merged.CustomerId).IsEqualTo("customer-2");
    await Assert.That(merged.OrganizationId).IsEqualTo("org-2");
    await Assert.That(merged.AllowedPrincipals).IsEquivalentTo(["group:editors"]);
    await Assert.That(merged.Extensions.Single().Value).IsEqualTo("west");
  }

  [Test]
  public async Task WithFieldsFrom_KeepsAFieldTheSourceDoesNotCarryAsync() {
    // A message with no user (a system write) does not erase who last wrote the row.
    var system = new PerspectiveScope { TenantId = "tenant-1" };

    var merged = _creator().WithFieldsFrom(system, ScopeFields.User | ScopeFields.AllowedPrincipals | ScopeFields.Extensions);

    await Assert.That(merged.UserId).IsEqualTo("user-creator");
    await Assert.That(merged.AllowedPrincipals).IsEquivalentTo(["group:owners"]);
    await Assert.That(merged.Extensions.Single().Value).IsEqualTo("east");
  }

  [Test]
  public async Task WithFieldsFrom_KeepsTheRowsFlagsAndDoesNotChangeEitherInputAsync() {
    var row = _creator();
    row.IsSystem = true;
    row.IsDeclaredUnscoped = true;
    var editor = _editor();

    var merged = row.WithFieldsFrom(editor, ScopeFields.User | ScopeFields.AllowedPrincipals);
    merged.AllowedPrincipals.Add("group:later");

    await Assert.That(merged.IsSystem).IsTrue();
    await Assert.That(merged.IsDeclaredUnscoped).IsTrue();
    await Assert.That(row.UserId).IsEqualTo("user-creator");
    await Assert.That(row.AllowedPrincipals).IsEquivalentTo(["group:owners"]);
    await Assert.That(editor.AllowedPrincipals).IsEquivalentTo(["group:editors"]);
  }

  [Test]
  public async Task WithFieldsFrom_NoFields_IsAnEqualCopyAsync() {
    var row = _creator();

    var merged = row.WithFieldsFrom(_editor(), ScopeFields.None);

    await Assert.That(merged).IsNotSameReferenceAs(row);
    await Assert.That(merged.UserId).IsEqualTo("user-creator");
    await Assert.That(merged.OrganizationId).IsEqualTo("org-1");
  }

  [Test]
  public async Task Resolve_NewRow_CarriesTheCreateAndAlwaysFieldsAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: _creator(), rowScope: null, rowExists: false,
      onCreate: ScopeFields.Tenant, always: ScopeFields.User, scopeEventScope: null);

    await Assert.That(write.ForceUpdate).IsFalse();
    await Assert.That(write.Scope!.TenantId).IsEqualTo("tenant-1");
    await Assert.That(write.Scope.UserId).IsEqualTo("user-creator")
      .Because("a field the row keeps current is also set when the row is created");
    await Assert.That(write.Scope.OrganizationId).IsNull();
  }

  [Test]
  public async Task Resolve_NewRow_WithDefaults_CarriesEveryFieldAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: _creator(), rowScope: null, rowExists: false,
      onCreate: ScopeFields.All, always: ScopeFields.None, scopeEventScope: null);

    await Assert.That(write.Scope!.UserId).IsEqualTo("user-creator");
    await Assert.That(write.Scope.OrganizationId).IsEqualTo("org-1");
    await Assert.That(write.Scope.AllowedPrincipals).IsEquivalentTo(["group:owners"]);
  }

  [Test]
  public async Task Resolve_ExistingRow_WithNoAlwaysFields_LeavesTheStoredScopeAloneAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: _editor(), rowScope: _creator(), rowExists: true,
      onCreate: ScopeFields.All, always: ScopeFields.None, scopeEventScope: null);

    await Assert.That(write.ForceUpdate).IsFalse()
      .Because("scope is set once at creation unless the model asks for fields to follow the latest writer");
  }

  [Test]
  public async Task Resolve_ExistingRow_WithAlwaysFields_UpdatesOnlyThoseFieldsAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: _editor(), rowScope: _creator(), rowExists: true,
      onCreate: ScopeFields.All, always: ScopeFields.User, scopeEventScope: null);

    await Assert.That(write.ForceUpdate).IsTrue();
    await Assert.That(write.Scope!.UserId).IsEqualTo("user-editor");
    await Assert.That(write.Scope.OrganizationId).IsEqualTo("org-1")
      .Because("fields the model does not keep current stay as the creating message set them");
    await Assert.That(write.Scope.AllowedPrincipals).IsEquivalentTo(["group:owners"]);
  }

  [Test]
  public async Task Resolve_ExistingRow_WithAlwaysFields_ButNoStoredScope_StartsFromAnEmptyScopeAsync() {
    // A row written before scope existed reads back with no scope; the latest writer's fields still land.
    var write = ScopeInheritance.Resolve(
      messageScope: _editor(), rowScope: null, rowExists: true,
      onCreate: ScopeFields.All, always: ScopeFields.User, scopeEventScope: null);

    await Assert.That(write.ForceUpdate).IsTrue();
    await Assert.That(write.Scope!.UserId).IsEqualTo("user-editor");
    await Assert.That(write.Scope.TenantId).IsNull();
  }

  [Test]
  public async Task Resolve_ScopeEvent_ForcesTheEventsScopeAsBeforeAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: _creator(), rowScope: _creator(), rowExists: true,
      onCreate: ScopeFields.All, always: ScopeFields.User, scopeEventScope: _editor());

    await Assert.That(write.ForceUpdate).IsTrue();
    await Assert.That(write.Scope!.OrganizationId).IsEqualTo("org-2")
      .Because("a scope event replaces the row's scope; Always fields do not narrow it, and the latest message's scope does not replace it");
  }

  [Test]
  public async Task Resolve_ScopeEvent_IsNotFilteredByTheCreateFieldsAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: _creator(), rowScope: null, rowExists: false,
      onCreate: ScopeFields.Tenant, always: ScopeFields.None, scopeEventScope: _creator());

    await Assert.That(write.ForceUpdate).IsTrue();
    await Assert.That(write.Scope!.AllowedPrincipals).IsEquivalentTo(["group:owners"])
      .Because("OnCreate decides what a row inherits from a message, not what a scope event sets (#1432)");
  }

  [Test]
  public async Task Resolve_ScopeEvent_WithNoMessageScope_IsStillWrittenAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: null, rowScope: _creator(), rowExists: true,
      onCreate: ScopeFields.All, always: ScopeFields.None, scopeEventScope: _editor());

    await Assert.That(write.ForceUpdate).IsTrue();
    await Assert.That(write.Scope!.UserId).IsEqualTo("user-editor");
  }

  [Test]
  public async Task Resolve_NoMessageScope_WritesNothingAndForcesNothingAsync() {
    var write = ScopeInheritance.Resolve(
      messageScope: null, rowScope: _creator(), rowExists: true,
      onCreate: ScopeFields.All, always: ScopeFields.User, scopeEventScope: null);

    await Assert.That(write.Scope).IsNull();
    await Assert.That(write.ForceUpdate).IsFalse();
  }
}
