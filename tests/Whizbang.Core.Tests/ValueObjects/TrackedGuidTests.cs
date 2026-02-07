using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for TrackedGuid implementation.
/// TrackedGuid wraps Guid with metadata tracking version and source for UUID generation provenance.
/// </summary>
[Category("ValueObjects")]
public class TrackedGuidTests {
  // ========================================
  // GuidMetadata Flags Tests
  // ========================================

  [Test]
  [Arguments(GuidMetadata.Version4, 0)]
  [Arguments(GuidMetadata.Version7, 1)]
  [Arguments(GuidMetadata.SourceMedo, 2)]
  [Arguments(GuidMetadata.SourceMicrosoft, 3)]
  [Arguments(GuidMetadata.SourceParsed, 4)]
  [Arguments(GuidMetadata.SourceExternal, 5)]
  [Arguments(GuidMetadata.SourceUnknown, 6)]
  public async Task GuidMetadata_Flags_HaveCorrectBitPositionsAsync(GuidMetadata flag, int bitPosition) {
    await Assert.That((byte)flag).IsEqualTo((byte)(1 << bitPosition));
  }

  [Test]
  public async Task GuidMetadata_CombineFlags_WorksCorrectlyAsync() {
    // Arrange & Act
    var combined = GuidMetadata.Version7 | GuidMetadata.SourceMedo;

    // Assert - both bits should be set
    await Assert.That((combined & GuidMetadata.Version7) != 0).IsTrue();
    await Assert.That((combined & GuidMetadata.SourceMedo) != 0).IsTrue();
    await Assert.That((combined & GuidMetadata.Version4) != 0).IsFalse();
  }

  // ========================================
  // TrackedGuid.NewMedo() Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_NewMedo_IsTimeOrdered_ReturnsTrueAsync() {
    // Act
    var tracked = TrackedGuid.NewMedo();

    // Assert
    await Assert.That(tracked.IsTimeOrdered).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_NewMedo_SubMillisecondPrecision_ReturnsTrueAsync() {
    // Act
    var tracked = TrackedGuid.NewMedo();

    // Assert
    await Assert.That(tracked.SubMillisecondPrecision).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_NewMedo_Timestamp_ReturnsRecentTimeAsync() {
    // Arrange
    var before = DateTimeOffset.UtcNow.AddSeconds(-1);

    // Act
    var tracked = TrackedGuid.NewMedo();
    var after = DateTimeOffset.UtcNow.AddSeconds(1);

    // Assert
    await Assert.That(tracked.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(tracked.Timestamp).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task TrackedGuid_NewMedo_Version_Returns7Async() {
    // Act
    var tracked = TrackedGuid.NewMedo();

    // Assert - the underlying Guid should have version 7
    await Assert.That(tracked.Value.Version).IsEqualTo(7);
  }

  [Test]
  public async Task TrackedGuid_NewMedo_HasSourceMedoMetadataAsync() {
    // Act
    var tracked = TrackedGuid.NewMedo();

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.SourceMedo) != 0).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.Version7) != 0).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_NewMedo_MultipleIds_AreUniqueAsync() {
    // Act
    var ids = Enumerable.Range(0, 100).Select(_ => TrackedGuid.NewMedo()).ToList();

    // Assert
    var distinctCount = ids.Select(t => t.Value).Distinct().Count();
    await Assert.That(distinctCount).IsEqualTo(100);
  }

  [Test]
  public async Task TrackedGuid_NewMedo_MultipleIds_AreTimeOrderedAsync() {
    // Act - Generate several IDs rapidly
    var ids = Enumerable.Range(0, 10).Select(_ => TrackedGuid.NewMedo()).ToList();

    // Assert - Each subsequent ID should be >= previous when compared
    for (int i = 1; i < ids.Count; i++) {
      await Assert.That(ids[i].CompareTo(ids[i - 1])).IsGreaterThanOrEqualTo(0);
    }
  }

  // ========================================
  // TrackedGuid.NewMicrosoftV7() Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_NewMicrosoftV7_IsTimeOrdered_ReturnsTrueAsync() {
    // Act
    var tracked = TrackedGuid.NewMicrosoftV7();

    // Assert
    await Assert.That(tracked.IsTimeOrdered).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_NewMicrosoftV7_SubMillisecondPrecision_ReturnsFalseAsync() {
    // Act
    var tracked = TrackedGuid.NewMicrosoftV7();

    // Assert - Microsoft's implementation only has millisecond precision
    await Assert.That(tracked.SubMillisecondPrecision).IsFalse();
  }

  [Test]
  public async Task TrackedGuid_NewMicrosoftV7_Version_Returns7Async() {
    // Act
    var tracked = TrackedGuid.NewMicrosoftV7();

    // Assert
    await Assert.That(tracked.Value.Version).IsEqualTo(7);
  }

  [Test]
  public async Task TrackedGuid_NewMicrosoftV7_HasSourceMicrosoftMetadataAsync() {
    // Act
    var tracked = TrackedGuid.NewMicrosoftV7();

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.SourceMicrosoft) != 0).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.Version7) != 0).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.SourceMedo) != 0).IsFalse();
  }

  // ========================================
  // TrackedGuid.NewRandom() Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_NewRandom_IsTimeOrdered_ReturnsFalseAsync() {
    // Act
    var tracked = TrackedGuid.NewRandom();

    // Assert
    await Assert.That(tracked.IsTimeOrdered).IsFalse();
  }

  [Test]
  public async Task TrackedGuid_NewRandom_Version_Returns4Async() {
    // Act
    var tracked = TrackedGuid.NewRandom();

    // Assert
    await Assert.That(tracked.Value.Version).IsEqualTo(4);
  }

  [Test]
  public async Task TrackedGuid_NewRandom_SubMillisecondPrecision_ReturnsFalseAsync() {
    // Act
    var tracked = TrackedGuid.NewRandom();

    // Assert - v4 has no timestamp, so no precision
    await Assert.That(tracked.SubMillisecondPrecision).IsFalse();
  }

  [Test]
  public async Task TrackedGuid_NewRandom_HasVersion4MetadataAsync() {
    // Act
    var tracked = TrackedGuid.NewRandom();

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.Version4) != 0).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.SourceMicrosoft) != 0).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.Version7) != 0).IsFalse();
  }

  // ========================================
  // TrackedGuid.Parse() Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_Parse_MarksAsSourceParsedAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var guidString = guid.ToString();

    // Act
    var tracked = TrackedGuid.Parse(guidString);

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.SourceParsed) != 0).IsTrue();
    await Assert.That(tracked.Value).IsEqualTo(guid);
  }

  [Test]
  public async Task TrackedGuid_Parse_WithV7Guid_SetsVersion7MetadataAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var guidString = guid.ToString();

    // Act
    var tracked = TrackedGuid.Parse(guidString);

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.Version7) != 0).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.Version4) != 0).IsFalse();
  }

  [Test]
  public async Task TrackedGuid_Parse_WithV4Guid_SetsVersion4MetadataAsync() {
    // Arrange
    var guid = Guid.NewGuid();
    var guidString = guid.ToString();

    // Act
    var tracked = TrackedGuid.Parse(guidString);

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.Version4) != 0).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.Version7) != 0).IsFalse();
  }

  [Test]
  public async Task TrackedGuid_TryParse_WithValidGuid_ReturnsTrueAsync() {
    // Arrange
    var guidString = Guid.CreateVersion7().ToString();

    // Act
    var success = TrackedGuid.TryParse(guidString, out var tracked);

    // Assert
    await Assert.That(success).IsTrue();
    await Assert.That((tracked.Metadata & GuidMetadata.SourceParsed) != 0).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_TryParse_WithInvalidGuid_ReturnsFalseAsync() {
    // Arrange
    var invalidString = "not-a-guid";

    // Act
    var success = TrackedGuid.TryParse(invalidString, out var tracked);

    // Assert
    await Assert.That(success).IsFalse();
    await Assert.That(tracked).IsEqualTo(default(TrackedGuid));
  }

  // ========================================
  // TrackedGuid.FromExternal() Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_FromExternal_MarksAsSourceExternalAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();

    // Act
    var tracked = TrackedGuid.FromExternal(guid);

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.SourceExternal) != 0).IsTrue();
    await Assert.That(tracked.Value).IsEqualTo(guid);
  }

  [Test]
  public async Task TrackedGuid_FromExternal_DetectsVersionAsync() {
    // Arrange
    var v7Guid = Guid.CreateVersion7();
    var v4Guid = Guid.NewGuid();

    // Act
    var trackedV7 = TrackedGuid.FromExternal(v7Guid);
    var trackedV4 = TrackedGuid.FromExternal(v4Guid);

    // Assert
    await Assert.That((trackedV7.Metadata & GuidMetadata.Version7) != 0).IsTrue();
    await Assert.That((trackedV4.Metadata & GuidMetadata.Version4) != 0).IsTrue();
  }

  // ========================================
  // Implicit/Explicit Conversion Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_ImplicitToGuid_ReturnsUnderlyingValueAsync() {
    // Arrange
    var tracked = TrackedGuid.NewMedo();
    var expectedGuid = tracked.Value;

    // Act
    Guid implicitGuid = tracked;

    // Assert
    await Assert.That(implicitGuid).IsEqualTo(expectedGuid);
  }

  [Test]
  public async Task TrackedGuid_ImplicitFromGuid_MarksAsSourceUnknownAsync() {
    // Arrange
    Guid rawGuid = Guid.CreateVersion7();

    // Act
    TrackedGuid tracked = rawGuid;

    // Assert
    await Assert.That((tracked.Metadata & GuidMetadata.SourceUnknown) != 0).IsTrue();
    await Assert.That(tracked.Value).IsEqualTo(rawGuid);
  }

  [Test]
  public async Task TrackedGuid_ImplicitFromGuid_StillDetectsVersionAsync() {
    // Arrange
    Guid v7Guid = Guid.CreateVersion7();
    Guid v4Guid = Guid.NewGuid();

    // Act
    TrackedGuid trackedV7 = v7Guid;
    TrackedGuid trackedV4 = v4Guid;

    // Assert - even though source is unknown, version should be detected
    await Assert.That((trackedV7.Metadata & GuidMetadata.Version7) != 0).IsTrue();
    await Assert.That((trackedV4.Metadata & GuidMetadata.Version4) != 0).IsTrue();
  }

  // ========================================
  // Equality Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_Equals_WithSameGuid_ReturnsTrueAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var tracked1 = TrackedGuid.FromExternal(guid);
    var tracked2 = TrackedGuid.FromExternal(guid);

    // Act & Assert
    await Assert.That(tracked1.Equals(tracked2)).IsTrue();
    await Assert.That(tracked1 == tracked2).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_Equals_WithDifferentGuid_ReturnsFalseAsync() {
    // Arrange
    var tracked1 = TrackedGuid.NewMedo();
    var tracked2 = TrackedGuid.NewMedo();

    // Act & Assert
    await Assert.That(tracked1.Equals(tracked2)).IsFalse();
    await Assert.That(tracked1 != tracked2).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_Equals_IgnoresMetadata_ComparesOnlyValueAsync() {
    // Arrange - Same underlying Guid but different sources
    var guid = Guid.CreateVersion7();
    var trackedExternal = TrackedGuid.FromExternal(guid);
    TrackedGuid trackedUnknown = guid; // implicit conversion - SourceUnknown

    // Act & Assert - Should be equal based on Value, not metadata
    await Assert.That(trackedExternal.Equals(trackedUnknown)).IsTrue();
    await Assert.That(trackedExternal == trackedUnknown).IsTrue();
  }

  [Test]
  public async Task TrackedGuid_GetHashCode_WithSameGuid_ReturnsSameHashAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var tracked1 = TrackedGuid.FromExternal(guid);
    var tracked2 = TrackedGuid.FromExternal(guid);

    // Act & Assert
    await Assert.That(tracked1.GetHashCode()).IsEqualTo(tracked2.GetHashCode());
  }

  // ========================================
  // Comparison Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_CompareTo_OrdersChronologicallyAsync() {
    // Arrange - Create IDs with small delay to ensure different timestamps
    var earlier = TrackedGuid.NewMedo();
    await Task.Delay(10); // Ensure different timestamp
    var later = TrackedGuid.NewMedo();

    // Act & Assert
    await Assert.That(earlier.CompareTo(later)).IsLessThan(0);
    await Assert.That(later.CompareTo(earlier)).IsGreaterThan(0);
    await Assert.That(earlier.CompareTo(earlier)).IsEqualTo(0);
  }

  [Test]
  public async Task TrackedGuid_ComparisonOperators_WorkCorrectlyAsync() {
    // Arrange
    var earlier = TrackedGuid.NewMedo();
    await Task.Delay(10);
    var later = TrackedGuid.NewMedo();

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
  public async Task TrackedGuid_ToString_ReturnsGuidStringAsync() {
    // Arrange
    var tracked = TrackedGuid.NewMedo();

    // Act
    var stringValue = tracked.ToString();

    // Assert
    await Assert.That(stringValue).IsEqualTo(tracked.Value.ToString());
  }

  // ========================================
  // IsTracking Property Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_IsTracking_OnlyAuthoritativeSourcesReturnTrueAsync() {
    // Arrange & Act - Create via different methods
    var medo = TrackedGuid.NewMedo();
    var microsoftV7 = TrackedGuid.NewMicrosoftV7();
    var random = TrackedGuid.NewRandom();
    var external = TrackedGuid.FromExternal(Guid.CreateVersion7());
    var parsed = TrackedGuid.Parse(Guid.CreateVersion7().ToString());
    TrackedGuid implicit_ = Guid.CreateVersion7();

    // Assert - Only generation methods have authoritative metadata
    await Assert.That(medo.IsTracking).IsTrue();
    await Assert.That(microsoftV7.IsTracking).IsTrue();
    await Assert.That(random.IsTracking).IsTrue();
    await Assert.That(external.IsTracking).IsFalse();
    await Assert.That(parsed.IsTracking).IsFalse();
    await Assert.That(implicit_.IsTracking).IsFalse();
  }

  // ========================================
  // Default Value Tests
  // ========================================

  [Test]
  public async Task TrackedGuid_Default_HasEmptyGuidAsync() {
    // Arrange
    var defaultTracked = default(TrackedGuid);

    // Assert
    await Assert.That(defaultTracked.Value).IsEqualTo(Guid.Empty);
    await Assert.That(defaultTracked.Metadata).IsEqualTo(GuidMetadata.None);
  }

  [Test]
  public async Task TrackedGuid_Empty_HasEmptyGuidAsync() {
    // Arrange & Act
    var empty = TrackedGuid.Empty;

    // Assert
    await Assert.That(empty.Value).IsEqualTo(Guid.Empty);
  }
}
