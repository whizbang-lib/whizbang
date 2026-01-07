using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for MatchStrictness flag enum.
/// Ensures individual flags can be combined and composite presets map to correct flag combinations.
/// </summary>
public class MatchStrictnessTests {
  [Test]
  public async Task MatchStrictness_IndividualFlags_CanBeCombinedAsync() {
    // Arrange & Act
    var combined = MatchStrictness.IgnoreCase | MatchStrictness.IgnoreVersion;

    // Assert - Should have both flags
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreCase)).IsTrue();
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreVersion)).IsTrue();

    // Should NOT have other flags
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreAssembly)).IsFalse();
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreNamespace)).IsFalse();
  }

  [Test]
  public async Task MatchStrictness_SimpleName_MapsToCorrectFlagsAsync() {
    // Arrange
    var expected = MatchStrictness.IgnoreNamespace | MatchStrictness.IgnoreAssembly | MatchStrictness.IgnoreVersion;

    // Act
    var actual = MatchStrictness.SimpleName;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreNamespace)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreAssembly)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreVersion)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreCase)).IsFalse();
  }

  [Test]
  public async Task MatchStrictness_SimpleNameCaseInsensitive_IncludesIgnoreCaseAsync() {
    // Arrange
    var expected = MatchStrictness.SimpleName | MatchStrictness.IgnoreCase;

    // Act
    var actual = MatchStrictness.SimpleNameCaseInsensitive;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreNamespace)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreAssembly)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreVersion)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreCase)).IsTrue();
  }

  [Test]
  public async Task MatchStrictness_Exact_EqualsNoneAsync() {
    // Arrange
    var exact = MatchStrictness.Exact;
    var none = MatchStrictness.None;

    // Act & Assert
    await Assert.That(exact).IsEqualTo(none);
    await Assert.That((int)exact).IsEqualTo(0);
  }

  [Test]
  public async Task MatchStrictness_CaseInsensitive_EqualsIgnoreCaseAsync() {
    // Arrange
    var caseInsensitive = MatchStrictness.CaseInsensitive;
    var ignoreCase = MatchStrictness.IgnoreCase;

    // Act & Assert
    await Assert.That(caseInsensitive).IsEqualTo(ignoreCase);
  }

  [Test]
  public async Task MatchStrictness_WithoutAssembly_IncludesIgnoreVersionAsync() {
    // Arrange
    var expected = MatchStrictness.IgnoreAssembly | MatchStrictness.IgnoreVersion;

    // Act
    var actual = MatchStrictness.WithoutAssembly;

    // Assert
    await Assert.That(actual).IsEqualTo(expected);
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreAssembly)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreVersion)).IsTrue();
    await Assert.That(actual.HasFlag(MatchStrictness.IgnoreNamespace)).IsFalse();
  }

  [Test]
  public async Task MatchStrictness_IndividualFlags_HaveDistinctValuesAsync() {
    // Act - Get individual flag values
    var ignoreCase = (int)MatchStrictness.IgnoreCase;
    var ignoreVersion = (int)MatchStrictness.IgnoreVersion;
    var ignoreAssembly = (int)MatchStrictness.IgnoreAssembly;
    var ignoreNamespace = (int)MatchStrictness.IgnoreNamespace;

    // Assert - Each should be a power of 2 (single bit)
    await Assert.That(ignoreCase).IsEqualTo(1);       // 1 << 0
    await Assert.That(ignoreVersion).IsEqualTo(2);    // 1 << 1
    await Assert.That(ignoreAssembly).IsEqualTo(4);   // 1 << 2
    await Assert.That(ignoreNamespace).IsEqualTo(8);  // 1 << 3
  }

  [Test]
  public async Task MatchStrictness_None_HasValueZeroAsync() {
    // Arrange & Act
    var none = MatchStrictness.None;

    // Assert
    await Assert.That((int)none).IsEqualTo(0);
    await Assert.That(none.HasFlag(MatchStrictness.IgnoreCase)).IsFalse();
    await Assert.That(none.HasFlag(MatchStrictness.IgnoreVersion)).IsFalse();
  }

  [Test]
  public async Task MatchStrictness_MultipleFlags_CanBeCombinedArbitrarilyAsync() {
    // Arrange & Act
    var combined = MatchStrictness.IgnoreCase | MatchStrictness.IgnoreAssembly | MatchStrictness.IgnoreNamespace;

    // Assert
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreCase)).IsTrue();
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreAssembly)).IsTrue();
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreNamespace)).IsTrue();
    await Assert.That(combined.HasFlag(MatchStrictness.IgnoreVersion)).IsFalse();
  }
}
