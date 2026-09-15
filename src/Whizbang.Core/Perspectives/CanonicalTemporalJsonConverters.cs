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
/// Registered on the persistence profile's options, ahead of everything else on it, so the
/// serializer applies them wherever the type occurs: a member inherited from a base class, a nested
/// object, an element of a collection, a positional record's constructor parameter, the framework's
/// own metadata. That is the same set Entity Framework's convention converts on the way back, so
/// the two sides agree by construction. They were once attached per model to a list of property
/// names a generator had discovered, and every placement that discovery missed was a place the two
/// sides could disagree.
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
/// <para>
/// Reading is strict about units and, for now, tolerant of renderings. A number is always the
/// canonical unit; a reader never guesses whether it is a day count, a tick count or a microsecond
/// count, because that question is settled by the stored form ledger before any reader sees the
/// row. A rendering is read as the value it renders, counted and announced through
/// <see cref="StoredFormFallbacks"/>, so a row the rewrite did not reach degrades to a
/// counted read rather than a stopped feature. The rendering branch goes once the count reads zero
/// across a release cycle.
/// </para>
/// <para>
/// The reading itself is <see cref="CanonicalTemporalReaders"/>, which Entity Framework's mapped
/// path calls as well, so both paths read and refuse the same values in the same words.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalJsonConverterTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalReaderToleranceTests.cs</tests>
public static class CanonicalTemporalJsonConverters {
  /// <summary>An instant, as microseconds since the Unix epoch.</summary>
  public sealed class InstantConverter : JsonConverter<DateTime> {
    /// <inheritdoc/>
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalReaders.Instant(ref reader);

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
      CanonicalTemporalReaders.OffsetInstant(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochMicroseconds(value));
    }
  }

  /// <summary>A date without a time, as microseconds since the Unix epoch at its midnight.</summary>
  public sealed class DayConverter : JsonConverter<DateOnly> {
    /// <inheritdoc/>
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalReaders.Day(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToEpochMicroseconds(value));
    }
  }

  /// <summary>A time of day, as microseconds since midnight.</summary>
  public sealed class TimeOfDayConverter : JsonConverter<TimeOnly> {
    /// <inheritdoc/>
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalReaders.TimeOfDay(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToMicrosecondsOfDay(value));
    }
  }

  /// <summary>A duration, as microseconds.</summary>
  public sealed class DurationConverter : JsonConverter<TimeSpan> {
    /// <inheritdoc/>
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      CanonicalTemporalReaders.Duration(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);
      writer.WriteNumberValue(CanonicalTemporalFormat.ToMicroseconds(value));
    }
  }

}
