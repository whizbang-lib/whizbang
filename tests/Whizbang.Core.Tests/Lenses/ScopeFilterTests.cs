using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for the ScopeFilter flags enum.
/// </summary>
/// <tests>ScopeFilter</tests>
public class ScopeFilterTests {
  // === Value Tests ===

  [Test]
  public async Task ScopeFilter_None_HasZeroValueAsync() {
    // Arrange
    var filter = ScopeFilter.None;

    // Assert
    await Assert.That((int)filter).IsEqualTo(0);
  }

  [Test]
  public async Task ScopeFilter_Tenant_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilter.Tenant;

    // Assert
    await Assert.That((int)filter).IsEqualTo(1);
  }

  [Test]
  public async Task ScopeFilter_Organization_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilter.Organization;

    // Assert
    await Assert.That((int)filter).IsEqualTo(2);
  }

  [Test]
  public async Task ScopeFilter_Customer_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilter.Customer;

    // Assert
    await Assert.That((int)filter).IsEqualTo(4);
  }

  [Test]
  public async Task ScopeFilter_User_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilter.User;

    // Assert
    await Assert.That((int)filter).IsEqualTo(8);
  }

  [Test]
  public async Task ScopeFilter_Principal_HasCorrectValueAsync() {
    // Arrange
    var filter = ScopeFilter.Principal;

    // Assert
    await Assert.That((int)filter).IsEqualTo(16);
  }

  // === Combination Tests ===

  [Test]
  public async Task ScopeFilter_CombinedFlags_CanBeOrTogetherAsync() {
    // Arrange & Act
    var filter = ScopeFilter.Tenant | ScopeFilter.User;

    // Assert
    await Assert.That((int)filter).IsEqualTo(9); // 1 + 8
  }

  [Test]
  public async Task ScopeFilter_HasFlag_DetectsIndividualFlagsAsync() {
    // Arrange
    var filter = ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Principal)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Organization)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilter.Customer)).IsFalse();
  }

  [Test]
  public async Task ScopeFilter_CombineAll_CreatesExpectedValueAsync() {
    // Arrange & Act
    var filter = ScopeFilter.Tenant | ScopeFilter.Organization | ScopeFilter.Customer
               | ScopeFilter.User | ScopeFilter.Principal;

    // Assert
    await Assert.That((int)filter).IsEqualTo(31); // 1 + 2 + 4 + 8 + 16
    await Assert.That(filter.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Organization)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Customer)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Principal)).IsTrue();
  }

  // === Extension Methods Tests ===

  [Test]
  public async Task ScopeFilterExtensions_TenantUser_ReturnsTenantOrUserAsync() {
    // Arrange & Act
    var filter = ScopeFilterExtensions.TenantUser;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Principal)).IsFalse();
  }

  [Test]
  public async Task ScopeFilterExtensions_TenantPrincipal_ReturnsTenantOrPrincipalAsync() {
    // Arrange & Act
    var filter = ScopeFilterExtensions.TenantPrincipal;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Principal)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.User)).IsFalse();
  }

  [Test]
  public async Task ScopeFilterExtensions_TenantUserOrPrincipal_ReturnsAllThreeAsync() {
    // Arrange & Act
    var filter = ScopeFilterExtensions.TenantUserOrPrincipal;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilter.Tenant)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.User)).IsTrue();
    await Assert.That(filter.HasFlag(ScopeFilter.Principal)).IsTrue();
  }

  // === None Flag Tests ===

  [Test]
  public async Task ScopeFilter_None_HasNoFlagsSetAsync() {
    // Arrange
    var filter = ScopeFilter.None;

    // Assert
    await Assert.That(filter.HasFlag(ScopeFilter.Tenant)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilter.Organization)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilter.Customer)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilter.User)).IsFalse();
    await Assert.That(filter.HasFlag(ScopeFilter.Principal)).IsFalse();
  }
}
