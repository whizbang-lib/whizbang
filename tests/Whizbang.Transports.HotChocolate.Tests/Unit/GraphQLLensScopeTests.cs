using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="GraphQLLensScopes"/> flags enum.
/// Verifies composable scope flags and preset combinations.
/// </summary>
public class GraphQLLensScopeTests {
  [Test]
  public async Task Default_ShouldBeZeroAsync() {
    // Arrange & Act
    var scope = GraphQLLensScopes.None;

    // Assert
    await Assert.That((int)scope).IsEqualTo(0);
  }

  [Test]
  public async Task Data_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScopes.Data;

    // Assert
    await Assert.That((int)scope).IsEqualTo(1);
  }

  [Test]
  public async Task Metadata_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScopes.Metadata;

    // Assert
    await Assert.That((int)scope).IsEqualTo(2);
  }

  [Test]
  public async Task Scope_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScopes.Scope;

    // Assert
    await Assert.That((int)scope).IsEqualTo(4);
  }

  [Test]
  public async Task SystemFields_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScopes.SystemFields;

    // Assert
    await Assert.That((int)scope).IsEqualTo(8);
  }

  [Test]
  public async Task DataOnly_ShouldEqualDataAsync() {
    // Arrange & Act
    var dataOnly = GraphQLLensScopes.DataOnly;
    var data = GraphQLLensScopes.Data;

    // Assert
    await Assert.That(dataOnly).IsEqualTo(data);
  }

  [Test]
  public async Task NoData_ShouldBeMetadataPlusScopePlusSystemFieldsAsync() {
    // Arrange
    var expected = GraphQLLensScopes.Metadata | GraphQLLensScopes.Scope | GraphQLLensScopes.SystemFields;
    var noData = GraphQLLensScopes.NoData;

    // Act & Assert
    await Assert.That(noData).IsEqualTo(expected);
  }

  [Test]
  public async Task All_ShouldIncludeAllComponentsAsync() {
    // Arrange
    var expected = GraphQLLensScopes.Data | GraphQLLensScopes.Metadata | GraphQLLensScopes.Scope | GraphQLLensScopes.SystemFields;
    var all = GraphQLLensScopes.All;

    // Act & Assert
    await Assert.That(all).IsEqualTo(expected);
  }

  [Test]
  public async Task Flags_ShouldBeComposableAsync() {
    // Arrange & Act
    var combined = GraphQLLensScopes.Data | GraphQLLensScopes.Metadata;
    var hasData = combined.HasFlag(GraphQLLensScopes.Data);
    var hasMetadata = combined.HasFlag(GraphQLLensScopes.Metadata);
    var hasScope = combined.HasFlag(GraphQLLensScopes.Scope);
    var hasSystemFields = combined.HasFlag(GraphQLLensScopes.SystemFields);

    // Assert
    await Assert.That(hasData).IsTrue();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasScope).IsFalse();
    await Assert.That(hasSystemFields).IsFalse();
  }

  [Test]
  public async Task CustomCombination_DataPlusSystemFields_ShouldWorkAsync() {
    // Arrange & Act
    var combined = GraphQLLensScopes.Data | GraphQLLensScopes.SystemFields;
    var intValue = (int)combined;
    var hasData = combined.HasFlag(GraphQLLensScopes.Data);
    var hasSystemFields = combined.HasFlag(GraphQLLensScopes.SystemFields);
    var hasMetadata = combined.HasFlag(GraphQLLensScopes.Metadata);
    var hasScope = combined.HasFlag(GraphQLLensScopes.Scope);

    // Assert - should have Data (1) and SystemFields (8) = 9
    await Assert.That(intValue).IsEqualTo(9);
    await Assert.That(hasData).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
    await Assert.That(hasMetadata).IsFalse();
    await Assert.That(hasScope).IsFalse();
  }

  [Test]
  public async Task NoData_ShouldNotIncludeDataAsync() {
    // Arrange & Act
    var noData = GraphQLLensScopes.NoData;
    var hasData = noData.HasFlag(GraphQLLensScopes.Data);
    var hasMetadata = noData.HasFlag(GraphQLLensScopes.Metadata);
    var hasScope = noData.HasFlag(GraphQLLensScopes.Scope);
    var hasSystemFields = noData.HasFlag(GraphQLLensScopes.SystemFields);

    // Assert
    await Assert.That(hasData).IsFalse();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasScope).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
  }

  [Test]
  public async Task All_ShouldIncludeDataAsync() {
    // Arrange & Act
    var all = GraphQLLensScopes.All;
    var hasData = all.HasFlag(GraphQLLensScopes.Data);

    // Assert
    await Assert.That(hasData).IsTrue();
  }

  [Test]
  public async Task Default_ShouldNotHaveAnyFlagsAsync() {
    // Arrange & Act
    var defaultScope = GraphQLLensScopes.None;
    var hasData = defaultScope.HasFlag(GraphQLLensScopes.Data);
    var hasMetadata = defaultScope.HasFlag(GraphQLLensScopes.Metadata);
    var hasScope = defaultScope.HasFlag(GraphQLLensScopes.Scope);
    var hasSystemFields = defaultScope.HasFlag(GraphQLLensScopes.SystemFields);

    // Assert - Default (0) should not have any individual flags
    await Assert.That(hasData).IsFalse();
    await Assert.That(hasMetadata).IsFalse();
    await Assert.That(hasScope).IsFalse();
    await Assert.That(hasSystemFields).IsFalse();
  }

  [Test]
  public async Task BitwiseOr_AllCombinations_ShouldProduceCorrectValuesAsync() {
    // Test all possible pairs
    var dataMetadata = (int)(GraphQLLensScopes.Data | GraphQLLensScopes.Metadata);
    var dataScope = (int)(GraphQLLensScopes.Data | GraphQLLensScopes.Scope);
    var dataSystemFields = (int)(GraphQLLensScopes.Data | GraphQLLensScopes.SystemFields);
    var metadataScope = (int)(GraphQLLensScopes.Metadata | GraphQLLensScopes.Scope);
    var metadataSystemFields = (int)(GraphQLLensScopes.Metadata | GraphQLLensScopes.SystemFields);
    var scopeSystemFields = (int)(GraphQLLensScopes.Scope | GraphQLLensScopes.SystemFields);

    await Assert.That(dataMetadata).IsEqualTo(3);
    await Assert.That(dataScope).IsEqualTo(5);
    await Assert.That(dataSystemFields).IsEqualTo(9);
    await Assert.That(metadataScope).IsEqualTo(6);
    await Assert.That(metadataSystemFields).IsEqualTo(10);
    await Assert.That(scopeSystemFields).IsEqualTo(12);
  }

  [Test]
  public async Task BitwiseAnd_ShouldExtractFlagsAsync() {
    // Arrange
    var combined = GraphQLLensScopes.Data | GraphQLLensScopes.Metadata;

    // Act & Assert
    var extractedData = (combined & GraphQLLensScopes.Data) == GraphQLLensScopes.Data;
    var extractedMetadata = (combined & GraphQLLensScopes.Metadata) == GraphQLLensScopes.Metadata;
    var extractedScope = (combined & GraphQLLensScopes.Scope) == GraphQLLensScopes.None;

    await Assert.That(extractedData).IsTrue();
    await Assert.That(extractedMetadata).IsTrue();
    await Assert.That(extractedScope).IsTrue();
  }

  [Test]
  public async Task FlagsAttribute_ShouldBeAppliedAsync() {
    // Arrange
    var type = typeof(GraphQLLensScopes);

    // Act
    var attributes = type.GetCustomAttributes(typeof(FlagsAttribute), false);
    var hasFlags = attributes.Length > 0;

    // Assert
    await Assert.That(hasFlags).IsTrue();
  }

  [Test]
  public async Task AllValues_ShouldBePowersOfTwoOrZeroAsync() {
    // Arrange - primitive flags should be 0 or powers of 2
    var primitiveFlags = new[] {
      GraphQLLensScopes.None,
      GraphQLLensScopes.Data,
      GraphQLLensScopes.Metadata,
      GraphQLLensScopes.Scope,
      GraphQLLensScopes.SystemFields
    };

    // Assert
    foreach (var flag in primitiveFlags) {
      var intValue = (int)flag;
      var isPowerOfTwoOrZero = intValue == 0 || (intValue & (intValue - 1)) == 0;
      await Assert.That(isPowerOfTwoOrZero).IsTrue();
    }
  }
}
