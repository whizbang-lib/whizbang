using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Schema;

namespace Whizbang.Data.Schema.Tests;

/// <summary>
/// Tests for WhizbangDataType enum - database-agnostic type system.
/// Tests verify all expected types are defined for cross-database compatibility.
/// </summary>
public class WhizbangDataTypeTests {
  [Test]
  public async Task WhizbangDataType_HasUuidTypeAsync() {
    // Arrange & Act
    var hasUuid = Enum.IsDefined(typeof(WhizbangDataType), "UUID");

    // Assert
    await Assert.That(hasUuid).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasStringTypeAsync() {
    // Arrange & Act
    var hasString = Enum.IsDefined(typeof(WhizbangDataType), "STRING");

    // Assert
    await Assert.That(hasString).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasTimestampTzTypeAsync() {
    // Arrange & Act
    var hasTimestampTz = Enum.IsDefined(typeof(WhizbangDataType), "TIMESTAMP_TZ");

    // Assert
    await Assert.That(hasTimestampTz).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasJsonTypeAsync() {
    // Arrange & Act
    var hasJson = Enum.IsDefined(typeof(WhizbangDataType), "JSON");

    // Assert
    await Assert.That(hasJson).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasBigIntTypeAsync() {
    // Arrange & Act
    var hasBigInt = Enum.IsDefined(typeof(WhizbangDataType), "BIG_INT");

    // Assert
    await Assert.That(hasBigInt).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasIntegerTypeAsync() {
    // Arrange & Act
    var hasInteger = Enum.IsDefined(typeof(WhizbangDataType), "INTEGER");

    // Assert
    await Assert.That(hasInteger).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasSmallIntTypeAsync() {
    // Arrange & Act
    var hasSmallInt = Enum.IsDefined(typeof(WhizbangDataType), "SMALL_INT");

    // Assert
    await Assert.That(hasSmallInt).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasBooleanTypeAsync() {
    // Arrange & Act
    var hasBoolean = Enum.IsDefined(typeof(WhizbangDataType), "BOOLEAN");

    // Assert
    await Assert.That(hasBoolean).IsTrue();
  }

  [Test]
  public async Task WhizbangDataType_HasExactlyEightTypesAsync() {
    // Arrange & Act - Use generic GetValues for AOT compatibility
    var typeCount = Enum.GetValues<WhizbangDataType>().Length;

    // Assert
    await Assert.That(typeCount).IsEqualTo(8);
  }

  [Test]
  public async Task WhizbangDataType_AllValuesAreUniqueAsync() {
    // Arrange - Use generic GetValues for AOT compatibility
    var values = Enum.GetValues<WhizbangDataType>().Cast<int>();

    // Act
    var distinctCount = values.Distinct().Count();
    var totalCount = values.Count();

    // Assert
    await Assert.That(distinctCount).IsEqualTo(totalCount);
  }

  [Test]
  public async Task WhizbangDataType_ToStringReturnsCorrectNamesAsync() {
    // Arrange
    var expectedNames = new[] { "UUID", "STRING", "TIMESTAMP_TZ", "JSON", "BIG_INT", "INTEGER", "SMALL_INT", "BOOLEAN" };

    // Act - Use generic GetNames for AOT compatibility
    var actualNames = Enum.GetNames<WhizbangDataType>();

    // Assert - Check each name individually for AOT compatibility
    await Assert.That(actualNames.Length).IsEqualTo(expectedNames.Length);
    foreach (var expectedName in expectedNames) {
      await Assert.That(actualNames).Contains(expectedName);
    }
  }
}
