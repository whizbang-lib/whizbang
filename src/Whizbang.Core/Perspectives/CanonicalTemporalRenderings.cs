using System.Globalization;
using System.Text.Json;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The renderings a canonical reader still accepts, and what it says when it accepts nothing.
/// </summary>
/// <remarks>
/// <para>
/// A rendering is the form a temporal had before the canonical one: ISO 8601 for an instant, a date
/// or a time, the constant format for a duration, and the two words PostgreSQL uses for the ends
/// of time. Every one of these is unambiguous, which is what makes reading it safe. A number is
/// never reinterpreted here; a number is the canonical unit and nothing else.
/// </para>
/// <para>
/// Every rendering read is reported to <see cref="StoredFormFallbacks"/> before it is
/// parsed, so a rendering that fails to parse is still counted as one that was found.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalReaderToleranceTests.cs</tests>
internal static class CanonicalTemporalRenderings {
  private const string INFINITY = "infinity";
  private const string NEGATIVE_INFINITY = "-infinity";
  private const DateTimeStyles UTC_STYLES = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;

  /// <summary>An instant from its rendering, as UTC.</summary>
  public static DateTime Instant(string rendering) {
    StoredFormFallbacks.RenderingRead(StoredTemporalKind.Instant);
    return rendering switch {
      "" => default,
      INFINITY => DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
      NEGATIVE_INFINITY => DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
      _ => DateTime.TryParse(rendering, CultureInfo.InvariantCulture, UTC_STYLES, out var value)
        ? value
        : throw _unreadable(nameof(DateTime), rendering),
    };
  }

  /// <summary>An offset instant from its rendering, keeping the offset the rendering carried.</summary>
  public static DateTimeOffset OffsetInstant(string rendering) {
    StoredFormFallbacks.RenderingRead(StoredTemporalKind.OffsetInstant);
    return rendering switch {
      "" => default,
      INFINITY => DateTimeOffset.MaxValue,
      NEGATIVE_INFINITY => DateTimeOffset.MinValue,
      _ => DateTimeOffset.TryParse(rendering, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
        ? value
        : throw _unreadable(nameof(DateTimeOffset), rendering),
    };
  }

  /// <summary>A date from its rendering, or from the rendering of an instant on that date.</summary>
  public static DateOnly Day(string rendering) {
    StoredFormFallbacks.RenderingRead(StoredTemporalKind.Day);
    if (rendering.Length == 0) {
      return default;
    }
    if (DateOnly.TryParse(rendering, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) {
      return day;
    }

    return DateTime.TryParse(rendering, CultureInfo.InvariantCulture, UTC_STYLES, out var instant)
      ? DateOnly.FromDateTime(instant)
      : throw _unreadable(nameof(DateOnly), rendering);
  }

  /// <summary>A time of day from its rendering.</summary>
  public static TimeOnly TimeOfDay(string rendering) {
    StoredFormFallbacks.RenderingRead(StoredTemporalKind.TimeOfDay);
    if (rendering.Length == 0) {
      return default;
    }

    return TimeOnly.TryParse(rendering, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
      ? value
      : throw _unreadable(nameof(TimeOnly), rendering);
  }

  /// <summary>A duration from its rendering.</summary>
  public static TimeSpan Duration(string rendering) {
    StoredFormFallbacks.RenderingRead(StoredTemporalKind.Duration);
    if (rendering.Length == 0) {
      return default;
    }

    return TimeSpan.TryParse(rendering, CultureInfo.InvariantCulture, out var value)
      ? value
      : throw _unreadable(nameof(TimeSpan), rendering);
  }

  /// <summary>
  /// The refusal for a token that is neither a number nor a rendering, naming what was found and
  /// what would have been taken. The serializer appends the path.
  /// </summary>
  public static JsonException Unexpected(string clrType, JsonTokenType found) =>
    new($"A stored {clrType} must be a number (microseconds) or a rendering, but the document holds {found}");

  private static JsonException _unreadable(string clrType, string rendering) =>
    new($"A stored {clrType} rendering could not be read: '{rendering}'");
}
