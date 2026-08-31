extern alias shared;

using System;
using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ConfigurationUtilities = shared::Whizbang.Generators.Shared.Utilities.ConfigurationUtilities;
using TableNameConfig = shared::Whizbang.Generators.Shared.Models.TableNameConfig;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for ConfigurationUtilities.
/// Tests MSBuild property parsing for table naming configuration.
/// </summary>
public class ConfigurationUtilitiesTests {
  #region ParseSuffixList Tests

  [Test]
  public async Task ParseSuffixList_CommaSeparated_ReturnsArrayAsync() {
    // Arrange
    const string input = "Model,Projection,Dto";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).Count().IsEqualTo(3);
    await Assert.That(result[0]).IsEqualTo("Model");
    await Assert.That(result[1]).IsEqualTo("Projection");
    await Assert.That(result[2]).IsEqualTo("Dto");
  }

  [Test]
  public async Task ParseSuffixList_WithWhitespace_TrimsValuesAsync() {
    // Arrange
    const string input = " Model , Projection , Dto ";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).Count().IsEqualTo(3);
    await Assert.That(result[0]).IsEqualTo("Model");
    await Assert.That(result[1]).IsEqualTo("Projection");
    await Assert.That(result[2]).IsEqualTo("Dto");
  }

  [Test]
  public async Task ParseSuffixList_EmptyString_ReturnsEmptyArrayAsync() {
    // Arrange
    const string input = "";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task ParseSuffixList_Null_ReturnsEmptyArrayAsync() {
    // Arrange
    const string? input = null;

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input!);

    // Assert
    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task ParseSuffixList_WhitespaceOnly_ReturnsEmptyArrayAsync() {
    // Arrange
    const string input = "   ";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task ParseSuffixList_SingleValue_ReturnsSingleElementArrayAsync() {
    // Arrange
    const string input = "Model";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).Count().IsEqualTo(1);
    await Assert.That(result[0]).IsEqualTo("Model");
  }

  [Test]
  public async Task ParseSuffixList_EmptyEntries_FiltersThemOutAsync() {
    // Arrange
    const string input = "Model,,Projection,,,Dto";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).Count().IsEqualTo(3);
    await Assert.That(result[0]).IsEqualTo("Model");
    await Assert.That(result[1]).IsEqualTo("Projection");
    await Assert.That(result[2]).IsEqualTo("Dto");
  }

  [Test]
  public async Task ParseSuffixList_TrailingComma_HandlesCorrectlyAsync() {
    // Arrange
    const string input = "Model,Projection,";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).Count().IsEqualTo(2);
    await Assert.That(result[0]).IsEqualTo("Model");
    await Assert.That(result[1]).IsEqualTo("Projection");
  }

  [Test]
  public async Task ParseSuffixList_LeadingComma_HandlesCorrectlyAsync() {
    // Arrange
    const string input = ",Model,Projection";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).Count().IsEqualTo(2);
    await Assert.That(result[0]).IsEqualTo("Model");
    await Assert.That(result[1]).IsEqualTo("Projection");
  }

  [Test]
  public async Task ParseSuffixList_DefaultSuffixes_ParsesCorrectlyAsync() {
    // Arrange - The default value as it would appear in MSBuild
    const string input = "ReadModel,Model,Projection,Dto,View";

    // Act
    var result = ConfigurationUtilities.ParseSuffixList(input);

    // Assert
    await Assert.That(result).Count().IsEqualTo(5);
    await Assert.That(result[0]).IsEqualTo("ReadModel");
    await Assert.That(result[1]).IsEqualTo("Model");
    await Assert.That(result[2]).IsEqualTo("Projection");
    await Assert.That(result[3]).IsEqualTo("Dto");
    await Assert.That(result[4]).IsEqualTo("View");
  }

  #endregion

  #region GetTableNameConfig Tests

  [Test]
  public async Task GetTableNameConfig_NullOptions_ReturnsDefaultAsync() {
    // Arrange
    Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions? options = null;

    // Act
    var result = ConfigurationUtilities.GetTableNameConfig(options!);

    // Assert
    await Assert.That(result.StripSuffixes).IsTrue();
    await Assert.That(result.SuffixesToStrip).IsEquivalentTo(TableNameConfig.Default.SuffixesToStrip);
  }

  #endregion

  #region Property Name Constants Tests

  [Test]
  public async Task STRIP_TABLE_NAME_SUFFIXES_PROPERTY_HasCorrectValueAsync() {
    // Arrange - store constant in variable to satisfy TUnit assertion rules
    var value = ConfigurationUtilities.STRIP_TABLE_NAME_SUFFIXES_PROPERTY;

    // Assert
    await Assert.That(value).IsEqualTo("build_property.WhizbangStripTableNameSuffixes");
  }

  [Test]
  public async Task TABLE_NAME_SUFFIXES_TO_STRIP_PROPERTY_HasCorrectValueAsync() {
    // Arrange - store constant in variable to satisfy TUnit assertion rules
    var value = ConfigurationUtilities.TABLE_NAME_SUFFIXES_TO_STRIP_PROPERTY;

    // Assert
    await Assert.That(value).IsEqualTo("build_property.WhizbangTableNameSuffixesToStrip");
  }

  [Test]
  public async Task MAX_IDENTIFIER_LENGTH_PROPERTY_HasCorrectValueAsync() {
    // Arrange - store constant in variable to satisfy TUnit assertion rules
    var value = ConfigurationUtilities.MAX_IDENTIFIER_LENGTH_PROPERTY;

    // Assert
    await Assert.That(value).IsEqualTo("build_property.WhizbangMaxIdentifierLength");
  }

  #endregion

  #region GetMaxIdentifierLengthOverride Tests

  [Test]
  public async Task GetMaxIdentifierLengthOverride_NullOptions_ReturnsNullAsync() {
    // Arrange
    Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions? options = null;

    // Act
    var result = ConfigurationUtilities.GetMaxIdentifierLengthOverride(options!);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region GetTableNameConfig — populated options

  /// <summary>Minimal AnalyzerConfigOptions backed by a dictionary of MSBuild properties.</summary>
  private sealed class StubOptions(Dictionary<string, string> values)
      : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions {
    public override bool TryGetValue(string key, out string value) {
      if (values.TryGetValue(key, out var found)) {
        value = found;
        return true;
      }
      value = null!;
      return false;
    }
  }

  [Test]
  [Arguments("true", true)]
  [Arguments("TRUE", true)]
  [Arguments("false", false)]
  [Arguments("nonsense", false)]
  public async Task GetTableNameConfig_ReadsStripSuffixesProperty_CaseInsensitivelyAsync(
      string raw, bool expected) {
    var options = new StubOptions(new Dictionary<string, string> {
      [ConfigurationUtilities.STRIP_TABLE_NAME_SUFFIXES_PROPERTY] = raw
    });

    var result = ConfigurationUtilities.GetTableNameConfig(options);

    await Assert.That(result.StripSuffixes).IsEqualTo(expected);
  }

  [Test]
  public async Task GetTableNameConfig_ReadsSuffixListProperty_AsyncAsync() {
    var options = new StubOptions(new Dictionary<string, string> {
      [ConfigurationUtilities.TABLE_NAME_SUFFIXES_TO_STRIP_PROPERTY] = "Model,Entity"
    });

    var result = ConfigurationUtilities.GetTableNameConfig(options);

    await Assert.That(result.SuffixesToStrip).Count().IsEqualTo(2);
    await Assert.That(result.SuffixesToStrip[0]).IsEqualTo("Model");
    await Assert.That(result.SuffixesToStrip[1]).IsEqualTo("Entity");
  }

  [Test]
  public async Task GetTableNameConfig_BlankSuffixList_KeepsDefaultsAsync() {
    var options = new StubOptions(new Dictionary<string, string> {
      [ConfigurationUtilities.TABLE_NAME_SUFFIXES_TO_STRIP_PROPERTY] = "   "
    });

    var result = ConfigurationUtilities.GetTableNameConfig(options);

    await Assert.That(result.SuffixesToStrip).IsEquivalentTo(TableNameConfig.Default.SuffixesToStrip);
  }

  [Test]
  public async Task GetTableNameConfig_BothProperties_AreAppliedTogetherAsync() {
    var options = new StubOptions(new Dictionary<string, string> {
      [ConfigurationUtilities.STRIP_TABLE_NAME_SUFFIXES_PROPERTY] = "false",
      [ConfigurationUtilities.TABLE_NAME_SUFFIXES_TO_STRIP_PROPERTY] = "Dto"
    });

    var result = ConfigurationUtilities.GetTableNameConfig(options);

    await Assert.That(result.StripSuffixes).IsFalse();
    await Assert.That(result.SuffixesToStrip).Count().IsEqualTo(1);
    await Assert.That(result.SuffixesToStrip[0]).IsEqualTo("Dto");
  }

  #endregion
}
