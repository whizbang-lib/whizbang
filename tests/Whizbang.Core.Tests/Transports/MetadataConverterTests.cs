using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for MetadataConverter - JSON converter for IReadOnlyDictionary&lt;string, JsonElement&gt;.
/// Covers all read/write paths including null handling, various value types, and error cases.
/// </summary>
[Category("Core")]
[Category("Transports")]
[Category("Serialization")]
public class MetadataConverterTests {
  private readonly MetadataConverter _converter = new();

  // ===========================
  // Read Tests
  // ===========================

  [Test]
  public async Task Read_WithNullToken_ShouldReturnNullAsync() {
    // Arrange
    var json = "null"u8.ToArray();
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Read_WithEmptyObject_ShouldReturnEmptyDictionaryAsync() {
    // Arrange
    var json = "{}"u8.ToArray();
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Read_WithStringValue_ShouldDeserializeCorrectlyAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"key":"value"}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["key"].GetString()).IsEqualTo("value");
  }

  [Test]
  public async Task Read_WithNumberValue_ShouldDeserializeCorrectlyAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"count":42}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["count"].GetInt32()).IsEqualTo(42);
  }

  [Test]
  public async Task Read_WithBooleanValue_ShouldDeserializeCorrectlyAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"active":true,"deleted":false}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["active"].GetBoolean()).IsTrue();
    await Assert.That(result["deleted"].GetBoolean()).IsFalse();
  }

  [Test]
  public async Task Read_WithNullValue_ShouldDeserializeAsNullJsonElementAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"key":null}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["key"].ValueKind).IsEqualTo(JsonValueKind.Null);
  }

  [Test]
  public async Task Read_WithArrayValue_ShouldDeserializeAsArrayJsonElementAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"items":[1,2,3]}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["items"].ValueKind).IsEqualTo(JsonValueKind.Array);
    await Assert.That(result["items"].GetArrayLength()).IsEqualTo(3);
  }

  [Test]
  public async Task Read_WithNestedObject_ShouldDeserializeAsObjectJsonElementAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"nested":{"inner":"value"}}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["nested"].ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(result["nested"].GetProperty("inner").GetString()).IsEqualTo("value");
  }

  [Test]
  public async Task Read_WithMultipleEntries_ShouldDeserializeAllAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"a":"alpha","b":2,"c":true}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Count).IsEqualTo(3);
    await Assert.That(result["a"].GetString()).IsEqualTo("alpha");
    await Assert.That(result["b"].GetInt32()).IsEqualTo(2);
    await Assert.That(result["c"].GetBoolean()).IsTrue();
  }

  [Test]
  public async Task Read_WithInvalidStartToken_ShouldThrowJsonExceptionAsync() {
    // Arrange - An array instead of an object
    var json = "[1,2,3]"u8.ToArray();
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Expected StartObject token");
  }

  [Test]
  public async Task Read_WithDoubleValue_ShouldDeserializeCorrectlyAsync() {
    // Arrange
    var json = Encoding.UTF8.GetBytes("""{"pi":3.14159}""");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["pi"].GetDouble()).IsEqualTo(3.14159);
  }

  [Test]
  public async Task Read_WithLargeInt64Value_ShouldDeserializeCorrectlyAsync() {
    // Arrange - Value larger than int32 max
    var largeValue = (long)int.MaxValue + 1000L;
    var json = Encoding.UTF8.GetBytes($"{{\"big\":{largeValue}}}");
    var reader = new Utf8JsonReader(json);
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!["big"].GetInt64()).IsEqualTo(largeValue);
  }

  // ===========================
  // Write Tests
  // ===========================

  [Test]
  public async Task Write_WithNullValue_ShouldWriteNullAsync() {
    // Arrange
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    // Act
    _converter.Write(writer, null, JsonSerializerOptions.Default);
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    await Assert.That(json).IsEqualTo("null");
  }

  [Test]
  public async Task Write_WithEmptyDictionary_ShouldWriteEmptyObjectAsync() {
    // Arrange
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);
    var dictionary = new Dictionary<string, JsonElement>();

    // Act
    _converter.Write(writer, dictionary, JsonSerializerOptions.Default);
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    await Assert.That(json).IsEqualTo("{}");
  }

  [Test]
  public async Task Write_WithStringValues_ShouldWriteCorrectlyAsync() {
    // Arrange
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);
    var dictionary = new Dictionary<string, JsonElement> {
      ["name"] = JsonSerializer.SerializeToElement("test")
    };

    // Act
    _converter.Write(writer, dictionary, JsonSerializerOptions.Default);
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    await Assert.That(json).Contains("\"name\"");
    await Assert.That(json).Contains("\"test\"");
  }

  [Test]
  public async Task Write_WithMixedValueTypes_ShouldWriteCorrectlyAsync() {
    // Arrange
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);
    var dictionary = new Dictionary<string, JsonElement> {
      ["str"] = JsonSerializer.SerializeToElement("hello"),
      ["num"] = JsonSerializer.SerializeToElement(42),
      ["flag"] = JsonSerializer.SerializeToElement(true)
    };

    // Act
    _converter.Write(writer, dictionary, JsonSerializerOptions.Default);
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    var doc = JsonDocument.Parse(json);
    await Assert.That(doc.RootElement.GetProperty("str").GetString()).IsEqualTo("hello");
    await Assert.That(doc.RootElement.GetProperty("num").GetInt32()).IsEqualTo(42);
    await Assert.That(doc.RootElement.GetProperty("flag").GetBoolean()).IsTrue();
  }

  // ===========================
  // Round-trip Tests
  // ===========================

  [Test]
  public async Task RoundTrip_WithComplexDictionary_ShouldPreserveAllDataAsync() {
    // Arrange
    var original = new Dictionary<string, JsonElement> {
      ["string"] = JsonSerializer.SerializeToElement("value"),
      ["number"] = JsonSerializer.SerializeToElement(12345),
      ["double"] = JsonSerializer.SerializeToElement(3.14),
      ["bool"] = JsonSerializer.SerializeToElement(true),
      ["null"] = JsonSerializer.SerializeToElement<string?>(null),
      ["array"] = JsonSerializer.SerializeToElement(new List<int> { 1, 2, 3 }),
      ["nested"] = JsonSerializer.SerializeToElement(new { inner = "data" })
    };

    // Act - Write
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);
    _converter.Write(writer, original, JsonSerializerOptions.Default);
    writer.Flush();

    // Act - Read
    var reader = new Utf8JsonReader(stream.ToArray());
    reader.Read();
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Count).IsEqualTo(7);
    await Assert.That(result["string"].GetString()).IsEqualTo("value");
    await Assert.That(result["number"].GetInt32()).IsEqualTo(12345);
    await Assert.That(result["double"].GetDouble()).IsEqualTo(3.14);
    await Assert.That(result["bool"].GetBoolean()).IsTrue();
    await Assert.That(result["null"].ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(result["array"].ValueKind).IsEqualTo(JsonValueKind.Array);
    await Assert.That(result["nested"].ValueKind).IsEqualTo(JsonValueKind.Object);
  }

  [Test]
  public async Task RoundTrip_WithNullValue_ShouldPreserveNullAsync() {
    // Act - Write null
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);
    _converter.Write(writer, null, JsonSerializerOptions.Default);
    writer.Flush();

    // Act - Read null
    var reader = new Utf8JsonReader(stream.ToArray());
    reader.Read();
    var result = _converter.Read(ref reader, typeof(IReadOnlyDictionary<string, JsonElement>), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result).IsNull();
  }

  // ===========================
  // CanConvert Tests
  // ===========================

  [Test]
  public async Task CanConvert_WithMatchingType_ShouldReturnTrueAsync() {
    // Act & Assert
    var canConvert = _converter.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>));
    await Assert.That(canConvert).IsTrue();
  }
}
