using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for MessageIdConverter - JSON converter for MessageId value object.
/// Tests verify serialization, deserialization, and error handling.
/// </summary>
[Category("Core")]
[Category("Transports")]
[Category("Serialization")]
public class MessageIdConverterTests {
  private readonly MessageIdConverter _converter = new();

  [Test]
  public async Task Read_WithValidUuidV7_ShouldReturnMessageIdAsync() {
    // Arrange - Use MessageId.New() which generates valid UUIDv7
    var messageId = MessageId.New();
    var json = $"\"{messageId.Value}\"";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read(); // Move to the first token

    // Act
    var result = _converter.Read(ref reader, typeof(MessageId), JsonSerializerOptions.Default);

    // Assert
    await Assert.That(result.Value).IsEqualTo(messageId.Value);
  }

  [Test]
  public async Task Read_WithNullValue_ShouldThrowJsonExceptionAsync() {
    // Arrange
    var json = "null";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(MessageId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid MessageId format");
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
      _converter.Read(ref reader, typeof(MessageId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid MessageId format");
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
      _converter.Read(ref reader, typeof(MessageId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid MessageId format");
  }

  [Test]
  public async Task Write_WithValidMessageId_ShouldWriteGuidStringAsync() {
    // Arrange - Use MessageId.New() which generates valid UUIDv7
    var messageId = MessageId.New();
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    // Act
    _converter.Write(writer, messageId, JsonSerializerOptions.Default);
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    await Assert.That(json).IsEqualTo($"\"{messageId.Value}\"");
  }

  [Test]
  public async Task RoundTrip_WithValidMessageId_ShouldPreserveValueAsync() {
    // Arrange - Use MessageId.New() which generates valid UUIDv7
    var original = MessageId.New();
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    // Act - Write
    _converter.Write(writer, original, JsonSerializerOptions.Default);
    writer.Flush();

    // Act - Read
    var reader = new Utf8JsonReader(stream.ToArray());
    reader.Read();
    var result = _converter.Read(ref reader, typeof(MessageId), JsonSerializerOptions.Default);

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
      _converter.Read(ref reader, typeof(MessageId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid MessageId format");
  }

  [Test]
  public async Task Read_WithPartialGuid_ShouldThrowJsonExceptionAsync() {
    // Arrange - GUID with missing characters
    var json = "\"12345678-1234-1234-1234\"";
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();

    // Act & Assert
    JsonException? caughtException = null;
    try {
      _converter.Read(ref reader, typeof(MessageId), JsonSerializerOptions.Default);
    } catch (JsonException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("Invalid MessageId format");
  }

  [Test]
  public async Task Write_WithMultipleMessageIds_ShouldWriteAllAsync() {
    // Arrange - Use MessageId.New() which generates valid UUIDv7
    var messageIds = Enumerable.Range(0, 5).Select(_ => MessageId.New()).ToList();
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    // Act
    writer.WriteStartArray();
    foreach (var messageId in messageIds) {
      _converter.Write(writer, messageId, JsonSerializerOptions.Default);
    }
    writer.WriteEndArray();
    writer.Flush();

    // Assert
    var json = Encoding.UTF8.GetString(stream.ToArray());
    foreach (var messageId in messageIds) {
      await Assert.That(json).Contains(messageId.Value.ToString());
    }
  }
}
