using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The serializer's side of the canonical temporal form.
/// </summary>
/// <remarks>
/// <para>
/// A perspective document has two writers and they have to agree. Entity Framework's mapping is what
/// reads a row back and what compiles a filter over it, and a value conversion attaches there. But a
/// row is written by the upsert, which serializes the model with System.Text.Json and sends the
/// document as a parameter, so a value conversion never sees it. Without these, the mapping would
/// read a number out of a document the writer had filled with renderings.
/// </para>
/// <para>
/// Scoped to the persistence profile, which is the perspective-document profile. The default profile
/// is transport and the event store, where a date is part of a payload other systems and older
/// releases read: changing it there would be a wire-format change, and nothing about indexing asks
/// for one.
/// </para>
/// <para>
/// This is also what gives a model stored as a single serialized value the same stored form as a
/// mapped one. It gains no index from it, since nothing inside such a document is reachable by an
/// extraction, but one stored form across both is worth more than a second one that buys nothing.
/// </para>
/// <para>
/// The values themselves come from <see cref="CanonicalTemporalFormat"/> rather than being computed
/// here. Three things have to agree about the stored form, and disagreement between any two is a
/// silent wrong answer.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalJsonConverterTests.cs</tests>
public static class CanonicalTemporalJsonConverters {
  /// <summary>An instant, as microseconds since the Unix epoch.</summary>
  public sealed class InstantConverter : JsonConverter<DateTime> {
    /// <inheritdoc/>
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalFormat.FromEpochMicroseconds(reader.GetInt64());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochMicroseconds(value));
    }
  }

  /// <summary>An instant carrying an offset, reduced to the instant it names.</summary>
  public sealed class OffsetInstantConverter : JsonConverter<DateTimeOffset> {
    /// <inheritdoc/>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalFormat.OffsetFromEpochMicroseconds(reader.GetInt64());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochMicroseconds(value));
    }
  }

  /// <summary>A date without a time, as days since the Unix epoch.</summary>
  public sealed class DayConverter : JsonConverter<DateOnly> {
    /// <inheritdoc/>
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalFormat.FromEpochDays(reader.GetInt32());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochDays(value));
    }
  }

  /// <summary>A time of day, as microseconds since midnight.</summary>
  public sealed class TimeOfDayConverter : JsonConverter<TimeOnly> {
    /// <inheritdoc/>
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalFormat.FromMicrosecondsOfDay(reader.GetInt64());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToMicrosecondsOfDay(value));
    }
  }

  /// <summary>A duration, as its tick count.</summary>
  public sealed class DurationConverter : JsonConverter<TimeSpan> {
    /// <inheritdoc/>
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalFormat.FromTicks(reader.GetInt64());

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToTicks(value));
    }
  }

  /// <summary>
  /// Every converter, in the order they are registered.
  /// </summary>
  /// <returns>One converter per temporal type.</returns>
  /// <remarks>
  /// An optional value needs no converter of its own. The serializer unwraps a nullable and applies
  /// the underlying type's converter, and a null is written as a null rather than reaching one at
  /// all, which is the behavior an absent date wants.
  /// </remarks>
  public static IReadOnlyList<JsonConverter> All() => [
    new InstantConverter(),
    new OffsetInstantConverter(),
    new DayConverter(),
    new TimeOfDayConverter(),
    new DurationConverter(),
  ];
}
