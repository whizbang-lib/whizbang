using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for security attributes.
/// </summary>
/// <tests>ScopedAttribute, RequirePermissionAttribute, FieldPermissionAttribute</tests>
public class SecurityAttributeTests {
  // === ScopedAttribute Tests ===

  [Test]
  public async Task ScopedAttribute_DefaultLevel_IsTenantAsync() {
    // Arrange
    var attribute = new ScopedAttribute();

    // Assert
    await Assert.That(attribute.Filter).IsEqualTo(ScopeFilter.Tenant);
  }

  [Test]
  public async Task ScopedAttribute_WithCustomFilter_StoresFilterAsync() {
    // Arrange
    var filter = ScopeFilter.Tenant | ScopeFilter.User;
    var attribute = new ScopedAttribute(filter);

    // Assert
    await Assert.That(attribute.Filter).IsEqualTo(filter);
  }

  [Test]
  public async Task ScopedAttribute_AppliedToProperty_CanBeRetrievedAsync() {
    // Arrange
    var property = typeof(ScopedModel).GetProperty(nameof(ScopedModel.TenantId));

    // Act
    var attribute = property?.GetCustomAttributes(typeof(ScopedAttribute), true)
                           .FirstOrDefault() as ScopedAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Filter).IsEqualTo(ScopeFilter.Tenant);
  }

  // === RequirePermissionAttribute Tests ===

  [Test]
  public async Task RequirePermissionAttribute_StoresPermissionAsync() {
    // Arrange
    var attribute = new RequirePermissionAttribute("orders:read");

    // Assert
    await Assert.That(attribute.Permission.Value).IsEqualTo("orders:read");
  }

  [Test]
  public async Task RequirePermissionAttribute_AppliedToClass_CanBeRetrievedAsync() {
    // Arrange
    var type = typeof(ProtectedModel);

    // Act
    var attributes = type.GetCustomAttributes(typeof(RequirePermissionAttribute), true)
                        .Cast<RequirePermissionAttribute>()
                        .ToList();

    // Assert
    await Assert.That(attributes.Count).IsEqualTo(1);
    await Assert.That(attributes[0].Permission.Value).IsEqualTo("orders:read");
  }

  [Test]
  public async Task RequirePermissionAttribute_MultipleApplied_AllCanBeRetrievedAsync() {
    // Arrange
    var type = typeof(MultiPermissionModel);

    // Act
    var attributes = type.GetCustomAttributes(typeof(RequirePermissionAttribute), true)
                        .Cast<RequirePermissionAttribute>()
                        .ToList();

    // Assert
    await Assert.That(attributes.Count).IsEqualTo(2);
    await Assert.That(attributes.Select(a => a.Permission.Value))
          .Contains("orders:read");
    await Assert.That(attributes.Select(a => a.Permission.Value))
          .Contains("orders:write");
  }

  // === FieldPermissionAttribute Tests ===

  [Test]
  public async Task FieldPermissionAttribute_DefaultMasking_IsHideAsync() {
    // Arrange
    var attribute = new FieldPermissionAttribute("pii:view");

    // Assert
    await Assert.That(attribute.Permission.Value).IsEqualTo("pii:view");
    await Assert.That(attribute.Masking).IsEqualTo(MaskingStrategy.Hide);
  }

  [Test]
  public async Task FieldPermissionAttribute_WithMaskingStrategy_StoresStrategyAsync() {
    // Arrange
    var attribute = new FieldPermissionAttribute("pii:view", MaskingStrategy.Partial);

    // Assert
    await Assert.That(attribute.Permission.Value).IsEqualTo("pii:view");
    await Assert.That(attribute.Masking).IsEqualTo(MaskingStrategy.Partial);
  }

  [Test]
  public async Task FieldPermissionAttribute_AppliedToProperty_CanBeRetrievedAsync() {
    // Arrange
    var property = typeof(SensitiveDataModel).GetProperty(nameof(SensitiveDataModel.SSN));

    // Act
    var attribute = property?.GetCustomAttributes(typeof(FieldPermissionAttribute), true)
                           .FirstOrDefault() as FieldPermissionAttribute;

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Permission.Value).IsEqualTo("pii:view");
    await Assert.That(attribute.Masking).IsEqualTo(MaskingStrategy.Partial);
  }

  // === MaskingStrategy Tests ===

  [Test]
  public async Task MaskingStrategy_Hide_HasCorrectValueAsync() {
    // Arrange
    var strategy = MaskingStrategy.Hide;

    // Assert
    await Assert.That((int)strategy).IsEqualTo(0);
  }

  [Test]
  public async Task MaskingStrategy_Mask_HasCorrectValueAsync() {
    // Arrange
    var strategy = MaskingStrategy.Mask;

    // Assert
    await Assert.That((int)strategy).IsEqualTo(1);
  }

  [Test]
  public async Task MaskingStrategy_Partial_HasCorrectValueAsync() {
    // Arrange
    var strategy = MaskingStrategy.Partial;

    // Assert
    await Assert.That((int)strategy).IsEqualTo(2);
  }

  [Test]
  public async Task MaskingStrategy_Redact_HasCorrectValueAsync() {
    // Arrange
    var strategy = MaskingStrategy.Redact;

    // Assert
    await Assert.That((int)strategy).IsEqualTo(3);
  }

  // === Test Models ===

  private sealed class ScopedModel {
    [Scoped]
    public string? TenantId { get; init; }
  }

  [RequirePermission("orders:read")]
  private sealed class ProtectedModel {
    public string? OrderId { get; init; }
  }

  [RequirePermission("orders:read")]
  [RequirePermission("orders:write")]
  private sealed class MultiPermissionModel {
    public string? OrderId { get; init; }
  }

  private sealed class SensitiveDataModel {
    [FieldPermission("pii:view", MaskingStrategy.Partial)]
    public string? SSN { get; init; }
  }
}
