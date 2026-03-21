using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for <see cref="QueryScopeMapper"/>.
/// Verifies all enum values map to the correct ScopeFilter combinations.
/// </summary>
public class QueryScopeMapperTests {
  [Test]
  public async Task ToScopeFilter_Global_ReturnsScopeFilterNoneAsync() {
    var result = QueryScopeMapper.ToScopeFilter(QueryScope.Global);
    await Assert.That(result).IsEqualTo(ScopeFilter.None);
  }

  [Test]
  public async Task ToScopeFilter_Tenant_ReturnsScopeFilterTenantAsync() {
    var result = QueryScopeMapper.ToScopeFilter(QueryScope.Tenant);
    await Assert.That(result).IsEqualTo(ScopeFilter.Tenant);
  }

  [Test]
  public async Task ToScopeFilter_Organization_ReturnsTenantAndOrganizationAsync() {
    var result = QueryScopeMapper.ToScopeFilter(QueryScope.Organization);
    await Assert.That(result).IsEqualTo(ScopeFilter.Tenant | ScopeFilter.Organization);
  }

  [Test]
  public async Task ToScopeFilter_Customer_ReturnsTenantAndCustomerAsync() {
    var result = QueryScopeMapper.ToScopeFilter(QueryScope.Customer);
    await Assert.That(result).IsEqualTo(ScopeFilter.Tenant | ScopeFilter.Customer);
  }

  [Test]
  public async Task ToScopeFilter_User_ReturnsTenantAndUserAsync() {
    var result = QueryScopeMapper.ToScopeFilter(QueryScope.User);
    await Assert.That(result).IsEqualTo(ScopeFilter.Tenant | ScopeFilter.User);
  }

  [Test]
  public async Task ToScopeFilter_Principal_ReturnsTenantAndPrincipalAsync() {
    var result = QueryScopeMapper.ToScopeFilter(QueryScope.Principal);
    await Assert.That(result).IsEqualTo(ScopeFilter.Tenant | ScopeFilter.Principal);
  }

  [Test]
  public async Task ToScopeFilter_UserOrPrincipal_ReturnsTenantUserAndPrincipalAsync() {
    var result = QueryScopeMapper.ToScopeFilter(QueryScope.UserOrPrincipal);
    await Assert.That(result).IsEqualTo(ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal);
  }

  [Test]
  public async Task ToScopeFilter_InvalidValue_ThrowsArgumentOutOfRangeExceptionAsync() {
    await Assert.That(() => QueryScopeMapper.ToScopeFilter((QueryScope)999))
        .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task ToScopeFilter_AllDefinedValues_MapWithoutExceptionAsync() {
    foreach (var scope in Enum.GetValues<QueryScope>()) {
      var result = QueryScopeMapper.ToScopeFilter(scope);
      // Every non-Global scope should include Tenant
      if (scope != QueryScope.Global) {
        await Assert.That(result.HasFlag(ScopeFilter.Tenant)).IsTrue();
      }
    }
  }
}
