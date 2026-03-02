using System.Text.Json;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Tests for <see cref="LenientDateTimeOffsetConverter"/>.
/// </summary>
public class LenientDateTimeOffsetConverterTests {
  private static readonly JsonSerializerOptions _options = new() {
    Converters = { new LenientDateTimeOffsetConverter() }
  };

  [Test]
  public async Task Read_WithOffset_ParsesCorrectlyAsync() {
    // Arrange
    var json = "\"2024-01-15T10:30:00+05:00\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Month).IsEqualTo(1);
    await Assert.That(result.Day).IsEqualTo(15);
    await Assert.That(result.Hour).IsEqualTo(10);
    await Assert.That(result.Minute).IsEqualTo(30);
    await Assert.That(result.Offset).IsEqualTo(TimeSpan.FromHours(5));
  }

  [Test]
  public async Task Read_WithZuluTime_ParsesCorrectlyAsync() {
    // Arrange
    var json = "\"2024-01-15T10:30:00Z\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Month).IsEqualTo(1);
    await Assert.That(result.Day).IsEqualTo(15);
    await Assert.That(result.Hour).IsEqualTo(10);
    await Assert.That(result.Minute).IsEqualTo(30);
    await Assert.That(result.Offset).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Read_WithoutOffset_ParsesAsUtcAsync() {
    // Arrange - This is the key case: dates without timezone offset (common from PostgreSQL)
    var json = "\"2024-01-15T10:30:00\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Month).IsEqualTo(1);
    await Assert.That(result.Day).IsEqualTo(15);
    await Assert.That(result.Hour).IsEqualTo(10);
    await Assert.That(result.Minute).IsEqualTo(30);
    // Should assume UTC when no offset specified
    await Assert.That(result.Offset).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Read_DateOnly_ParsesAsMidnightUtcAsync() {
    // Arrange
    var json = "\"2024-01-15\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Month).IsEqualTo(1);
    await Assert.That(result.Day).IsEqualTo(15);
    await Assert.That(result.Hour).IsEqualTo(0);
    await Assert.That(result.Minute).IsEqualTo(0);
    await Assert.That(result.Second).IsEqualTo(0);
  }

  [Test]
  public async Task Read_EmptyString_ReturnsDefaultAsync() {
    // Arrange
    var json = "\"\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result).IsEqualTo(default(DateTimeOffset));
  }

  [Test]
  public async Task Write_ProducesIso8601WithOffsetAsync() {
    // Arrange
    var value = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(-8));

    // Act
    var json = JsonSerializer.Serialize(value, _options);

    // Assert - Should write in round-trip format with offset
    await Assert.That(json).Contains("2024-01-15T10:30:00.0000000-08:00");
  }

  [Test]
  public async Task RoundTrip_PreservesValueAsync() {
    // Arrange
    var original = new DateTimeOffset(2024, 1, 15, 10, 30, 45, 123, TimeSpan.FromHours(3));

    // Act
    var json = JsonSerializer.Serialize(original, _options);
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result).IsEqualTo(original);
  }

  [Test]
  public async Task Read_InvalidFormat_ThrowsJsonExceptionAsync() {
    // Arrange
    var json = "\"not-a-date\"";

    // Act & Assert
    await Assert.ThrowsAsync<JsonException>(async () => {
      JsonSerializer.Deserialize<DateTimeOffset>(json, _options);
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Read_NumberToken_ThrowsJsonExceptionAsync() {
    // Arrange - JSON number instead of string
    var json = "123456789";

    // Act & Assert
    await Assert.ThrowsAsync<JsonException>(async () => {
      JsonSerializer.Deserialize<DateTimeOffset>(json, _options);
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Read_NegativeOffset_ParsesCorrectlyAsync() {
    // Arrange
    var json = "\"2024-01-15T10:30:00-08:00\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Month).IsEqualTo(1);
    await Assert.That(result.Day).IsEqualTo(15);
    await Assert.That(result.Hour).IsEqualTo(10);
    await Assert.That(result.Offset).IsEqualTo(TimeSpan.FromHours(-8));
  }

  [Test]
  public async Task Read_WithMilliseconds_ParsesCorrectlyAsync() {
    // Arrange
    var json = "\"2024-01-15T10:30:00.123\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Millisecond).IsEqualTo(123);
    await Assert.That(result.Offset).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Read_WithMillisecondsAndOffset_ParsesCorrectlyAsync() {
    // Arrange
    var json = "\"2024-01-15T10:30:00.456+03:00\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Millisecond).IsEqualTo(456);
    await Assert.That(result.Offset).IsEqualTo(TimeSpan.FromHours(3));
  }

  [Test]
  public async Task Read_LowercaseZ_ParsesCorrectlyAsync() {
    // Arrange - lowercase 'z' is also valid
    var json = "\"2024-01-15T10:30:00z\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result.Year).IsEqualTo(2024);
    await Assert.That(result.Offset).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Write_UtcValue_ProducesCorrectFormatAsync() {
    // Arrange
    var value = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

    // Act
    var json = JsonSerializer.Serialize(value, _options);

    // Assert - Check the date/time parts (+ is Unicode escaped to \u002B)
    await Assert.That(json).Contains("2024-01-15T10:30:00.0000000");
    await Assert.That(json).Contains("00:00"); // The timezone part
  }

  [Test]
  public async Task Read_PostgresNegativeInfinity_ReturnsMinValueAsync() {
    // Arrange - PostgreSQL special timestamp value
    var json = "\"-infinity\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result).IsEqualTo(DateTimeOffset.MinValue);
  }

  [Test]
  public async Task Read_PostgresInfinity_ReturnsMaxValueAsync() {
    // Arrange - PostgreSQL special timestamp value
    var json = "\"infinity\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset>(json, _options);

    // Assert
    await Assert.That(result).IsEqualTo(DateTimeOffset.MaxValue);
  }
}

/// <summary>
/// Tests for <see cref="LenientNullableDateTimeOffsetConverter"/>.
/// </summary>
public class LenientNullableDateTimeOffsetConverterTests {
  private static readonly JsonSerializerOptions _options = new() {
    Converters = { new LenientNullableDateTimeOffsetConverter() }
  };

  [Test]
  public async Task Read_Null_ReturnsNullAsync() {
    // Arrange
    var json = "null";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset?>(json, _options);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Read_ValidValue_ReturnsValueAsync() {
    // Arrange
    var json = "\"2024-01-15T10:30:00\"";

    // Act
    var result = JsonSerializer.Deserialize<DateTimeOffset?>(json, _options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.Year).IsEqualTo(2024);
  }

  [Test]
  public async Task Write_Null_WritesNullAsync() {
    // Arrange
    DateTimeOffset? value = null;

    // Act
    var json = JsonSerializer.Serialize(value, _options);

    // Assert
    await Assert.That(json).IsEqualTo("null");
  }

  [Test]
  public async Task Write_Value_WritesFormattedDateAsync() {
    // Arrange
    DateTimeOffset? value = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

    // Act
    var json = JsonSerializer.Serialize(value, _options);

    // Assert
    await Assert.That(json).Contains("2024-01-15T10:30:00");
  }
}
