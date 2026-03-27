#pragma warning disable CA1707

using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for GuidMetadatas flags enum.
/// Validates bit positions, flag combinations, and the Flags attribute behavior.
/// </summary>
[Category("ValueObjects")]
public class GuidMetadataTests {

  // ========================================
  // None Value Tests
  // ========================================

  [Test]
  public async Task GuidMetadata_None_HasValueZeroAsync() {
    var value = (ushort)GuidMetadatas.None;
    await Assert.That(value).IsEqualTo((ushort)0);
  }

  // ========================================
  // UUID Version Bit Tests (bits 0-1)
  // ========================================

  [Test]
  public async Task GuidMetadata_Version4_IsBit0Async() {
    var value = (ushort)GuidMetadatas.Version4;
    await Assert.That(value).IsEqualTo((ushort)(1 << 0));
  }

  [Test]
  public async Task GuidMetadata_Version7_IsBit1Async() {
    var value = (ushort)GuidMetadatas.Version7;
    await Assert.That(value).IsEqualTo((ushort)(1 << 1));
  }

  // ========================================
  // Creation Source Bit Tests (bits 2-5)
  // ========================================

  [Test]
  public async Task GuidMetadata_SourceMedo_IsBit2Async() {
    var value = (ushort)GuidMetadatas.SourceMedo;
    await Assert.That(value).IsEqualTo((ushort)(1 << 2));
  }

  [Test]
  public async Task GuidMetadata_SourceMicrosoft_IsBit3Async() {
    var value = (ushort)GuidMetadatas.SourceMicrosoft;
    await Assert.That(value).IsEqualTo((ushort)(1 << 3));
  }

  [Test]
  public async Task GuidMetadata_SourceParsed_IsBit4Async() {
    var value = (ushort)GuidMetadatas.SourceParsed;
    await Assert.That(value).IsEqualTo((ushort)(1 << 4));
  }

  [Test]
  public async Task GuidMetadata_SourceExternal_IsBit5Async() {
    var value = (ushort)GuidMetadatas.SourceExternal;
    await Assert.That(value).IsEqualTo((ushort)(1 << 5));
  }

  // ========================================
  // Special Marker Bit Tests (bits 6-7)
  // ========================================

  [Test]
  public async Task GuidMetadata_SourceUnknown_IsBit6Async() {
    var value = (ushort)GuidMetadatas.SourceUnknown;
    await Assert.That(value).IsEqualTo((ushort)(1 << 6));
  }

  [Test]
  public async Task GuidMetadata_Reserved_IsBit7Async() {
    var value = (ushort)GuidMetadatas.Reserved;
    await Assert.That(value).IsEqualTo((ushort)(1 << 7));
  }

  // ========================================
  // Third-Party Source Bit Tests (bits 8-13)
  // ========================================

  [Test]
  [Arguments(GuidMetadatas.SourceMarten, 8)]
  [Arguments(GuidMetadatas.SourceUuidNext, 9)]
  [Arguments(GuidMetadatas.SourceDaanV2, 10)]
  [Arguments(GuidMetadatas.SourceUuids, 11)]
  [Arguments(GuidMetadatas.SourceGuidOne, 12)]
  [Arguments(GuidMetadatas.SourceTaiizor, 13)]
  public async Task GuidMetadata_ThirdPartySources_HaveCorrectBitPositionsAsync(
      GuidMetadatas flag, int bitPosition) {
    var value = (ushort)flag;
    await Assert.That(value).IsEqualTo((ushort)(1 << bitPosition));
  }

  // ========================================
  // Flags Attribute Behavior Tests
  // ========================================

  [Test]
  public async Task GuidMetadata_IsFlagsEnum_SupportsOrCombinationAsync() {
    // Arrange & Act
    var combined = GuidMetadatas.Version7 | GuidMetadatas.SourceMedo;

    // Assert - both flags should be independently testable
    var hasVersion7 = (combined & GuidMetadatas.Version7) != 0;
    var hasMedo = (combined & GuidMetadatas.SourceMedo) != 0;
    var hasVersion4 = (combined & GuidMetadatas.Version4) != 0;
    var hasMicrosoft = (combined & GuidMetadatas.SourceMicrosoft) != 0;

    await Assert.That(hasVersion7).IsTrue();
    await Assert.That(hasMedo).IsTrue();
    await Assert.That(hasVersion4).IsFalse();
    await Assert.That(hasMicrosoft).IsFalse();
  }

  [Test]
  public async Task GuidMetadata_AllFlags_AreDistinctBitsAsync() {
    // Arrange - all defined flags
    var allFlags = new[] {
      GuidMetadatas.Version4, GuidMetadatas.Version7,
      GuidMetadatas.SourceMedo, GuidMetadatas.SourceMicrosoft,
      GuidMetadatas.SourceParsed, GuidMetadatas.SourceExternal,
      GuidMetadatas.SourceUnknown, GuidMetadatas.Reserved,
      GuidMetadatas.SourceMarten, GuidMetadatas.SourceUuidNext,
      GuidMetadatas.SourceDaanV2, GuidMetadatas.SourceUuids,
      GuidMetadatas.SourceGuidOne, GuidMetadatas.SourceTaiizor
    };

    // Assert - each flag should have exactly one bit set (power of 2)
    foreach (var flag in allFlags) {
      var value = (ushort)flag;
      var isPowerOfTwo = value > 0 && (value & (value - 1)) == 0;
      await Assert.That(isPowerOfTwo).IsTrue();
    }
  }

  [Test]
  public async Task GuidMetadata_NoOverlappingBits_BetweenAnyFlagsAsync() {
    // Arrange - all defined flags
    var allFlags = new[] {
      GuidMetadatas.Version4, GuidMetadatas.Version7,
      GuidMetadatas.SourceMedo, GuidMetadatas.SourceMicrosoft,
      GuidMetadatas.SourceParsed, GuidMetadatas.SourceExternal,
      GuidMetadatas.SourceUnknown, GuidMetadatas.Reserved,
      GuidMetadatas.SourceMarten, GuidMetadatas.SourceUuidNext,
      GuidMetadatas.SourceDaanV2, GuidMetadatas.SourceUuids,
      GuidMetadatas.SourceGuidOne, GuidMetadatas.SourceTaiizor
    };

    // Assert - no two flags share the same bit
    for (int i = 0; i < allFlags.Length; i++) {
      for (int j = i + 1; j < allFlags.Length; j++) {
        var overlap = (ushort)((ushort)allFlags[i] & (ushort)allFlags[j]);
        await Assert.That(overlap).IsEqualTo((ushort)0);
      }
    }
  }

  [Test]
  public async Task GuidMetadata_VersionAndSource_CanBeCombinedAsync() {
    // Arrange & Act - combine version with each source
    var medoV7 = GuidMetadatas.Version7 | GuidMetadatas.SourceMedo;
    var microsoftV4 = GuidMetadatas.Version4 | GuidMetadatas.SourceMicrosoft;
    var externalV7 = GuidMetadatas.Version7 | GuidMetadatas.SourceExternal;

    // Assert
    var medoV7Value = (ushort)medoV7;
    var microsoftV4Value = (ushort)microsoftV4;
    var externalV7Value = (ushort)externalV7;

    await Assert.That(medoV7Value).IsEqualTo((ushort)(GuidMetadatas.Version7 | GuidMetadatas.SourceMedo));
    await Assert.That(microsoftV4Value).IsEqualTo((ushort)(GuidMetadatas.Version4 | GuidMetadatas.SourceMicrosoft));
    await Assert.That(externalV7Value).IsEqualTo((ushort)(GuidMetadatas.Version7 | GuidMetadatas.SourceExternal));
  }

  // ========================================
  // Underlying Type Tests
  // ========================================

  [Test]
  public async Task GuidMetadata_UnderlyingType_IsUshortAsync() {
    // The enum is declared as : ushort
    var underlyingType = Enum.GetUnderlyingType(typeof(GuidMetadatas));
    await Assert.That(underlyingType).IsEqualTo(typeof(ushort));
  }

  [Test]
  public async Task GuidMetadata_HasFlagsAttribute_ReturnsTrueAsync() {
    var hasFlagsAttribute = typeof(GuidMetadatas).IsDefined(typeof(FlagsAttribute), false);
    await Assert.That(hasFlagsAttribute).IsTrue();
  }
}
