using Whizbang.Core.Scoping;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for the scoping marker interfaces.
/// </summary>
/// <tests>ITenantScoped, IUserScoped, IOrganizationScoped, ICustomerScoped</tests>
public class MarkerInterfaceTests {
  // === ITenantScoped Tests ===

  [Test]
  public async Task ITenantScoped_ImplementingModel_HasTenantIdAsync() {
    // Arrange
    ITenantScoped model = new TenantScopedModel { TenantId = "tenant-123" };

    // Act & Assert
    await Assert.That(model.TenantId).IsEqualTo("tenant-123");
  }

  // === IUserScoped Tests ===

  [Test]
  public async Task IUserScoped_ImplementingModel_HasUserIdAndTenantIdAsync() {
    // Arrange
    IUserScoped model = new UserScopedModel {
      TenantId = "tenant-123",
      UserId = "user-456"
    };

    // Act & Assert
    await Assert.That(model.TenantId).IsEqualTo("tenant-123");
    await Assert.That(model.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task IUserScoped_InheritedFromITenantScoped_CanBeUsedAsTenantScopedAsync() {
    // Arrange
    IUserScoped userModel = new UserScopedModel {
      TenantId = "tenant-123",
      UserId = "user-456"
    };

    // Act - can assign to ITenantScoped
    ITenantScoped tenantModel = userModel;

    // Assert
    await Assert.That(tenantModel.TenantId).IsEqualTo("tenant-123");
  }

  // === IOrganizationScoped Tests ===

  [Test]
  public async Task IOrganizationScoped_ImplementingModel_HasOrganizationIdAndTenantIdAsync() {
    // Arrange
    IOrganizationScoped model = new OrganizationScopedModel {
      TenantId = "tenant-123",
      OrganizationId = "org-789"
    };

    // Act & Assert
    await Assert.That(model.TenantId).IsEqualTo("tenant-123");
    await Assert.That(model.OrganizationId).IsEqualTo("org-789");
  }

  // === ICustomerScoped Tests ===

  [Test]
  public async Task ICustomerScoped_ImplementingModel_HasCustomerIdAndTenantIdAsync() {
    // Arrange
    ICustomerScoped model = new CustomerScopedModel {
      TenantId = "tenant-123",
      CustomerId = "customer-abc"
    };

    // Act & Assert
    await Assert.That(model.TenantId).IsEqualTo("tenant-123");
    await Assert.That(model.CustomerId).IsEqualTo("customer-abc");
  }

  // === Test Models ===

  private sealed record TenantScopedModel : ITenantScoped {
    public required string TenantId { get; init; }
  }

  private sealed record UserScopedModel : IUserScoped {
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
  }

  private sealed record OrganizationScopedModel : IOrganizationScoped {
    public required string TenantId { get; init; }
    public required string OrganizationId { get; init; }
  }

  private sealed record CustomerScopedModel : ICustomerScoped {
    public required string TenantId { get; init; }
    public required string CustomerId { get; init; }
  }
}
