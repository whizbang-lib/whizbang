using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="GraphQLLensScope"/> flags enum.
/// Verifies composable scope flags and preset combinations.
/// </summary>
public class GraphQLLensScopeTests {
  [Test]
  public async Task Default_ShouldBeZeroAsync() {
    // Arrange & Act
    var scope = GraphQLLensScope.Default;

    // Assert
    await Assert.That((int)scope).IsEqualTo(0);
  }

  [Test]
  public async Task Data_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScope.Data;

    // Assert
    await Assert.That((int)scope).IsEqualTo(1);
  }

  [Test]
  public async Task Metadata_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScope.Metadata;

    // Assert
    await Assert.That((int)scope).IsEqualTo(2);
  }

  [Test]
  public async Task Scope_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScope.Scope;

    // Assert
    await Assert.That((int)scope).IsEqualTo(4);
  }

  [Test]
  public async Task SystemFields_ShouldBeBitFlagAsync() {
    // Arrange & Act
    var scope = GraphQLLensScope.SystemFields;

    // Assert
    await Assert.That((int)scope).IsEqualTo(8);
  }

  [Test]
  public async Task DataOnly_ShouldEqualDataAsync() {
    // Arrange & Act
    var dataOnly = GraphQLLensScope.DataOnly;
    var data = GraphQLLensScope.Data;

    // Assert
    await Assert.That(dataOnly).IsEqualTo(data);
  }

  [Test]
  public async Task NoData_ShouldBeMetadataPlusScopePlusSystemFieldsAsync() {
    // Arrange
    var expected = GraphQLLensScope.Metadata | GraphQLLensScope.Scope | GraphQLLensScope.SystemFields;
    var noData = GraphQLLensScope.NoData;

    // Act & Assert
    await Assert.That(noData).IsEqualTo(expected);
  }

  [Test]
  public async Task All_ShouldIncludeAllComponentsAsync() {
    // Arrange
    var expected = GraphQLLensScope.Data | GraphQLLensScope.Metadata | GraphQLLensScope.Scope | GraphQLLensScope.SystemFields;
    var all = GraphQLLensScope.All;

    // Act & Assert
    await Assert.That(all).IsEqualTo(expected);
  }

  [Test]
  public async Task Flags_ShouldBeComposableAsync() {
    // Arrange & Act
    var combined = GraphQLLensScope.Data | GraphQLLensScope.Metadata;
    var hasData = combined.HasFlag(GraphQLLensScope.Data);
    var hasMetadata = combined.HasFlag(GraphQLLensScope.Metadata);
    var hasScope = combined.HasFlag(GraphQLLensScope.Scope);
    var hasSystemFields = combined.HasFlag(GraphQLLensScope.SystemFields);

    // Assert
    await Assert.That(hasData).IsTrue();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasScope).IsFalse();
    await Assert.That(hasSystemFields).IsFalse();
  }

  [Test]
  public async Task CustomCombination_DataPlusSystemFields_ShouldWorkAsync() {
    // Arrange & Act
    var combined = GraphQLLensScope.Data | GraphQLLensScope.SystemFields;
    var intValue = (int)combined;
    var hasData = combined.HasFlag(GraphQLLensScope.Data);
    var hasSystemFields = combined.HasFlag(GraphQLLensScope.SystemFields);
    var hasMetadata = combined.HasFlag(GraphQLLensScope.Metadata);
    var hasScope = combined.HasFlag(GraphQLLensScope.Scope);

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
    var noData = GraphQLLensScope.NoData;
    var hasData = noData.HasFlag(GraphQLLensScope.Data);
    var hasMetadata = noData.HasFlag(GraphQLLensScope.Metadata);
    var hasScope = noData.HasFlag(GraphQLLensScope.Scope);
    var hasSystemFields = noData.HasFlag(GraphQLLensScope.SystemFields);

    // Assert
    await Assert.That(hasData).IsFalse();
    await Assert.That(hasMetadata).IsTrue();
    await Assert.That(hasScope).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
  }

  [Test]
  public async Task All_ShouldIncludeDataAsync() {
    // Arrange & Act
    var all = GraphQLLensScope.All;
    var hasData = all.HasFlag(GraphQLLensScope.Data);

    // Assert
    await Assert.That(hasData).IsTrue();
  }

  [Test]
  public async Task Default_ShouldNotHaveAnyFlagsAsync() {
    // Arrange & Act
    var defaultScope = GraphQLLensScope.Default;
    var hasData = defaultScope.HasFlag(GraphQLLensScope.Data);
    var hasMetadata = defaultScope.HasFlag(GraphQLLensScope.Metadata);
    var hasScope = defaultScope.HasFlag(GraphQLLensScope.Scope);
    var hasSystemFields = defaultScope.HasFlag(GraphQLLensScope.SystemFields);

    // Assert - Default (0) should not have any individual flags
    await Assert.That(hasData).IsFalse();
    await Assert.That(hasMetadata).IsFalse();
    await Assert.That(hasScope).IsFalse();
    await Assert.That(hasSystemFields).IsFalse();
  }

  [Test]
  public async Task BitwiseOr_AllCombinations_ShouldProduceCorrectValuesAsync() {
    // Test all possible pairs
    var dataMetadata = (int)(GraphQLLensScope.Data | GraphQLLensScope.Metadata);
    var dataScope = (int)(GraphQLLensScope.Data | GraphQLLensScope.Scope);
    var dataSystemFields = (int)(GraphQLLensScope.Data | GraphQLLensScope.SystemFields);
    var metadataScope = (int)(GraphQLLensScope.Metadata | GraphQLLensScope.Scope);
    var metadataSystemFields = (int)(GraphQLLensScope.Metadata | GraphQLLensScope.SystemFields);
    var scopeSystemFields = (int)(GraphQLLensScope.Scope | GraphQLLensScope.SystemFields);

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
    var combined = GraphQLLensScope.Data | GraphQLLensScope.Metadata;

    // Act & Assert
    var extractedData = (combined & GraphQLLensScope.Data) == GraphQLLensScope.Data;
    var extractedMetadata = (combined & GraphQLLensScope.Metadata) == GraphQLLensScope.Metadata;
    var extractedScope = (combined & GraphQLLensScope.Scope) == GraphQLLensScope.Default;

    await Assert.That(extractedData).IsTrue();
    await Assert.That(extractedMetadata).IsTrue();
    await Assert.That(extractedScope).IsTrue();
  }

  [Test]
  public async Task FlagsAttribute_ShouldBeAppliedAsync() {
    // Arrange
    var type = typeof(GraphQLLensScope);

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
      GraphQLLensScope.Default,
      GraphQLLensScope.Data,
      GraphQLLensScope.Metadata,
      GraphQLLensScope.Scope,
      GraphQLLensScope.SystemFields
    };

    // Assert
    foreach (var flag in primitiveFlags) {
      var intValue = (int)flag;
      var isPowerOfTwoOrZero = intValue == 0 || (intValue & (intValue - 1)) == 0;
      await Assert.That(isPowerOfTwoOrZero).IsTrue();
    }
  }
}
