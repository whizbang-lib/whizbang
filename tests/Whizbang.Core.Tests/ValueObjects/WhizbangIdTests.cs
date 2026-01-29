using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for IWhizbangId interface and WhizbangId struct.
/// WhizbangId is a generic implementation of IWhizbangId using TrackedGuid.
/// </summary>
[Category("ValueObjects")]
public class WhizbangIdTests {
  // ========================================
  // WhizbangId.New() Tests
  // ========================================

  [Test]
  public async Task WhizbangId_New_UsesTrackedGuidNewMedoAsync() {
    // Act
    var id = WhizbangId.New();

    // Assert - Should use Medo internally
    await Assert.That(id.SubMillisecondPrecision).IsTrue();
  }

  [Test]
  public async Task WhizbangId_New_IsTimeOrdered_ReturnsTrueAsync() {
    // Act
    var id = WhizbangId.New();

    // Assert
    await Assert.That(id.IsTimeOrdered).IsTrue();
  }

  [Test]
  public async Task WhizbangId_New_SubMillisecondPrecision_ReturnsTrueAsync() {
    // Act
    var id = WhizbangId.New();

    // Assert
    await Assert.That(id.SubMillisecondPrecision).IsTrue();
  }

  [Test]
  public async Task WhizbangId_New_Timestamp_ReturnsRecentTimeAsync() {
    // Arrange
    var before = DateTimeOffset.UtcNow.AddSeconds(-1);

    // Act
    var id = WhizbangId.New();
    var after = DateTimeOffset.UtcNow.AddSeconds(1);

    // Assert
    await Assert.That(id.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(id.Timestamp).IsLessThanOrEqualTo(after);
  }

  // ========================================
  // WhizbangId.From() Tests
  // ========================================

  [Test]
  public async Task WhizbangId_From_WithTrackedGuid_PreservesMetadataAsync() {
    // Arrange
    var tracked = TrackedGuid.NewMedo();

    // Act
    var id = WhizbangId.From(tracked);

    // Assert - Metadata should be preserved
    await Assert.That(id.SubMillisecondPrecision).IsTrue();
    await Assert.That(id.IsTimeOrdered).IsTrue();
    await Assert.That(id.ToGuid()).IsEqualTo(tracked.Value);
  }

  [Test]
  public async Task WhizbangId_From_WithNonV7Guid_ThrowsAsync() {
    // Arrange
    var v4Guid = Guid.NewGuid(); // v4 is not time-ordered

    // Act & Assert
    var exception = await Assert.That(() => WhizbangId.From(v4Guid))
        .ThrowsExactly<ArgumentException>();
    await Assert.That(exception!.Message).Contains("UUIDv7");
  }

  [Test]
  public async Task WhizbangId_From_WithV7Guid_SucceedsAsync() {
    // Arrange
    var v7Guid = Guid.CreateVersion7();

    // Act
    var id = WhizbangId.From(v7Guid);

    // Assert
    await Assert.That(id.ToGuid()).IsEqualTo(v7Guid);
    await Assert.That(id.IsTimeOrdered).IsTrue();
  }

  [Test]
  public async Task WhizbangId_From_WithMicrosoftV7_HasNoSubMillisecondPrecisionAsync() {
    // Arrange - Microsoft's v7 only has millisecond precision
    var v7Guid = Guid.CreateVersion7();

    // Act
    var id = WhizbangId.From(v7Guid);

    // Assert - Should detect it's external/parsed, not Medo
    await Assert.That(id.SubMillisecondPrecision).IsFalse();
  }

  // ========================================
  // IWhizbangId Interface Tests
  // ========================================

  [Test]
  public async Task WhizbangId_ImplementsIWhizbangIdAsync() {
    // Act
    var id = WhizbangId.New();

    // Assert - Should implement the interface (check via type)
    await Assert.That(typeof(IWhizbangId).IsAssignableFrom(typeof(WhizbangId))).IsTrue();
    await Assert.That(id.ToGuid()).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task IWhizbangId_ToGuid_ReturnsUnderlyingValueAsync() {
    // Arrange
    var id = WhizbangId.New();
    var result = _accessViaInterface(id);

    // Assert
    await Assert.That(result).IsEqualTo(id.ToGuid());
    await Assert.That(result.Version).IsEqualTo(7);
  }

  // Helper to access via interface to test interface implementation
  // Suppressing CA1859 as we intentionally want to test the interface
#pragma warning disable CA1859
  private static Guid _accessViaInterface(IWhizbangId whizbangId) => whizbangId.ToGuid();
#pragma warning restore CA1859

  // ========================================
  // Equality Tests
  // ========================================

  [Test]
  public async Task WhizbangId_Equals_WithSameGuid_ReturnsTrueAsync() {
    // Arrange
    var v7Guid = Guid.CreateVersion7();
    var id1 = WhizbangId.From(v7Guid);
    var id2 = WhizbangId.From(v7Guid);

    // Act & Assert
    await Assert.That(id1.Equals(id2)).IsTrue();
    await Assert.That(id1 == id2).IsTrue();
  }

  [Test]
  public async Task WhizbangId_Equals_WithDifferentGuid_ReturnsFalseAsync() {
    // Arrange
    var id1 = WhizbangId.New();
    var id2 = WhizbangId.New();

    // Act & Assert
    await Assert.That(id1.Equals(id2)).IsFalse();
    await Assert.That(id1 != id2).IsTrue();
  }

  [Test]
  public async Task WhizbangId_GetHashCode_WithSameGuid_ReturnsSameHashAsync() {
    // Arrange
    var v7Guid = Guid.CreateVersion7();
    var id1 = WhizbangId.From(v7Guid);
    var id2 = WhizbangId.From(v7Guid);

    // Act & Assert
    await Assert.That(id1.GetHashCode()).IsEqualTo(id2.GetHashCode());
  }

  // ========================================
  // Comparison Tests
  // ========================================

  [Test]
  public async Task WhizbangId_CompareTo_OrdersChronologicallyAsync() {
    // Arrange
    var earlier = WhizbangId.New();
    await Task.Delay(10);
    var later = WhizbangId.New();

    // Act & Assert
    await Assert.That(earlier.CompareTo(later)).IsLessThan(0);
    await Assert.That(later.CompareTo(earlier)).IsGreaterThan(0);
  }

  [Test]
  public async Task WhizbangId_ComparisonOperators_WorkCorrectlyAsync() {
    // Arrange
    var earlier = WhizbangId.New();
    await Task.Delay(10);
    var later = WhizbangId.New();

    // Act & Assert
    await Assert.That(earlier < later).IsTrue();
    await Assert.That(earlier <= later).IsTrue();
    await Assert.That(later > earlier).IsTrue();
    await Assert.That(later >= earlier).IsTrue();
  }

  // ========================================
  // ToString Tests
  // ========================================

  [Test]
  public async Task WhizbangId_ToString_ReturnsGuidStringAsync() {
    // Arrange
    var id = WhizbangId.New();

    // Act
    var stringValue = id.ToString();

    // Assert
    await Assert.That(stringValue).IsEqualTo(id.ToGuid().ToString());
  }

  // ========================================
  // Default Value Tests
  // ========================================

  [Test]
  public async Task WhizbangId_Default_HasEmptyGuidAsync() {
    // Arrange
    var defaultId = default(WhizbangId);

    // Assert
    await Assert.That(defaultId.ToGuid()).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task WhizbangId_Empty_HasEmptyGuidAsync() {
    // Arrange & Act
    var empty = WhizbangId.Empty;

    // Assert
    await Assert.That(empty.ToGuid()).IsEqualTo(Guid.Empty);
  }
}
