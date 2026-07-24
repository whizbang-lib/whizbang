using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for <see cref="ScopeFilterOverride"/>.
/// </summary>
public class ScopeFilterOverrideTests {
  [Test]
  public async Task ScopeFilterOverride_DefaultValues_AllNullAsync() {
    var ov = new ScopeFilterOverride();

    await Assert.That(ov.TenantId).IsNull();
    await Assert.That(ov.UserId).IsNull();
    await Assert.That(ov.OrganizationId).IsNull();
    await Assert.That(ov.CustomerId).IsNull();
  }

  [Test]
  public async Task ScopeFilterOverride_WithInit_SetsValuesAsync() {
    var ov = new ScopeFilterOverride {
      TenantId = "tenant-1",
      UserId = "user-1",
      OrganizationId = "org-1",
      CustomerId = "customer-1"
    };

    await Assert.That(ov.TenantId).IsEqualTo("tenant-1");
    await Assert.That(ov.UserId).IsEqualTo("user-1");
    await Assert.That(ov.OrganizationId).IsEqualTo("org-1");
    await Assert.That(ov.CustomerId).IsEqualTo("customer-1");
  }

  [Test]
  public async Task ScopeFilterOverride_Equality_SameValues_AreEqualAsync() {
    var a = new ScopeFilterOverride { TenantId = "t1" };
    var b = new ScopeFilterOverride { TenantId = "t1" };

    await Assert.That(a).IsEqualTo(b);
  }

  [Test]
  public async Task ScopeFilterOverride_Equality_DifferentValues_AreNotEqualAsync() {
    var a = new ScopeFilterOverride { TenantId = "t1" };
    var b = new ScopeFilterOverride { TenantId = "t2" };

    await Assert.That(a).IsNotEqualTo(b);
  }
}
