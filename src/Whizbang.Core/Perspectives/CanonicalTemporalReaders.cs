using System.Text.Json;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// One reader per temporal kind, for every path that reads a stored document.
/// </summary>
/// <remarks>
/// <para>
/// A stored document is read two ways. System.Text.Json reads an opaque document through the
/// persistence profile's converters (<see cref="CanonicalTemporalJsonConverters"/>), and Entity
/// Framework reads a mapped document through its own JSON reader, property by property. Both call
/// these, so what one path reads the other reads: a number in the canonical unit, or, for now, a
/// rendering, counted and announced through <see cref="StoredFormFallbacks"/>.
/// </para>
/// <para>
/// Both refuse anything else the same way: a <see cref="JsonException"/> naming the type, the forms
/// accepted and the token found. That is what lets a failure downstream be classified by its type
/// (<see cref="StoredFormUnreadable"/>) rather than by the wording of whichever reader happened to
/// fail, and what an operator reads in the log.
/// </para>
/// <para>
/// A number is never reinterpreted. Which unit a number is in is settled by the stored-form ledger
/// before any reader sees the row, so a reader that guessed would only be guessing wrong quietly.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalReadersTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalReaderToleranceTests.cs</tests>
public static class CanonicalTemporalReaders {
  /// <summary>An instant from the token under the reader: microseconds, or a rendering.</summary>
  /// <param name="reader">A reader positioned on the value.</param>
  /// <returns>The instant, as UTC.</returns>
  /// <exception cref="JsonException">The token is neither a number nor a rendering.</exception>
  public static DateTime Instant(ref Utf8JsonReader reader) =>
    reader.TokenType switch {
      JsonTokenType.Number => CanonicalTemporalFormat.FromEpochMicroseconds(reader.GetInt64()),
      JsonTokenType.String => CanonicalTemporalRenderings.Instant(reader.GetString()!),
      _ => throw CanonicalTemporalRenderings.Unexpected(nameof(DateTime), reader.TokenType),
    };

  /// <summary>An offset instant from the token under the reader: microseconds, or a rendering.</summary>
  /// <param name="reader">A reader positioned on the value.</param>
  /// <returns>The instant it names, at zero offset when read from a number.</returns>
  /// <exception cref="JsonException">The token is neither a number nor a rendering.</exception>
  public static DateTimeOffset OffsetInstant(ref Utf8JsonReader reader) =>
    reader.TokenType switch {
      JsonTokenType.Number => CanonicalTemporalFormat.OffsetFromEpochMicroseconds(reader.GetInt64()),
      JsonTokenType.String => CanonicalTemporalRenderings.OffsetInstant(reader.GetString()!),
      _ => throw CanonicalTemporalRenderings.Unexpected(nameof(DateTimeOffset), reader.TokenType),
    };

  /// <summary>A date from the token under the reader: microseconds at its midnight, or a rendering.</summary>
  /// <param name="reader">A reader positioned on the value.</param>
  /// <returns>The date.</returns>
  /// <exception cref="JsonException">The token is neither a number nor a rendering.</exception>
  public static DateOnly Day(ref Utf8JsonReader reader) =>
    reader.TokenType switch {
      JsonTokenType.Number => CanonicalTemporalFormat.DayFromEpochMicroseconds(reader.GetInt64()),
      JsonTokenType.String => CanonicalTemporalRenderings.Day(reader.GetString()!),
      _ => throw CanonicalTemporalRenderings.Unexpected(nameof(DateOnly), reader.TokenType),
    };

  /// <summary>A time of day from the token under the reader: microseconds since midnight, or a rendering.</summary>
  /// <param name="reader">A reader positioned on the value.</param>
  /// <returns>The time of day.</returns>
  /// <exception cref="JsonException">The token is neither a number nor a rendering.</exception>
  public static TimeOnly TimeOfDay(ref Utf8JsonReader reader) =>
    reader.TokenType switch {
      JsonTokenType.Number => CanonicalTemporalFormat.FromMicrosecondsOfDay(reader.GetInt64()),
      JsonTokenType.String => CanonicalTemporalRenderings.TimeOfDay(reader.GetString()!),
      _ => throw CanonicalTemporalRenderings.Unexpected(nameof(TimeOnly), reader.TokenType),
    };

  /// <summary>A duration from the token under the reader: microseconds, or a rendering.</summary>
  /// <param name="reader">A reader positioned on the value.</param>
  /// <returns>The duration.</returns>
  /// <exception cref="JsonException">The token is neither a number nor a rendering.</exception>
  public static TimeSpan Duration(ref Utf8JsonReader reader) =>
    reader.TokenType switch {
      JsonTokenType.Number => CanonicalTemporalFormat.DurationFromMicroseconds(reader.GetInt64()),
      JsonTokenType.String => CanonicalTemporalRenderings.Duration(reader.GetString()!),
      _ => throw CanonicalTemporalRenderings.Unexpected(nameof(TimeSpan), reader.TokenType),
    };
}
