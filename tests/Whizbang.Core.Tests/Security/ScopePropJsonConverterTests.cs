using System.Text.Json;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for ScopePropJsonConverter covering Read, Write, ReadAsPropertyName,
/// and WriteAsPropertyName with edge cases including null, empty, and unknown values.
/// </summary>
[Category("Security")]
[Category("Serialization")]
public class ScopePropJsonConverterTests {
  // Static options to avoid CA1869 - cache and reuse JsonSerializerOptions
  private static readonly JsonSerializerOptions _options = new() {
    Converters = { new ScopePropJsonConverter() }
  };

  // ========================================
  // Read Tests (value deserialization)
  // ========================================

  [Test]
  [Arguments("Sc", ScopeProp.Scope)]
  [Arguments("Ro", ScopeProp.Roles)]
  [Arguments("Pe", ScopeProp.Perms)]
  [Arguments("Pr", ScopeProp.Principals)]
  [Arguments("Cl", ScopeProp.Claims)]
  [Arguments("Ac", ScopeProp.Actual)]
  [Arguments("Ef", ScopeProp.Effective)]
  [Arguments("Ty", ScopeProp.Type)]
  public async Task Read_AbbreviatedName_ReturnsCorrectEnumAsync(string abbrev, ScopeProp expected) {
    // Arrange
    var json = $"\"{abbrev}\"";

    // Act
    var result = JsonSerializer.Deserialize<ScopeProp>(json, _options);

    // Assert
    await Assert.That(result).IsEqualTo(expected);
  }

  [Test]
  [Arguments("Scope", ScopeProp.Scope)]
  [Arguments("Roles", ScopeProp.Roles)]
  [Arguments("Perms", ScopeProp.Perms)]
  [Arguments("Principals", ScopeProp.Principals)]
  [Arguments("Claims", ScopeProp.Claims)]
  [Arguments("Actual", ScopeProp.Actual)]
  [Arguments("Effective", ScopeProp.Effective)]
  [Arguments("Type", ScopeProp.Type)]
  public async Task Read_FullEnumName_FallsBackToEnumParseAsync(string fullName, ScopeProp expected) {
    // Arrange
    var json = $"\"{fullName}\"";

    // Act
    var result = JsonSerializer.Deserialize<ScopeProp>(json, _options);

    // Assert
    await Assert.That(result).IsEqualTo(expected);
  }

  [Test]
  [Arguments("scope", ScopeProp.Scope)]
  [Arguments("ROLES", ScopeProp.Roles)]
  [Arguments("perms", ScopeProp.Perms)]
  public async Task Read_CaseInsensitiveFullName_FallsBackToEnumParseAsync(string name, ScopeProp expected) {
    // Arrange
    var json = $"\"{name}\"";

    // Act
    var result = JsonSerializer.Deserialize<ScopeProp>(json, _options);

    // Assert
    await Assert.That(result).IsEqualTo(expected);
  }

  [Test]
  public async Task Read_UnknownValue_ThrowsJsonExceptionAsync() {
    // Arrange
    var json = "\"Unknown\"";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<ScopeProp>(json, _options))
      .Throws<JsonException>();
  }

  [Test]
  public async Task Read_EmptyString_ThrowsJsonExceptionAsync() {
    // Arrange
    var json = "\"\"";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<ScopeProp>(json, _options))
      .Throws<JsonException>();
  }

  [Test]
  public async Task Read_NullValue_ThrowsJsonExceptionAsync() {
    // Arrange
    var json = "null";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<ScopeProp>(json, _options))
      .Throws<JsonException>();
  }

  // ========================================
  // Write Tests (value serialization)
  // ========================================

  [Test]
  [Arguments(ScopeProp.Scope, "Sc")]
  [Arguments(ScopeProp.Roles, "Ro")]
  [Arguments(ScopeProp.Perms, "Pe")]
  [Arguments(ScopeProp.Principals, "Pr")]
  [Arguments(ScopeProp.Claims, "Cl")]
  [Arguments(ScopeProp.Actual, "Ac")]
  [Arguments(ScopeProp.Effective, "Ef")]
  [Arguments(ScopeProp.Type, "Ty")]
  public async Task Write_KnownEnum_WritesAbbreviationAsync(ScopeProp prop, string expectedAbbrev) {
    // Act
    var json = JsonSerializer.Serialize(prop, _options);

    // Assert
    await Assert.That(json).IsEqualTo($"\"{expectedAbbrev}\"");
  }

  [Test]
  public async Task Write_UnknownEnumValue_FallsBackToToStringAsync() {
    // Arrange - Cast an invalid byte value to ScopeProp to simulate unknown enum value
    var unknownProp = (ScopeProp)255;

    // Act
    var json = JsonSerializer.Serialize(unknownProp, _options);

    // Assert - Should fall back to ToString() which gives "255"
    await Assert.That(json).IsEqualTo("\"255\"");
  }

  // ========================================
  // ReadAsPropertyName Tests
  // ========================================

  [Test]
  public async Task ReadAsPropertyName_AbbreviatedName_DeserializesCorrectlyAsync() {
    // Arrange - JSON with ScopeProp as dictionary key
    var json = """{"Sc":"scope-value","Ro":"roles-value"}""";

    // Act
    var dict = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _options);

    // Assert
    await Assert.That(dict).IsNotNull();
    await Assert.That(dict!.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(dict[ScopeProp.Scope]).IsEqualTo("scope-value");
    await Assert.That(dict.ContainsKey(ScopeProp.Roles)).IsTrue();
    await Assert.That(dict[ScopeProp.Roles]).IsEqualTo("roles-value");
  }

  [Test]
  public async Task ReadAsPropertyName_FullEnumName_FallsBackToEnumParseAsync() {
    // Arrange - JSON with full enum names as dictionary keys
    var json = """{"Scope":"scope-value","Roles":"roles-value"}""";

    // Act
    var dict = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _options);

    // Assert
    await Assert.That(dict).IsNotNull();
    await Assert.That(dict!.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(dict!.ContainsKey(ScopeProp.Roles)).IsTrue();
  }

  [Test]
  public async Task ReadAsPropertyName_CaseInsensitiveFallback_WorksAsync() {
    // Arrange
    var json = """{"scope":"value","ROLES":"value2"}""";

    // Act
    var dict = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _options);

    // Assert
    await Assert.That(dict).IsNotNull();
    await Assert.That(dict!.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(dict.ContainsKey(ScopeProp.Roles)).IsTrue();
  }

  [Test]
  public async Task ReadAsPropertyName_UnknownKey_ThrowsJsonExceptionAsync() {
    // Arrange
    var json = """{"Unknown":"value"}""";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _options))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ReadAsPropertyName_EmptyKey_ThrowsJsonExceptionAsync() {
    // Arrange
    var json = """{"":"value"}""";

    // Act & Assert
    await Assert.That(() => JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _options))
      .Throws<JsonException>();
  }

  // ========================================
  // WriteAsPropertyName Tests
  // ========================================

  [Test]
  public async Task WriteAsPropertyName_KnownEnum_WritesAbbreviationAsync() {
    // Arrange
    var dict = new Dictionary<ScopeProp, string> {
      [ScopeProp.Scope] = "scope-value",
      [ScopeProp.Roles] = "roles-value"
    };

    // Act
    var json = JsonSerializer.Serialize(dict, _options);

    // Assert
    await Assert.That(json).Contains("\"Sc\":");
    await Assert.That(json).Contains("\"Ro\":");
    await Assert.That(json).DoesNotContain("\"Scope\":");
    await Assert.That(json).DoesNotContain("\"Roles\":");
  }

  [Test]
  public async Task WriteAsPropertyName_UnknownEnum_FallsBackToToStringAsync() {
    // Arrange - Use an invalid enum value to trigger fallback
    var unknownProp = (ScopeProp)255;
    var dict = new Dictionary<ScopeProp, string> {
      [unknownProp] = "value"
    };

    // Act
    var json = JsonSerializer.Serialize(dict, _options);

    // Assert - Should fall back to ToString() which gives "255"
    await Assert.That(json).Contains("\"255\":");
  }

  [Test]
  public async Task WriteAsPropertyName_AllValues_WriteCorrectAbbreviationsAsync() {
    // Arrange
    var dict = new Dictionary<ScopeProp, string> {
      [ScopeProp.Scope] = "v1",
      [ScopeProp.Roles] = "v2",
      [ScopeProp.Perms] = "v3",
      [ScopeProp.Principals] = "v4",
      [ScopeProp.Claims] = "v5",
      [ScopeProp.Actual] = "v6",
      [ScopeProp.Effective] = "v7",
      [ScopeProp.Type] = "v8"
    };

    // Act
    var json = JsonSerializer.Serialize(dict, _options);

    // Assert
    await Assert.That(json).Contains("\"Sc\":");
    await Assert.That(json).Contains("\"Ro\":");
    await Assert.That(json).Contains("\"Pe\":");
    await Assert.That(json).Contains("\"Pr\":");
    await Assert.That(json).Contains("\"Cl\":");
    await Assert.That(json).Contains("\"Ac\":");
    await Assert.That(json).Contains("\"Ef\":");
    await Assert.That(json).Contains("\"Ty\":");
  }

  // ========================================
  // Round-Trip Tests
  // ========================================

  [Test]
  public async Task RoundTrip_DictionaryWithAllKeys_PreservesAllValuesAsync() {
    // Arrange
    var original = new Dictionary<ScopeProp, string> {
      [ScopeProp.Scope] = "scope-val",
      [ScopeProp.Roles] = "roles-val",
      [ScopeProp.Perms] = "perms-val",
      [ScopeProp.Principals] = "principals-val",
      [ScopeProp.Claims] = "claims-val",
      [ScopeProp.Actual] = "actual-val",
      [ScopeProp.Effective] = "effective-val",
      [ScopeProp.Type] = "type-val"
    };

    // Act
    var json = JsonSerializer.Serialize(original, _options);
    var deserialized = JsonSerializer.Deserialize<Dictionary<ScopeProp, string>>(json, _options);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Count).IsEqualTo(8);
    foreach (var kvp in original) {
      await Assert.That(deserialized[kvp.Key]).IsEqualTo(kvp.Value);
    }
  }

  [Test]
  public async Task RoundTrip_SingleValue_PreservesAsync() {
    // Arrange
    var original = ScopeProp.Effective;

    // Act
    var json = JsonSerializer.Serialize(original, _options);
    var deserialized = JsonSerializer.Deserialize<ScopeProp>(json, _options);

    // Assert
    await Assert.That(deserialized).IsEqualTo(original);
  }
}
