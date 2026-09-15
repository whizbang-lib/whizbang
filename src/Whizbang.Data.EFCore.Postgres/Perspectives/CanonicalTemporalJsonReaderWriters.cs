using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Entity Framework's JSON reader/writers for the canonical temporal form, one per kind, each
/// reading through <see cref="CanonicalTemporalReaders"/>.
/// </summary>
/// <remarks>
/// <para>
/// A value conversion tells Entity Framework what a temporal is stored as; it does not decide how
/// the stored value is read out of the document. Left to itself, Entity Framework reads the number
/// with its own integer reader, which refuses a rendering the serializer's readers would take and
/// refuses an unexpected token with a generic error naming the token and nothing else. These put
/// the serializer's reader on the mapped path, so a mapped document is read exactly as an opaque
/// one is and refused in the same words, which is what lets a failure be classified downstream.
/// </para>
/// <para>
/// Writing is the number in the canonical unit, the same value the conversion produces.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CanonicalTemporalConventionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/CanonicalTemporalStorageTests.cs</tests>
public static class CanonicalTemporalJsonReaderWriters {
  /// <summary>An instant, as microseconds since the Unix epoch.</summary>
  public sealed class Instant : JsonValueReaderWriter<DateTime> {
    /// <summary>The single instance Entity Framework uses.</summary>
    public static Instant Instance { get; } = new();

    private Instant() {
    }

    /// <inheritdoc/>
    public override DateTime FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) =>
      CanonicalTemporalReaders.Instant(ref manager.CurrentReader);

    /// <inheritdoc/>
    public override void ToJsonTyped(Utf8JsonWriter writer, DateTime value) =>
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochMicroseconds(value));

    /// <inheritdoc/>
    public override Expression ConstructorExpression =>
      Expression.Property(null, typeof(Instant), nameof(Instance));
  }

  /// <summary>An instant carrying an offset, reduced to the instant it names.</summary>
  public sealed class OffsetInstant : JsonValueReaderWriter<DateTimeOffset> {
    /// <summary>The single instance Entity Framework uses.</summary>
    public static OffsetInstant Instance { get; } = new();

    private OffsetInstant() {
    }

    /// <inheritdoc/>
    public override DateTimeOffset FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) =>
      CanonicalTemporalReaders.OffsetInstant(ref manager.CurrentReader);

    /// <inheritdoc/>
    public override void ToJsonTyped(Utf8JsonWriter writer, DateTimeOffset value) =>
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochMicroseconds(value));

    /// <inheritdoc/>
    public override Expression ConstructorExpression =>
      Expression.Property(null, typeof(OffsetInstant), nameof(Instance));
  }

  /// <summary>A date without a time, as microseconds since the Unix epoch at its midnight.</summary>
  public sealed class Day : JsonValueReaderWriter<DateOnly> {
    /// <summary>The single instance Entity Framework uses.</summary>
    public static Day Instance { get; } = new();

    private Day() {
    }

    /// <inheritdoc/>
    public override DateOnly FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) =>
      CanonicalTemporalReaders.Day(ref manager.CurrentReader);

    /// <inheritdoc/>
    public override void ToJsonTyped(Utf8JsonWriter writer, DateOnly value) =>
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochMicroseconds(value));

    /// <inheritdoc/>
    public override Expression ConstructorExpression =>
      Expression.Property(null, typeof(Day), nameof(Instance));
  }

  /// <summary>A time of day, as microseconds since midnight.</summary>
  public sealed class TimeOfDay : JsonValueReaderWriter<TimeOnly> {
    /// <summary>The single instance Entity Framework uses.</summary>
    public static TimeOfDay Instance { get; } = new();

    private TimeOfDay() {
    }

    /// <inheritdoc/>
    public override TimeOnly FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) =>
      CanonicalTemporalReaders.TimeOfDay(ref manager.CurrentReader);

    /// <inheritdoc/>
    public override void ToJsonTyped(Utf8JsonWriter writer, TimeOnly value) =>
      writer.WriteNumberValue(CanonicalTemporalFormat.ToMicrosecondsOfDay(value));

    /// <inheritdoc/>
    public override Expression ConstructorExpression =>
      Expression.Property(null, typeof(TimeOfDay), nameof(Instance));
  }

  /// <summary>A duration, as microseconds.</summary>
  public sealed class Duration : JsonValueReaderWriter<TimeSpan> {
    /// <summary>The single instance Entity Framework uses.</summary>
    public static Duration Instance { get; } = new();

    private Duration() {
    }

    /// <inheritdoc/>
    public override TimeSpan FromJsonTyped(ref Utf8JsonReaderManager manager, object? existingObject = null) =>
      CanonicalTemporalReaders.Duration(ref manager.CurrentReader);

    /// <inheritdoc/>
    public override void ToJsonTyped(Utf8JsonWriter writer, TimeSpan value) =>
      writer.WriteNumberValue(CanonicalTemporalFormat.ToMicroseconds(value));

    /// <inheritdoc/>
    public override Expression ConstructorExpression =>
      Expression.Property(null, typeof(Duration), nameof(Instance));
  }

  /// <summary>The reader/writer for a kind, or null when the kind is none.</summary>
  /// <param name="kind">The temporal kind.</param>
  /// <returns>The reader/writer type Entity Framework instantiates, or <see langword="null"/>.</returns>
  public static Type? TypeFor(StoredTemporalKind? kind) => kind switch {
    StoredTemporalKind.Instant => typeof(Instant),
    StoredTemporalKind.OffsetInstant => typeof(OffsetInstant),
    StoredTemporalKind.Day => typeof(Day),
    StoredTemporalKind.TimeOfDay => typeof(TimeOfDay),
    StoredTemporalKind.Duration => typeof(Duration),
    _ => null,
  };
}
