using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for <see cref="FieldStorageMode"/> enum.
/// Validates enum values and their integer representations.
/// </summary>
/// <docs>perspectives/physical-fields</docs>
[Category("Core")]
[Category("Enums")]
[Category("PhysicalFields")]
public class FieldStorageModeTests {
  [Test]
  public async Task FieldStorageMode_JsonOnly_HasValueZeroAsync() {
    // Arrange
    var jsonOnlyValue = (int)FieldStorageMode.JsonOnly;

    // Assert - JsonOnly is default, should be 0
    await Assert.That(jsonOnlyValue).IsEqualTo(0);
  }

  [Test]
  public async Task FieldStorageMode_Extracted_HasValueOneAsync() {
    // Arrange
    var extractedValue = (int)FieldStorageMode.Extracted;

    // Assert
    await Assert.That(extractedValue).IsEqualTo(1);
  }

  [Test]
  public async Task FieldStorageMode_Split_HasValueTwoAsync() {
    // Arrange
    var splitValue = (int)FieldStorageMode.Split;

    // Assert
    await Assert.That(splitValue).IsEqualTo(2);
  }

  [Test]
  public async Task FieldStorageMode_Default_IsJsonOnlyAsync() {
    // Arrange
    FieldStorageMode defaultValue = default;

    // Assert - default value should be JsonOnly (0)
    await Assert.That(defaultValue).IsEqualTo(FieldStorageMode.JsonOnly);
  }

  [Test]
  public async Task FieldStorageMode_HasExactlyThreeValuesAsync() {
    // Arrange
    var values = Enum.GetValues<FieldStorageMode>();

    // Assert
    await Assert.That(values).Count().IsEqualTo(3);
  }

  [Test]
  public async Task FieldStorageMode_AllValuesAreParsableAsync() {
    // Arrange & Act
    var jsonOnlyParsed = Enum.TryParse<FieldStorageMode>("JsonOnly", out var jsonOnly);
    var extractedParsed = Enum.TryParse<FieldStorageMode>("Extracted", out var extracted);
    var splitParsed = Enum.TryParse<FieldStorageMode>("Split", out var split);

    // Assert
    await Assert.That(jsonOnlyParsed).IsTrue();
    await Assert.That(extractedParsed).IsTrue();
    await Assert.That(splitParsed).IsTrue();
    await Assert.That(jsonOnly).IsEqualTo(FieldStorageMode.JsonOnly);
    await Assert.That(extracted).IsEqualTo(FieldStorageMode.Extracted);
    await Assert.That(split).IsEqualTo(FieldStorageMode.Split);
  }
}
