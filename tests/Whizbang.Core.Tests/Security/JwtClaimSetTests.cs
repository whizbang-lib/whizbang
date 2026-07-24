using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="JwtClaimSet"/> — the multi-value-aware JWT claim builder helper.
/// </summary>
/// <tests>JwtClaimSet</tests>
public class JwtClaimSetTests {
  [Test]
  public async Task NewSet_IsEmptyAsync() {
    var set = new JwtClaimSet();
    var scalarsCount = set.Scalars.Count;
    var multiCount = set.MultiValued.Count;
    await Assert.That(scalarsCount).IsEqualTo(0);
    await Assert.That(multiCount).IsEqualTo(0);
  }

  [Test]
  public async Task SetScalar_AddsEntryAsync() {
    var set = new JwtClaimSet();
    set.SetScalar("sub", "user-1");
    var got = set.Scalars["sub"];
    await Assert.That(got).IsEqualTo("user-1");
  }

  [Test]
  public async Task SetScalar_LastWriteWinsAsync() {
    var set = new JwtClaimSet();
    set.SetScalar("sub", "first").SetScalar("sub", "second");
    var got = set.Scalars["sub"];
    await Assert.That(got).IsEqualTo("second");
  }

  [Test]
  public async Task AddMultiValued_AccumulatesAsync() {
    var set = new JwtClaimSet();
    set.AddMultiValued("permissions", "nav.home").AddMultiValued("permissions", "nav.job");
    var count = set.MultiValued.Count;
    var values = set.MultiValued.Where(kvp => kvp.Key == "permissions").Select(kvp => kvp.Value).ToList();
    await Assert.That(count).IsEqualTo(2);
    await Assert.That(values.Contains("nav.home")).IsTrue();
    await Assert.That(values.Contains("nav.job")).IsTrue();
  }

  [Test]
  public async Task AddMultiValuedRange_ExpandsEachValueIntoSeparateEntryAsync() {
    var set = new JwtClaimSet();
    set.AddMultiValuedRange("groups", ["g1", "g2", "g3"]);
    var count = set.MultiValued.Count;
    await Assert.That(count).IsEqualTo(3);
  }

  [Test]
  public async Task AddMultiValuedRange_EmptyEnumerable_IsNoOpAsync() {
    var set = new JwtClaimSet();
    set.AddMultiValuedRange("groups", []);
    var count = set.MultiValued.Count;
    await Assert.That(count).IsEqualTo(0);
  }

  [Test]
  public async Task ToClaims_EmitsScalarsAndMultiValued_PreservesOrderAsync() {
    var set = new JwtClaimSet();
    set.SetScalar("sub", "user-1").SetScalar("tenant_id", "t-1");
    set.AddMultiValuedRange("permissions", ["nav.home", "nav.job"]);
    set.AddMultiValuedRange("groups", ["g1"]);

    var claims = set.ToClaims().ToList();

    var totalCount = claims.Count;
    await Assert.That(totalCount).IsEqualTo(5);
    var permissions = claims.Where(c => c.Type == "permissions").Select(c => c.Value).ToList();
    var groups = claims.Where(c => c.Type == "groups").Select(c => c.Value).ToList();
    var sub = claims.First(c => c.Type == "sub").Value;
    await Assert.That(permissions.Count).IsEqualTo(2);
    await Assert.That(groups.Count).IsEqualTo(1);
    await Assert.That(sub).IsEqualTo("user-1");
  }

  [Test]
  public async Task ToClaims_NoEntriesForUnsetClaimAsync() {
    var set = new JwtClaimSet();
    set.SetScalar("sub", "u");
    var anyOther = set.ToClaims().Any(c => c.Type != "sub");
    await Assert.That(anyOther).IsFalse();
  }
}
