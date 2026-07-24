using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for TypeQualifications flag enum.
/// Ensures individual flags can be combined and composite presets map to correct flag combinations.
/// </summary>
public class TypeQualificationTests {
  [Test]
  public async Task TypeQualification_ComponentFlags_CanBeCombinedAsync() {
    // Arrange & Act
    var combined = TypeQualifications.Namespace | TypeQualifications.TypeName | TypeQualifications.Assembly;

    // Assert - Should have all three flags
    await Assert.That(combined.HasFlag(TypeQualifications.Namespace)).IsTrue();
    await Assert.That(combined.HasFlag(TypeQualifications.TypeName)).IsTrue();
    await Assert.That(combined.HasFlag(TypeQualifications.Assembly)).IsTrue();

    // Should NOT have other flags
    await Assert.That(combined.HasFlag(TypeQualifications.Version)).IsFalse();
    await Assert.That(combined.HasFlag(TypeQualifications.Culture)).IsFalse();
    await Assert.That(combined.HasFlag(TypeQualifications.PublicKeyToken)).IsFalse();
    await Assert.That(combined.HasFlag(TypeQualifications.GlobalPrefix)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_FullyQualified_MapToCorrectFlagsAsync() {
    // Arrange
    var expected = TypeQualifications.Namespace | TypeQualifications.TypeName | TypeQualifications.Assembly;

    // Act
    var actual = TypeQualifications.FullyQualified;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualifications.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.Assembly)).IsTrue();
  }

  [Test]
  public async Task TypeQualification_Simple_MapsToTypeNameOnlyAsync() {
    // Arrange
    var expected = TypeQualifications.TypeName;

    // Act
    var actual = TypeQualifications.Simple;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualifications.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.Namespace)).IsFalse();
    await Assert.That(actual.HasFlag(TypeQualifications.Assembly)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_NamespaceQualified_MapsToNamespaceAndTypeNameAsync() {
    // Arrange
    var expected = TypeQualifications.Namespace | TypeQualifications.TypeName;

    // Act
    var actual = TypeQualifications.NamespaceQualified;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualifications.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.Assembly)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_GlobalQualified_IncludesGlobalPrefixAsync() {
    // Arrange
    var expected = TypeQualifications.GlobalPrefix | TypeQualifications.Namespace | TypeQualifications.TypeName;

    // Act
    var actual = TypeQualifications.GlobalQualified;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualifications.GlobalPrefix)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.TypeName)).IsTrue();
  }

  [Test]
  public async Task TypeQualification_FullyQualifiedWithVersion_IncludesAllComponentsAsync() {
    // Arrange
    var expected = TypeQualifications.Namespace
                   | TypeQualifications.TypeName
                   | TypeQualifications.Assembly
                   | TypeQualifications.Version
                   | TypeQualifications.Culture
                   | TypeQualifications.PublicKeyToken;

    // Act
    var actual = TypeQualifications.FullyQualifiedWithVersion;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualifications.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.Assembly)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.Version)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.Culture)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.PublicKeyToken)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualifications.GlobalPrefix)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_IndividualFlags_HaveDistinctValuesAsync() {
    // Act - Get individual flag values
    var typeName = (int)TypeQualifications.TypeName;
    var namespaceName = (int)TypeQualifications.Namespace;
    var assembly = (int)TypeQualifications.Assembly;
    var version = (int)TypeQualifications.Version;
    var culture = (int)TypeQualifications.Culture;
    var publicKeyToken = (int)TypeQualifications.PublicKeyToken;
    var globalPrefix = (int)TypeQualifications.GlobalPrefix;

    // Assert - Each should be a power of 2 (single bit)
    await Assert.That(typeName).IsEqualTo(1);      // 1 << 0
    await Assert.That(namespaceName).IsEqualTo(2); // 1 << 1
    await Assert.That(assembly).IsEqualTo(4);      // 1 << 2
    await Assert.That(version).IsEqualTo(8);       // 1 << 3
    await Assert.That(culture).IsEqualTo(16);      // 1 << 4
    await Assert.That(publicKeyToken).IsEqualTo(32); // 1 << 5
    await Assert.That(globalPrefix).IsEqualTo(64); // 1 << 6
  }

  [Test]
  public async Task TypeQualification_None_HasValueZeroAsync() {
    // Arrange & Act
    var none = TypeQualifications.None;

    // Assert
    await Assert.That((int)none).IsEqualTo(0);
    await Assert.That(none.HasFlag(TypeQualifications.TypeName)).IsFalse();
    await Assert.That(none.HasFlag(TypeQualifications.Namespace)).IsFalse();
  }
}
