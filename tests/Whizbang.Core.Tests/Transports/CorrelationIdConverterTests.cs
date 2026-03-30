using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for CorrelationIdConverter - JSON converter for CorrelationId value object.
/// Tests verify serialization, deserialization, and error handling.
/// </summary>
[Category("Core")]
[Category("Transports")]
[Category("Serialization")]
public class CorrelationIdConverterTests {
  private readonly CorrelationIdConverter _converter = new();

  [Test]
  public async Task Read_WithValidUuidV7_ShouldReturnCorrelationIdAsync() {
    // Arrange - Use CorrelationId.New() which generates valid UUIDv7
    var correlationId = CorrelationId.New();
    var json = $"\"{correlationId.Value}\"";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act
    var result = _converter.Read(ref reader, typeof(CorrelationId), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result.Value).IsEqualTo(correlationId.Value);
  }

  [Test]
  public async Task Read_WithNullValue_ShouldThrowJsonExceptionAsync() {
    // Arrange
    const string json = "null";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(CorrelationId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid CorrelationId format");
  }

  [Test]
  public async Task Read_WithInvalidGuidFormat_ShouldThrowJsonExceptionAsync() {
    // Arrange
    var json = "\"not-a-guid\"";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(CorrelationId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid CorrelationId format");
  }

  [Test]
  public async Task Read_WithEmptyString_ShouldThrowJsonExceptionAsync() {
    // Arrange
    var json = "\"\"";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(CorrelationId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid CorrelationId format");
  }

  [Test]
  public async Task Write_WithValidCorrelationId_ShouldWriteGuidStringAsync() {
    // Arrange - Use CorrelationId.New() which generates valid UUIDv7
    var correlationId = CorrelationId.New();
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    // Act
    _converter.Write(writer, correlationId, JsonSerializerOptions.Default);
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    await Assert.That(json).IsEqualTo($"\"{correlationId.Value}\"");
  }

  [Test]
  public async Task RoundTrip_WithValidCorrelationId_ShouldPreserveValueAsync() {
    // Arrange - Use CorrelationId.New() which generates valid UUIDv7
    var original = CorrelationId.New();
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    // Act - Write
    _converter.Write(writer, original, JsonSerializerOptions.Default);
    writer.Flush();

    // Act - Read
    var reader = new Utf8JsonReader(stream.ToArray());
    reader.Read();
    var result = _converter.Read(ref reader, typeof(CorrelationId), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result.Value).IsEqualTo(original.Value);
  }

  [Test]
  public async Task Read_WithWhitespaceGuid_ShouldThrowJsonExceptionAsync() {
    // Arrange
    var json = "\"   \"";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(CorrelationId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid CorrelationId format");
  }

  [Test]
  public async Task Read_WithPartialGuid_ShouldThrowJsonExceptionAsync() {
    // Arrange
    var json = "\"12345678-1234-1234-1234\"";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(CorrelationId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid CorrelationId format");
  }

  [Test]
  public async Task Write_WithMultipleCorrelationIds_ShouldWriteAllAsync() {
    // Arrange - Use CorrelationId.New() which generates valid UUIDv7
    var correlationIds = Enumerable.Range(0, 5).Select(_ => CorrelationId.New()).ToList();
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    // Act
    writer.WriteStartArray();
    foreach (var correlationId in correlationIds) {
      _converter.Write(writer, correlationId, JsonSerializerOptions.Default);
    }
    writer.WriteEndArray();
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    foreach (var correlationId in correlationIds) {
      await Assert.That(json).Contains(correlationId.Value.ToString());
    }
  }
}
