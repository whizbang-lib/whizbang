extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using NamingConventionUtilities = shared::Whizbang.Generators.Shared.Utilities.NamingConventionUtilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for NamingConventionUtilities.
/// Tests naming convention conversion utilities used by all generators.
/// </summary>
public class NamingConventionUtilitiesTests {
  #region ToSnakeCase Tests

  [Test]
  public async Task ToSnakeCase_PascalCase_ReturnsSnakeCaseAsync() {
    // Arrange
    var input = "OrderItem";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("order_item");
  }

  [Test]
  public async Task ToSnakeCase_MultipleWords_ReturnsSnakeCaseAsync() {
    // Arrange
    var input = "ActiveJobTemplateModel";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("active_job_template_model");
  }

  [Test]
  public async Task ToSnakeCase_EmptyString_ReturnsEmptyAsync() {
    // Arrange
    var input = "";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task ToSnakeCase_Null_ReturnsNullAsync() {
    // Arrange
    string? input = null;

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input!);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ToSnakeCase_SingleWord_ReturnsLowercaseAsync() {
    // Arrange
    var input = "Order";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("order");
  }

  [Test]
  public async Task ToSnakeCase_AllLowercase_ReturnsSameAsync() {
    // Arrange
    var input = "order";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("order");
  }

  #endregion

  #region Pluralize Tests

  [Test]
  public async Task Pluralize_WithoutS_AddsSAsync() {
    // Arrange
    var input = "Order";

    // Act
    var result = NamingConventionUtilities.Pluralize(input);

    // Assert
    await Assert.That(result).IsEqualTo("Orders");
  }

  [Test]
  public async Task Pluralize_WithS_ReturnsSameAsync() {
    // Arrange
    var input = "Orders";

    // Act
    var result = NamingConventionUtilities.Pluralize(input);

    // Assert - Already ends with 's', returns unchanged
    await Assert.That(result).IsEqualTo("Orders");
  }

  [Test]
  public async Task Pluralize_Empty_ReturnsEmptyAsync() {
    // Arrange
    var input = "";

    // Act
    var result = NamingConventionUtilities.Pluralize(input);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task Pluralize_Null_ReturnsNullAsync() {
    // Arrange
    string? input = null;

    // Act
    var result = NamingConventionUtilities.Pluralize(input!);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region StripCommonSuffixes Tests

  [Test]
  public async Task StripCommonSuffixes_Model_StripsAsync() {
    // Arrange
    var input = "OrderModel";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripCommonSuffixes_ReadModel_StripsAsync() {
    // Arrange
    var input = "OrderReadModel";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripCommonSuffixes_Dto_StripsAsync() {
    // Arrange
    var input = "ProductDto";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Product");
  }

  [Test]
  public async Task StripCommonSuffixes_NoSuffix_ReturnsSameAsync() {
    // Arrange
    var input = "Order";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripCommonSuffixes_Empty_ReturnsEmptyAsync() {
    // Arrange
    var input = "";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task StripCommonSuffixes_Null_ReturnsNullAsync() {
    // Arrange
    string? input = null;

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input!);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region ToDefaultRouteName Tests

  [Test]
  public async Task ToDefaultRouteName_ReturnsApiPrefixedRouteAsync() {
    // Arrange
    var input = "OrderReadModel";

    // Act
    var result = NamingConventionUtilities.ToDefaultRouteName(input);

    // Assert
    await Assert.That(result).IsEqualTo("/api/orders");
  }

  [Test]
  public async Task ToDefaultRouteName_WithDto_StripsAndPluralizesAsync() {
    // Arrange
    var input = "ProductDto";

    // Act
    var result = NamingConventionUtilities.ToDefaultRouteName(input);

    // Assert
    await Assert.That(result).IsEqualTo("/api/products");
  }

  [Test]
  public async Task ToDefaultRouteName_AlreadyPlural_DoesNotDoublePluralizeAsync() {
    // Arrange
    var input = "Orders";

    // Act
    var result = NamingConventionUtilities.ToDefaultRouteName(input);

    // Assert - Should not become "orderss"
    await Assert.That(result).IsEqualTo("/api/orders");
  }

  #endregion

  #region ToDefaultQueryName Tests

  [Test]
  public async Task ToDefaultQueryName_ReturnsCamelCasePluralAsync() {
    // Arrange
    var input = "OrderReadModel";

    // Act
    var result = NamingConventionUtilities.ToDefaultQueryName(input);

    // Assert - No /api/ prefix, just the name
    await Assert.That(result).IsEqualTo("orders");
  }

  [Test]
  public async Task ToDefaultQueryName_WithModel_StripsAndPluralizesAsync() {
    // Arrange
    var input = "ProductModel";

    // Act
    var result = NamingConventionUtilities.ToDefaultQueryName(input);

    // Assert
    await Assert.That(result).IsEqualTo("products");
  }

  #endregion
}
