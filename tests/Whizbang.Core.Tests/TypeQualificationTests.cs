using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for TypeQualification flag enum.
/// Ensures individual flags can be combined and composite presets map to correct flag combinations.
/// </summary>
public class TypeQualificationTests {
  [Test]
  public async Task TypeQualification_ComponentFlags_CanBeCombinedAsync() {
    // Arrange & Act
    var combined = TypeQualification.Namespace | TypeQualification.TypeName | TypeQualification.Assembly;

    // Assert - Should have all three flags
    await Assert.That(combined.HasFlag(TypeQualification.Namespace)).IsTrue();
    await Assert.That(combined.HasFlag(TypeQualification.TypeName)).IsTrue();
    await Assert.That(combined.HasFlag(TypeQualification.Assembly)).IsTrue();

    // Should NOT have other flags
    await Assert.That(combined.HasFlag(TypeQualification.Version)).IsFalse();
    await Assert.That(combined.HasFlag(TypeQualification.Culture)).IsFalse();
    await Assert.That(combined.HasFlag(TypeQualification.PublicKeyToken)).IsFalse();
    await Assert.That(combined.HasFlag(TypeQualification.GlobalPrefix)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_FullyQualified_MapToCorrectFlagsAsync() {
    // Arrange
    var expected = TypeQualification.Namespace | TypeQualification.TypeName | TypeQualification.Assembly;

    // Act
    var actual = TypeQualification.FullyQualified;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualification.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.Assembly)).IsTrue();
  }

  [Test]
  public async Task TypeQualification_Simple_MapsToTypeNameOnlyAsync() {
    // Arrange
    var expected = TypeQualification.TypeName;

    // Act
    var actual = TypeQualification.Simple;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualification.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.Namespace)).IsFalse();
    await Assert.That(actual.HasFlag(TypeQualification.Assembly)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_NamespaceQualified_MapsToNamespaceAndTypeNameAsync() {
    // Arrange
    var expected = TypeQualification.Namespace | TypeQualification.TypeName;

    // Act
    var actual = TypeQualification.NamespaceQualified;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualification.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.Assembly)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_GlobalQualified_IncludesGlobalPrefixAsync() {
    // Arrange
    var expected = TypeQualification.GlobalPrefix | TypeQualification.Namespace | TypeQualification.TypeName;

    // Act
    var actual = TypeQualification.GlobalQualified;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualification.GlobalPrefix)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.TypeName)).IsTrue();
  }

  [Test]
  public async Task TypeQualification_FullyQualifiedWithVersion_IncludesAllComponentsAsync() {
    // Arrange
    var expected = TypeQualification.Namespace
                   | TypeQualification.TypeName
                   | TypeQualification.Assembly
                   | TypeQualification.Version
                   | TypeQualification.Culture
                   | TypeQualification.PublicKeyToken;

    // Act
    var actual = TypeQualification.FullyQualifiedWithVersion;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(TypeQualification.Namespace)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.TypeName)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.Assembly)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.Version)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.Culture)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.PublicKeyToken)).IsTrue();
    await Assert.That(actual.HasFlag(TypeQualification.GlobalPrefix)).IsFalse();
  }

  [Test]
  public async Task TypeQualification_IndividualFlags_HaveDistinctValuesAsync() {
    // Act - Get individual flag values
    var typeName = (int)TypeQualification.TypeName;
    var namespaceName = (int)TypeQualification.Namespace;
    var assembly = (int)TypeQualification.Assembly;
    var version = (int)TypeQualification.Version;
    var culture = (int)TypeQualification.Culture;
    var publicKeyToken = (int)TypeQualification.PublicKeyToken;
    var globalPrefix = (int)TypeQualification.GlobalPrefix;

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
    var none = TypeQualification.None;

    // Assert
    await Assert.That((int)none).IsEqualTo(0);
    await Assert.That(none.HasFlag(TypeQualification.TypeName)).IsFalse();
    await Assert.That(none.HasFlag(TypeQualification.Namespace)).IsFalse();
  }
}
