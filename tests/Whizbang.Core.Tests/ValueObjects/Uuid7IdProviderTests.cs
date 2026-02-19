using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for Uuid7IdProvider - time-ordered UUID generation.
/// Validates UUIDv7 generation, ordering, and compatibility.
/// </summary>
[Category("Core")]
[Category("ValueObjects")]
[Category("IdGeneration")]
public class Uuid7IdProviderTests {

  [Test]
  public async Task NewGuid_ShouldReturnNonEmptyGuidAsync() {
    // Arrange
    var provider = new Uuid7IdProvider();

    // Act
    var result = provider.NewGuid();

    // Assert
    await Assert.That(result.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task NewGuid_CalledMultipleTimes_ShouldReturnUniqueGuidsAsync() {
    // Arrange
    var provider = new Uuid7IdProvider();
    var guids = new HashSet<Guid>();
    const int count = 100;

    // Act
    for (int i = 0; i < count; i++) {
      guids.Add(provider.NewGuid().Value);
    }

    // Assert
    await Assert.That(guids).Count().IsEqualTo(count);
  }

  [Test]
  public async Task NewGuid_CalledSequentially_ShouldReturnTimeOrderedGuidsAsync() {
    // Arrange
    var provider = new Uuid7IdProvider();
    var previousGuid = provider.NewGuid().Value;

    // Act & Assert
    for (int i = 0; i < 10; i++) {
      var currentGuid = provider.NewGuid().Value;
      await Assert.That(currentGuid.CompareTo(previousGuid)).IsGreaterThanOrEqualTo(0);
      previousGuid = currentGuid;
    }
  }

  [Test]
  public async Task NewGuid_ShouldReturnValidUuidV7FormatAsync() {
    // Arrange
    var provider = new Uuid7IdProvider();

    // Act
    var result = provider.NewGuid();
    var bytes = result.Value.ToByteArray();

    // Assert - UUIDv7 has version bits 0111 in high nibble of byte 7
    var versionByte = bytes[7];
    var highNibble = versionByte >> 4;
    await Assert.That(highNibble).IsEqualTo(0x7);
  }

  [Test]
  public async Task NewGuid_ShouldBeCompatibleWithStandardGuidAsync() {
    // Arrange
    var provider = new Uuid7IdProvider();

    // Act
    var result = provider.NewGuid();
    Guid guidValue = result; // Implicit conversion

    // Assert - Can use standard Guid methods
    var stringRepresentation = guidValue.ToString();
    var byteArray = guidValue.ToByteArray();
    var parsedGuid = Guid.Parse(stringRepresentation);

    await Assert.That(stringRepresentation).IsNotNull();
    await Assert.That(byteArray).Count().IsEqualTo(16);
    await Assert.That(parsedGuid).IsEqualTo(guidValue);
  }

  [Test]
  public async Task NewGuid_HighVolume_ShouldMaintainOrderingAsync() {
    // Arrange
    var provider = new Uuid7IdProvider();
    const int count = 1000;
    var guids = new List<Guid>(count);

    // Act
    for (int i = 0; i < count; i++) {
      guids.Add(provider.NewGuid().Value);
    }

    // Assert - Verify all GUIDs are in ascending order
    for (int i = 1; i < guids.Count; i++) {
      await Assert.That(guids[i].CompareTo(guids[i - 1])).IsGreaterThanOrEqualTo(0);
    }
  }

  [Test]
  public async Task NewGuid_ShouldReturnTrackedGuidWithCorrectMetadataAsync() {
    // Arrange
    var provider = new Uuid7IdProvider();

    // Act
    var result = provider.NewGuid();

    // Assert - Should have Medo v7 metadata
    await Assert.That(result.IsTimeOrdered).IsTrue();
    await Assert.That(result.SubMillisecondPrecision).IsTrue();
    await Assert.That(result.IsTracking).IsTrue();
    await Assert.That((result.Metadata & GuidMetadata.Version7) != 0).IsTrue();
    await Assert.That((result.Metadata & GuidMetadata.SourceMedo) != 0).IsTrue();
  }
}
