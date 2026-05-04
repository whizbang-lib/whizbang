using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for <see cref="PerspectiveScope.FilterByFields"/>. The runtime helper that backs
/// <see cref="InheritScopeAttribute"/> — given a source scope and a flag set, produce a
/// new scope containing only the named fields.
/// </summary>
/// <tests>PerspectiveScope.FilterByFields,InheritScopeAttribute</tests>
public class PerspectiveScopeFilterByFieldsTests {
  private static PerspectiveScope _full() => new() {
    TenantId = "t-1",
    UserId = "u-1",
    CustomerId = "c-1",
    OrganizationId = "o-1",
    AllowedPrincipals = ["user:alice", "group:sales"],
    Extensions = [new("region", "us-west")],
  };

  [Test]
  public async Task FilterByFields_None_ReturnsEmptyScopeAsync() {
    var filtered = _full().FilterByFields(ScopeFields.None);
    var t = filtered.TenantId;
    var u = filtered.UserId;
    var c = filtered.CustomerId;
    var o = filtered.OrganizationId;
    var apCount = filtered.AllowedPrincipals.Count;
    var exCount = filtered.Extensions.Count;
    await Assert.That(t).IsNull();
    await Assert.That(u).IsNull();
    await Assert.That(c).IsNull();
    await Assert.That(o).IsNull();
    await Assert.That(apCount).IsEqualTo(0);
    await Assert.That(exCount).IsEqualTo(0);
  }

  [Test]
  public async Task FilterByFields_TenantOnly_KeepsOnlyTenantAsync() {
    var filtered = _full().FilterByFields(ScopeFields.Tenant);
    var t = filtered.TenantId;
    var u = filtered.UserId;
    var c = filtered.CustomerId;
    var o = filtered.OrganizationId;
    var apCount = filtered.AllowedPrincipals.Count;
    var exCount = filtered.Extensions.Count;
    await Assert.That(t).IsEqualTo("t-1");
    await Assert.That(u).IsNull();
    await Assert.That(c).IsNull();
    await Assert.That(o).IsNull();
    await Assert.That(apCount).IsEqualTo(0);
    await Assert.That(exCount).IsEqualTo(0);
  }

  [Test]
  public async Task FilterByFields_TenantAndUser_KeepsBothAsync() {
    var filtered = _full().FilterByFields(ScopeFields.Tenant | ScopeFields.User);
    var t = filtered.TenantId;
    var u = filtered.UserId;
    var c = filtered.CustomerId;
    await Assert.That(t).IsEqualTo("t-1");
    await Assert.That(u).IsEqualTo("u-1");
    await Assert.That(c).IsNull();
  }

  [Test]
  public async Task FilterByFields_AllowedPrincipalsOnly_CopiesListAsync() {
    var filtered = _full().FilterByFields(ScopeFields.AllowedPrincipals);
    var apCount = filtered.AllowedPrincipals.Count;
    await Assert.That(apCount).IsEqualTo(2);
    await Assert.That(filtered.AllowedPrincipals[0]).IsEqualTo("user:alice");
    await Assert.That(filtered.AllowedPrincipals[1]).IsEqualTo("group:sales");
  }

  [Test]
  public async Task FilterByFields_AllowedPrincipals_DoesNotShareListWithSourceAsync() {
    var src = _full();
    var filtered = src.FilterByFields(ScopeFields.AllowedPrincipals);
    filtered.AllowedPrincipals.Add("group:other");
    var srcCount = src.AllowedPrincipals.Count;
    await Assert.That(srcCount).IsEqualTo(2);
  }

  [Test]
  public async Task FilterByFields_Extensions_CopiesListAsync() {
    var filtered = _full().FilterByFields(ScopeFields.Extensions);
    var exCount = filtered.Extensions.Count;
    await Assert.That(exCount).IsEqualTo(1);
    await Assert.That(filtered.Extensions[0].Key).IsEqualTo("region");
    await Assert.That(filtered.Extensions[0].Value).IsEqualTo("us-west");
  }

  [Test]
  public async Task FilterByFields_All_KeepsEverythingAsync() {
    var filtered = _full().FilterByFields(ScopeFields.All);
    var t = filtered.TenantId;
    var u = filtered.UserId;
    var c = filtered.CustomerId;
    var o = filtered.OrganizationId;
    var apCount = filtered.AllowedPrincipals.Count;
    var exCount = filtered.Extensions.Count;
    await Assert.That(t).IsEqualTo("t-1");
    await Assert.That(u).IsEqualTo("u-1");
    await Assert.That(c).IsEqualTo("c-1");
    await Assert.That(o).IsEqualTo("o-1");
    await Assert.That(apCount).IsEqualTo(2);
    await Assert.That(exCount).IsEqualTo(1);
  }

  [Test]
  public async Task FilterByFields_ReturnsNewInstance_DoesNotMutateSourceAsync() {
    var src = _full();
    var filtered = src.FilterByFields(ScopeFields.Tenant);
    var areSame = ReferenceEquals(src, filtered);
    var srcUserStillSet = src.UserId;
    await Assert.That(areSame).IsFalse();
    await Assert.That(srcUserStillSet).IsEqualTo("u-1");
  }
}
