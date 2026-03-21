#pragma warning disable CA1707

using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for TrackedGuidJsonConverter.
/// Verifies that TrackedGuid serializes as plain UUID strings and deserializes correctly.
/// </summary>
[Category("ValueObjects")]
[Category("Serialization")]
public class TrackedGuidJsonConverterTests {
  private readonly JsonSerializerOptions _options;

  public TrackedGuidJsonConverterTests() {
    _options = new JsonSerializerOptions();
    _options.Converters.Add(new TrackedGuidJsonConverter());
  }

  // ========================================
  // Write (Serialization) Tests
  // ========================================

  [Test]
  public async Task Write_WithTrackedGuid_SerializesAsPlainUuidStringAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var tracked = TrackedGuid.FromExternal(guid);

    // Act
    var json = JsonSerializer.Serialize(tracked, _options);

    // Assert - should be a quoted UUID string
    await Assert.That(json).IsEqualTo($"\"{guid}\"");
  }

  [Test]
  public async Task Write_WithMedoTrackedGuid_SerializesValueNotMetadataAsync() {
    // Arrange
    var tracked = TrackedGuid.NewMedo();
    var expectedGuid = tracked.Value;

    // Act
    var json = JsonSerializer.Serialize(tracked, _options);

    // Assert - should not contain metadata, just the UUID string
    await Assert.That(json).IsEqualTo($"\"{expectedGuid}\"");
    await Assert.That(json).DoesNotContain("Metadata");
    await Assert.That(json).DoesNotContain("Value");
  }

  [Test]
  public async Task Write_WithEmptyTrackedGuid_SerializesEmptyGuidStringAsync() {
    // Arrange
    var tracked = TrackedGuid.Empty;

    // Act
    var json = JsonSerializer.Serialize(tracked, _options);

    // Assert
    await Assert.That(json).IsEqualTo($"\"{Guid.Empty}\"");
  }

  // ========================================
  // Read (Deserialization) Tests
  // ========================================

  [Test]
  public async Task Read_WithValidGuidString_DeserializesToTrackedGuidAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var json = $"\"{guid}\"";

    // Act
    var result = JsonSerializer.Deserialize<TrackedGuid>(json, _options);

    // Assert
    await Assert.That(result.Value).IsEqualTo(guid);
  }

  [Test]
  public async Task Read_WithValidGuidString_MarksAsExternalSourceAsync() {
    // Arrange
    var guid = Guid.CreateVersion7();
    var json = $"\"{guid}\"";

    // Act
    var result = JsonSerializer.Deserialize<TrackedGuid>(json, _options);

    // Assert - FromExternal sets SourceExternal
    await Assert.That((result.Metadata & GuidMetadata.SourceExternal) != 0).IsTrue();
  }

  [Test]
  public async Task Read_WithNullValue_ReturnsEmptyTrackedGuidAsync() {
    // Arrange
    const string json = "null";

    // Act
    var result = JsonSerializer.Deserialize<TrackedGuid>(json, _options);

    // Assert
    await Assert.That(result.Value).IsEqualTo(Guid.Empty);
    await Assert.That(result.Metadata).IsEqualTo(GuidMetadata.None);
  }

  [Test]
  public async Task Read_WithInvalidGuidString_ReturnsEmptyTrackedGuidAsync() {
    // Arrange
    var json = "\"not-a-valid-guid\"";

    // Act
    var result = JsonSerializer.Deserialize<TrackedGuid>(json, _options);

    // Assert
    await Assert.That(result.Value).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task Read_WithEmptyGuidString_DeserializesToEmptyGuidAsync() {
    // Arrange
    var json = $"\"{Guid.Empty}\"";

    // Act
    var result = JsonSerializer.Deserialize<TrackedGuid>(json, _options);

    // Assert
    await Assert.That(result.Value).IsEqualTo(Guid.Empty);
  }

  // ========================================
  // Round-trip Tests
  // ========================================

  [Test]
  public async Task RoundTrip_SerializeAndDeserialize_PreservesGuidValueAsync() {
    // Arrange
    var original = TrackedGuid.NewMedo();

    // Act
    var json = JsonSerializer.Serialize(original, _options);
    var deserialized = JsonSerializer.Deserialize<TrackedGuid>(json, _options);

    // Assert - value should survive round-trip
    await Assert.That(deserialized.Value).IsEqualTo(original.Value);
  }

  [Test]
  public async Task RoundTrip_MetadataIsLost_AfterDeserializationAsync() {
    // Arrange - start with Medo metadata
    var original = TrackedGuid.NewMedo();
    await Assert.That((original.Metadata & GuidMetadata.SourceMedo) != 0).IsTrue();

    // Act
    var json = JsonSerializer.Serialize(original, _options);
    var deserialized = JsonSerializer.Deserialize<TrackedGuid>(json, _options);

    // Assert - after deserialization, source is External (not Medo)
    await Assert.That((deserialized.Metadata & GuidMetadata.SourceMedo) != 0).IsFalse();
    await Assert.That((deserialized.Metadata & GuidMetadata.SourceExternal) != 0).IsTrue();
  }

  // ========================================
  // Object Property Serialization Tests
  // ========================================

  [Test]
  public async Task Write_InObjectProperty_SerializesAsUuidStringNotObjectAsync() {
    // Arrange
    var dto = new TestDto { Id = TrackedGuid.NewMedo(), Name = "test" };

    // Act
    var json = JsonSerializer.Serialize(dto, _options);

    // Assert - Id should be a UUID string, not an object with Value/Metadata
    await Assert.That(json).DoesNotContain("\"Value\"");
    await Assert.That(json).DoesNotContain("\"Metadata\"");
    await Assert.That(json).Contains("\"Id\"");
    await Assert.That(json).Contains("\"Name\"");
  }

  [Test]
  public async Task RoundTrip_ObjectWithTrackedGuid_PreservesGuidValueAsync() {
    // Arrange
    var original = new TestDto { Id = TrackedGuid.NewMedo(), Name = "test" };

    // Act
    var json = JsonSerializer.Serialize(original, _options);
    var deserialized = JsonSerializer.Deserialize<TestDto>(json, _options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Id.Value).IsEqualTo(original.Id.Value);
    await Assert.That(deserialized.Name).IsEqualTo("test");
  }

  // ========================================
  // Test Support Types
  // ========================================

  private sealed class TestDto {
    public TrackedGuid Id { get; set; }
    public string Name { get; set; } = string.Empty;
  }
}
