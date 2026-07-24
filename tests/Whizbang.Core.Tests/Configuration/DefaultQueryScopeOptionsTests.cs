using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// Tests for DefaultQueryScope on WhizbangCoreOptions.
/// </summary>
public class DefaultQueryScopeOptionsTests {
  [Test]
  public async Task DefaultQueryScope_DefaultsToTenantAsync() {
    var options = new WhizbangCoreOptions();

    await Assert.That(options.DefaultQueryScope).IsEqualTo(QueryScope.Tenant);
  }

  [Test]
  public async Task DefaultQueryScope_CanBeSetToGlobalAsync() {
    var options = new WhizbangCoreOptions {
      DefaultQueryScope = QueryScope.Global
    };

    await Assert.That(options.DefaultQueryScope).IsEqualTo(QueryScope.Global);
  }

  [Test]
  public async Task DefaultQueryScope_CanBeSetToUserAsync() {
    var options = new WhizbangCoreOptions {
      DefaultQueryScope = QueryScope.User
    };

    await Assert.That(options.DefaultQueryScope).IsEqualTo(QueryScope.User);
  }

  [Test]
  public async Task DefaultQueryScope_CanBeSetToOrganizationAsync() {
    var options = new WhizbangCoreOptions {
      DefaultQueryScope = QueryScope.Organization
    };

    await Assert.That(options.DefaultQueryScope).IsEqualTo(QueryScope.Organization);
  }

  [Test]
  public async Task DefaultQueryScope_CanBeSetToAllValuesAsync() {
    var options = new WhizbangCoreOptions();

    foreach (var scope in Enum.GetValues<QueryScope>()) {
      options.DefaultQueryScope = scope;
      await Assert.That(options.DefaultQueryScope).IsEqualTo(scope);
    }
  }
}
