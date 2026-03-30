using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for the ScopeFilters flags enum.
/// </summary>
/// <tests>ScopeFilters</tests>
public class ScopeFilterTests {
  // === Value Tests ===

  [Test]
  public async Task ScopeFilter_None_HasZeroValueAsync() {
    // Arrange
    var filter = ScopeFilters.None;

    // Assert
    await Assert.That((int)filter).IsEqualTo(0);
  }

  [Test]
  public async Task ScopeFilter_Tenant_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilters.Tenant;

    // Assert
    await Assert.That((int)filter).IsEqualTo(1);
  }

  [Test]
  public async Task ScopeFilter_Organization_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilters.Organization;

    // Assert
    await Assert.That((int)filter).IsEqualTo(2);
  }

  [Test]
  public async Task ScopeFilter_Customer_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilters.Customer;

    // Assert
    await Assert.That((int)filter).IsEqualTo(4);
  }

  [Test]
  public async Task ScopeFilter_User_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilters.User;

    // Assert
    await Assert.That((int)filter).IsEqualTo(8);
  }

  [Test]
  public async Task ScopeFilter_Principal_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilters.Principal;

    // Assert
    await Assert.That((int)filter).IsEqualTo(16);
  }

  // === Combination Tests ===

  [Test]
  public async Task ScopeFilter_CombinedFlags_CanBeOrTogetherAsync() {
    // Arrange & Act
    var filter = ScopeFilters.Tenant | ScopeFilters.User;

    // Assert
    await Assert.That((int)filter).IsEqualTo(9); // 1 + 8
  }

  [Test]
  public async Task ScopeFilter_HasFlag_DetectsIndividualFlagsAsync() {
    // Arrange
    var filter = ScopeFilters.Tenant | ScopeFilters.User | ScopeFilters.Principal;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Principal)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Organization)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilters.Customer)).IsFalse();
  }

  [Test]
  public async Task ScopeFilter_CombineAll_CreatesExpectedValueAsync() {
    // Arrange & Act
    var filter = ScopeFilters.Tenant | ScopeFilters.Organization | ScopeFilters.Customer
               | ScopeFilters.User | ScopeFilters.Principal;

    // Assert
    await Assert.That((int)filter).IsEqualTo(31); // 1 + 2 + 4 + 8 + 16
    await Assert.That(filter.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Organization)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Customer)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Principal)).IsTrue();
  }

  // === Extension Methods Tests ===

  [Test]
  public async Task ScopeFilterExtensions_TenantUser_ReturnsTenantOrUserAsync() {
    // Arrange & Act
    var filter = ScopeFilterExtensions.TenantUser;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Principal)).IsFalse();
  }

  [Test]
  public async Task ScopeFilterExtensions_TenantPrincipal_ReturnsTenantOrPrincipalAsync() {
    // Arrange & Act
    var filter = ScopeFilterExtensions.TenantPrincipal;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Principal)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.User)).IsFalse();
  }

  [Test]
  public async Task ScopeFilterExtensions_TenantUserOrPrincipal_ReturnsAllThreeAsync() {
    // Arrange & Act
    var filter = ScopeFilterExtensions.TenantUserOrPrincipal;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilters.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilters.Principal)).IsTrue();
  }

  // === None Flag Tests ===

  [Test]
  public async Task ScopeFilter_None_HasNoFlagsSetAsync() {
    // Arrange
    var filter = ScopeFilters.None;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilters.Tenant)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilters.Organization)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilters.Customer)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilters.User)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilters.Principal)).IsFalse();
  }
}
