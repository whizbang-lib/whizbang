using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for security-related attributes (FieldPermission, RequirePermission, Scoped).
/// </summary>
public class SecurityAttributeTests {
  #region FieldPermissionAttribute

  [Test]
  public async Task FieldPermissionAttribute_Constructor_CreatesPermissionAsync() {
    // Arrange & Act
    var attr = new FieldPermissionAttribute("pii:view");

    // Assert
    await Assert.That(attr.Permission.Value).IsEqualTo("pii:view");
  }

  [Test]
  public async Task FieldPermissionAttribute_Constructor_DefaultMaskingIsHideAsync() {
    // Arrange & Act
    var attr = new FieldPermissionAttribute("pii:view");

    // Assert
    await Assert.That(attr.Masking).IsEqualTo(MaskingStrategy.Hide);
  }

  [Test]
  public async Task FieldPermissionAttribute_Constructor_WithMaskingStrategy_SetsMaskingAsync() {
    // Arrange & Act
    var attr = new FieldPermissionAttribute("pii:view", MaskingStrategy.Partial);

    // Assert
    await Assert.That(attr.Masking).IsEqualTo(MaskingStrategy.Partial);
  }

  [Test]
  public async Task FieldPermissionAttribute_Constructor_WithMaskStrategy_SetsMaskingAsync() {
    // Arrange & Act
    var attr = new FieldPermissionAttribute("ssn:view", MaskingStrategy.Mask);

    // Assert
    await Assert.That(attr.Masking).IsEqualTo(MaskingStrategy.Mask);
  }

  [Test]
  public async Task FieldPermissionAttribute_Constructor_WithRedactStrategy_SetsMaskingAsync() {
    // Arrange & Act
    var attr = new FieldPermissionAttribute("classified:view", MaskingStrategy.Redact);

    // Assert
    await Assert.That(attr.Masking).IsEqualTo(MaskingStrategy.Redact);
  }

  #endregion

  #region RequirePermissionAttribute

  [Test]
  public async Task RequirePermissionAttribute_Constructor_CreatesPermissionAsync() {
    // Arrange & Act
    var attr = new RequirePermissionAttribute("orders:read");

    // Assert
    await Assert.That(attr.Permission.Value).IsEqualTo("orders:read");
  }

  [Test]
  public async Task RequirePermissionAttribute_Constructor_WithResourceAction_CreatesPermissionAsync() {
    // Arrange & Act
    var attr = new RequirePermissionAttribute("customers:admin");

    // Assert
    await Assert.That(attr.Permission.Value).IsEqualTo("customers:admin");
  }

  #endregion

  #region ScopedAttribute

  [Test]
  public async Task ScopedAttribute_DefaultConstructor_UsesTenantFilterAsync() {
    // Arrange & Act
    var attr = new ScopedAttribute();

    // Assert
    await Assert.That(attr.Filter).IsEqualTo(ScopeFilter.Tenant);
  }

  [Test]
  public async Task ScopedAttribute_Constructor_WithFilter_SetsFilterAsync() {
    // Arrange & Act
    var attr = new ScopedAttribute(ScopeFilter.User);

    // Assert
    await Assert.That(attr.Filter).IsEqualTo(ScopeFilter.User);
  }

  [Test]
  public async Task ScopedAttribute_Constructor_WithCombinedFilters_SetsFilterAsync() {
    // Arrange & Act
    var attr = new ScopedAttribute(ScopeFilter.Tenant | ScopeFilter.User);

    // Assert
    await Assert.That(attr.Filter).IsEqualTo(ScopeFilter.Tenant | ScopeFilter.User);
  }

  [Test]
  public async Task ScopedAttribute_Constructor_WithOrganization_SetsFilterAsync() {
    // Arrange & Act
    var attr = new ScopedAttribute(ScopeFilter.Organization);

    // Assert
    await Assert.That(attr.Filter).IsEqualTo(ScopeFilter.Organization);
  }

  [Test]
  public async Task ScopedAttribute_Constructor_WithPrincipal_SetsFilterAsync() {
    // Arrange & Act
    var attr = new ScopedAttribute(ScopeFilter.Principal);

    // Assert
    await Assert.That(attr.Filter).IsEqualTo(ScopeFilter.Principal);
  }

  #endregion

  #region MaskingStrategy Enum Values

  [Test]
  public async Task MaskingStrategy_AllValues_AreDistinctAsync() {
    // Arrange
    var values = new[] {
      MaskingStrategy.Hide,
      MaskingStrategy.Mask,
      MaskingStrategy.Partial,
      MaskingStrategy.Redact
    };

    // Act
    var distinctValues = values.Select(v => (int)v).Distinct().ToList();

    // Assert
    await Assert.That(distinctValues).Count().IsEqualTo(4);
  }

  [Test]
  public async Task MaskingStrategy_Hide_IsDefaultValueAsync() {
    // Arrange
    var defaultStrategy = default(MaskingStrategy);

    // Assert
    await Assert.That(defaultStrategy).IsEqualTo(MaskingStrategy.Hide);
  }

  #endregion
}
