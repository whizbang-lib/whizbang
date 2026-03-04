using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Serialization;

/// <summary>
/// A lenient DateTimeOffset JSON converter that handles dates with or without timezone offsets.
/// This is necessary because some serializers (like PostgreSQL JSONB) may store timestamps
/// without explicit timezone offsets.
/// </summary>
/// <remarks>
/// Supported formats:
/// - ISO 8601 with offset: "2024-01-01T00:00:00+00:00" or "2024-01-01T00:00:00Z"
/// - ISO 8601 without offset: "2024-01-01T00:00:00" (assumes UTC)
/// - Date only: "2024-01-01" (assumes midnight UTC)
/// - PostgreSQL special: "-infinity" (maps to MinValue), "infinity" (maps to MaxValue)
/// </remarks>
/// <docs>internals/json-serialization-customizations</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/LenientDateTimeOffsetConverterTests.cs</tests>
public sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset> {
  /// <inheritdoc/>
  public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType != JsonTokenType.String) {
      throw new JsonException($"Expected string token for DateTimeOffset, but got {reader.TokenType}");
    }

    var value = reader.GetString();
    if (string.IsNullOrEmpty(value)) {
      return default;
    }

    // Handle PostgreSQL special timestamp values
    if (value == "-infinity") {
      return DateTimeOffset.MinValue;
    }
    if (value == "infinity") {
      return DateTimeOffset.MaxValue;
    }

    // Check if the string contains timezone info (Z, +, or - followed by digits)
    bool hasTimezoneOffset = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                             (value.Length > 6 &&
                              (value[^6] == '+' || value[^6] == '-') &&
                              char.IsDigit(value[^5]) &&
                              char.IsDigit(value[^4]) &&
                              value[^3] == ':');

    if (hasTimezoneOffset) {
      // Parse with offset preserved (most common case for properly formatted data)
      if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)) {
        return result;
      }
    }

    // No timezone offset or parsing failed - parse as DateTime and assume UTC
    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)) {
      // Create DateTimeOffset with explicit UTC offset
      return new DateTimeOffset(dateTime, TimeSpan.Zero);
    }

    throw new JsonException($"Unable to parse DateTimeOffset from value: {value}");
  }

  /// <inheritdoc/>
  public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) {
    // Always write in ISO 8601 format with offset for consistency
    writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
  }
}

/// <summary>
/// A lenient nullable DateTimeOffset JSON converter.
/// </summary>
/// <docs>internals/json-serialization-customizations</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/LenientDateTimeOffsetConverterTests.cs:LenientNullableDateTimeOffsetConverterTests</tests>
public sealed class LenientNullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?> {
  private static readonly LenientDateTimeOffsetConverter _innerConverter = new();

  /// <inheritdoc/>
  public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType == JsonTokenType.Null) {
      return null;
    }

    return _innerConverter.Read(ref reader, typeof(DateTimeOffset), options);
  }

  /// <inheritdoc/>
  public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options) {
    if (value is null) {
      writer.WriteNullValue();
    } else {
      _innerConverter.Write(writer, value.Value, options);
    }
  }
}
