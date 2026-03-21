extern alias shared;

using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using NamingConventionUtilities = shared::Whizbang.Generators.Shared.Utilities.NamingConventionUtilities;
using TableNameConfig = shared::Whizbang.Generators.Shared.Models.TableNameConfig;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for NamingConventionUtilities.
/// Tests naming convention conversion utilities used by all generators.
/// </summary>
public class NamingConventionUtilitiesTests {
  // Static readonly configs to satisfy CA1861 (avoid allocating arrays repeatedly)
  private static readonly TableNameConfig _defaultConfig = TableNameConfig.Default;
  private static readonly TableNameConfig _disabledConfig = TableNameConfig.NoStripping;
  private static readonly TableNameConfig _readModelFirstConfig = new(
      StripSuffixes: true,
      SuffixesToStrip: ["ReadModel", "Model", "Projection", "Dto", "View"]
  );
  private static readonly TableNameConfig _minimalConfig = new(
      StripSuffixes: true,
      SuffixesToStrip: ["Model", "Projection"]
  );
  private static readonly TableNameConfig _customSuffixConfig = new(
      StripSuffixes: true,
      SuffixesToStrip: ["Aggregate", "State"]
  );
  private static readonly TableNameConfig _singleSuffixConfig = new(
      StripSuffixes: true,
      SuffixesToStrip: ["Model"]
  );
  private static readonly TableNameConfig _modelViewConfig = new(
      StripSuffixes: true,
      SuffixesToStrip: ["ModelView", "Model", "View"]
  );
  private static readonly TableNameConfig _emptySuffixConfig = new(
      StripSuffixes: true,
      SuffixesToStrip: []
  );

  #region ToSnakeCase Tests

  [Test]
  public async Task ToSnakeCase_PascalCase_ReturnsSnakeCaseAsync() {
    // Arrange
    const string input = "OrderItem";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("order_item");
  }

  [Test]
  public async Task ToSnakeCase_MultipleWords_ReturnsSnakeCaseAsync() {
    // Arrange
    const string input = "ActiveJobTemplateModel";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("active_job_template_model");
  }

  [Test]
  public async Task ToSnakeCase_EmptyString_ReturnsEmptyAsync() {
    // Arrange
    const string input = "";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task ToSnakeCase_Null_ReturnsNullAsync() {
    // Arrange
    const string? input = null;

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input!);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ToSnakeCase_SingleWord_ReturnsLowercaseAsync() {
    // Arrange
    const string input = "Order";

    // Act
    var result = NamingConventionUtilities.ToSnakeCase(input);

    // Assert
    await Assert.That(result).IsEqualTo("order");
  }

  [Test]
  public async Task ToSnakeCase_AllLowercase_ReturnsSameAsync() {
    // Arrange
    const string input = "order";

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
    const string input = "Order";

    // Act
    var result = NamingConventionUtilities.Pluralize(input);

    // Assert
    await Assert.That(result).IsEqualTo("Orders");
  }

  [Test]
  public async Task Pluralize_WithS_ReturnsSameAsync() {
    // Arrange
    const string input = "Orders";

    // Act
    var result = NamingConventionUtilities.Pluralize(input);

    // Assert - Already ends with 's', returns unchanged
    await Assert.That(result).IsEqualTo("Orders");
  }

  [Test]
  public async Task Pluralize_Empty_ReturnsEmptyAsync() {
    // Arrange
    const string input = "";

    // Act
    var result = NamingConventionUtilities.Pluralize(input);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task Pluralize_Null_ReturnsNullAsync() {
    // Arrange
    const string? input = null;

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
    const string input = "OrderModel";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripCommonSuffixes_ReadModel_StripsAsync() {
    // Arrange
    const string input = "OrderReadModel";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripCommonSuffixes_Dto_StripsAsync() {
    // Arrange
    const string input = "ProductDto";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Product");
  }

  [Test]
  public async Task StripCommonSuffixes_NoSuffix_ReturnsSameAsync() {
    // Arrange
    const string input = "Order";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripCommonSuffixes_Empty_ReturnsEmptyAsync() {
    // Arrange
    const string input = "";

    // Act
    var result = NamingConventionUtilities.StripCommonSuffixes(input);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task StripCommonSuffixes_Null_ReturnsNullAsync() {
    // Arrange
    const string? input = null;

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
    const string input = "OrderReadModel";

    // Act
    var result = NamingConventionUtilities.ToDefaultRouteName(input);

    // Assert
    await Assert.That(result).IsEqualTo("/api/orders");
  }

  [Test]
  public async Task ToDefaultRouteName_WithDto_StripsAndPluralizesAsync() {
    // Arrange
    const string input = "ProductDto";

    // Act
    var result = NamingConventionUtilities.ToDefaultRouteName(input);

    // Assert
    await Assert.That(result).IsEqualTo("/api/products");
  }

  [Test]
  public async Task ToDefaultRouteName_AlreadyPlural_DoesNotDoublePluralizeAsync() {
    // Arrange
    const string input = "Orders";

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
    const string input = "OrderReadModel";

    // Act
    var result = NamingConventionUtilities.ToDefaultQueryName(input);

    // Assert - No /api/ prefix, just the name
    await Assert.That(result).IsEqualTo("orders");
  }

  [Test]
  public async Task ToDefaultQueryName_WithModel_StripsAndPluralizesAsync() {
    // Arrange
    const string input = "ProductModel";

    // Act
    var result = NamingConventionUtilities.ToDefaultQueryName(input);

    // Assert
    await Assert.That(result).IsEqualTo("products");
  }

  #endregion

  #region StripConfigurableSuffixes Tests

  [Test]
  public async Task StripConfigurableSuffixes_WhenEnabled_StripsMatchingSuffixAsync() {
    // Arrange
    const string input = "OrderProjection";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripConfigurableSuffixes_WhenDisabled_ReturnsInputUnchangedAsync() {
    // Arrange
    const string input = "OrderProjection";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _disabledConfig);

    // Assert
    await Assert.That(result).IsEqualTo("OrderProjection");
  }

  [Test]
  public async Task StripConfigurableSuffixes_WithModel_StripsModelAsync() {
    // Arrange
    const string input = "ActivityEmbeddingModel";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("ActivityEmbedding");
  }

  [Test]
  public async Task StripConfigurableSuffixes_WithReadModel_StripsReadModelAsync() {
    // Arrange - ReadModel should be checked before Model (longer suffix first)
    const string input = "OrderReadModel";

    // Act - Use config with ReadModel first
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _readModelFirstConfig);

    // Assert - Should strip "ReadModel", not just "Model"
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripConfigurableSuffixes_WithDto_StripsDtoAsync() {
    // Arrange
    const string input = "ProductDto";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("Product");
  }

  [Test]
  public async Task StripConfigurableSuffixes_WithView_StripsViewAsync() {
    // Arrange
    const string input = "CustomerView";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("Customer");
  }

  [Test]
  public async Task StripConfigurableSuffixes_NoMatchingSuffix_ReturnsUnchangedAsync() {
    // Arrange
    const string input = "OrderEntity";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("OrderEntity");
  }

  [Test]
  public async Task StripConfigurableSuffixes_EmptyString_ReturnsEmptyAsync() {
    // Arrange
    const string input = "";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _minimalConfig);

    // Assert
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task StripConfigurableSuffixes_Null_ReturnsNullAsync() {
    // Arrange
    const string? input = null;

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input!, _minimalConfig);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task StripConfigurableSuffixes_EmptySuffixArray_ReturnsUnchangedAsync() {
    // Arrange
    const string input = "OrderProjection";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _emptySuffixConfig);

    // Assert
    await Assert.That(result).IsEqualTo("OrderProjection");
  }

  [Test]
  public async Task StripConfigurableSuffixes_CustomSuffixes_StripsCustomSuffixAsync() {
    // Arrange
    const string input = "OrderAggregate";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _customSuffixConfig);

    // Assert
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task StripConfigurableSuffixes_CaseSensitive_DoesNotMatchWrongCaseAsync() {
    // Arrange - suffixes should be case-sensitive
    const string input = "OrderMODEL";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _singleSuffixConfig);

    // Assert - Should NOT match because case differs
    await Assert.That(result).IsEqualTo("OrderMODEL");
  }

  [Test]
  public async Task StripConfigurableSuffixes_OnlySuffix_ReturnsEmptyIfOnlySuffixAsync() {
    // Arrange - Edge case: name is only the suffix
    const string input = "Model";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _singleSuffixConfig);

    // Assert - Stripping "Model" from "Model" leaves empty
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task StripConfigurableSuffixes_LongerSuffixMatchedFirst_StripsCorrectlyAsync() {
    // Arrange - If ModelView is in list before View, "OrderModelView" should strip "ModelView"
    const string input = "OrderModelView";

    // Act
    var result = NamingConventionUtilities.StripConfigurableSuffixes(input, _modelViewConfig);

    // Assert - Should strip "ModelView", not "View"
    await Assert.That(result).IsEqualTo("Order");
  }

  #endregion

  #region GenerateTableName Tests

  [Test]
  public async Task GenerateTableName_WithProjection_GeneratesCorrectTableNameAsync() {
    // Arrange
    const string input = "OrderProjection";

    // Act
    var result = NamingConventionUtilities.GenerateTableName(input, _defaultConfig);

    // Assert - wh_per_ prefix + snake_case(stripped name)
    await Assert.That(result).IsEqualTo("wh_per_order");
  }

  [Test]
  public async Task GenerateTableName_WithModel_GeneratesCorrectTableNameAsync() {
    // Arrange
    const string input = "ActivityEmbeddingModel";

    // Act
    var result = NamingConventionUtilities.GenerateTableName(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("wh_per_activity_embedding");
  }

  [Test]
  public async Task GenerateTableName_WhenStripDisabled_IncludesSuffixAsync() {
    // Arrange
    const string input = "OrderProjection";

    // Act
    var result = NamingConventionUtilities.GenerateTableName(input, _disabledConfig);

    // Assert - Suffix is kept when stripping is disabled
    await Assert.That(result).IsEqualTo("wh_per_order_projection");
  }

  [Test]
  public async Task GenerateTableName_ComplexName_GeneratesCorrectTableNameAsync() {
    // Arrange
    const string input = "ActiveJobTemplateFieldCatalogProjection";

    // Act
    var result = NamingConventionUtilities.GenerateTableName(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("wh_per_active_job_template_field_catalog");
  }

  [Test]
  public async Task GenerateTableName_NoMatchingSuffix_UsesFullNameAsync() {
    // Arrange
    const string input = "OrderEntity";

    // Act
    var result = NamingConventionUtilities.GenerateTableName(input, _defaultConfig);

    // Assert
    await Assert.That(result).IsEqualTo("wh_per_order_entity");
  }

  [Test]
  public async Task GenerateTableName_SingleWord_GeneratesCorrectTableNameAsync() {
    // Arrange
    const string input = "Order";

    // Act
    var result = NamingConventionUtilities.GenerateTableName(input, _minimalConfig);

    // Assert
    await Assert.That(result).IsEqualTo("wh_per_order");
  }

  [Test]
  public async Task GenerateTableName_EmptyString_ReturnsJustPrefixAsync() {
    // Arrange
    const string input = "";

    // Act
    var result = NamingConventionUtilities.GenerateTableName(input, _minimalConfig);

    // Assert - Just the prefix for empty input
    await Assert.That(result).IsEqualTo("wh_per_");
  }

  #endregion
}
