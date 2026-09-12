using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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

  /// <summary>
  /// Applies the canonical form to the named properties of one model type, and to nothing else.
  /// </summary>
  /// <param name="info">The resolved metadata the serializer is about to use.</param>
  /// <param name="modelType">The perspective model this applies to.</param>
  /// <param name="propertyNames">The model's temporal properties, as the generator discovered them.</param>
  /// <remarks>
  /// <para>
  /// Per property rather than per type, and that distinction is the whole reason this exists. A
  /// converter registered on the options applies to every occurrence of its type everywhere, so a
  /// canonical date reached <c>PerspectiveMetadata.Timestamp</c> as well: a framework document mapped
  /// and read by something with no matching conversion, which turned every row it wrote into one the
  /// reader could not parse.
  /// </para>
  /// <para>
  /// The names come from the same discovery that emits the model's value conversions, so the writer
  /// and the reader convert exactly the same set. Two lists would be two chances to disagree, and a
  /// disagreement here is a row nothing can read.
  /// </para>
  /// <para>
  /// No reflection: the type is compared to one the caller named and the properties are matched by
  /// the metadata's own names, so this is ahead-of-time safe.
  /// </para>
  /// </remarks>
  public static void ApplyTo(JsonTypeInfo info, Type modelType, params string[] propertyNames) {
    ArgumentNullException.ThrowIfNull(info);
    ArgumentNullException.ThrowIfNull(propertyNames);

    if (info.Type != modelType) {
      return;
    }

    foreach (var property in info.Properties) {
      if (Array.IndexOf(propertyNames, property.Name) < 0) {
        continue;
      }

      var converter = _converterFor(property.PropertyType);
      if (converter is not null) {
        property.CustomConverter = converter;
      }
    }
  }

  /// <summary>
  /// An optional value, converted through the underlying type's converter.
  /// </summary>
  /// <typeparam name="T">The underlying value type.</typeparam>
  /// <remarks>
  /// Needed only because this is attached per property. A converter handed to the options is wrapped
  /// for a nullable automatically; one assigned to a property's metadata is not, and the serializer
  /// refuses a converter whose type does not match the property's exactly.
  /// </remarks>
  public sealed class NullableConverter<T>(JsonConverter<T> inner) : JsonConverter<T?> where T : struct {
    private readonly JsonConverter<T> _inner = inner;

    /// <inheritdoc/>
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      reader.TokenType == JsonTokenType.Null ? null : _inner.Read(ref reader, typeof(T), options);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options) {
      ArgumentNullException.ThrowIfNull(writer);

      if (value is null) {
        writer.WriteNullValue();
        return;
      }

      _inner.Write(writer, value.Value, options);
    }
  }

  /// <summary>The converter for a property's type, wrapping it when the property is optional.</summary>
  private static JsonConverter? _converterFor(Type propertyType) {
    var underlying = Nullable.GetUnderlyingType(propertyType);
    var bare = underlying ?? propertyType;

    if (bare == typeof(DateTime)) {
      return underlying is null
        ? new InstantConverter()
        : new NullableConverter<DateTime>(new InstantConverter());
    }
    if (bare == typeof(DateTimeOffset)) {
      return underlying is null
        ? new OffsetInstantConverter()
        : new NullableConverter<DateTimeOffset>(new OffsetInstantConverter());
    }
    if (bare == typeof(DateOnly)) {
      return underlying is null
        ? new DayConverter()
        : new NullableConverter<DateOnly>(new DayConverter());
    }
    if (bare == typeof(TimeOnly)) {
      return underlying is null
        ? new TimeOfDayConverter()
        : new NullableConverter<TimeOnly>(new TimeOfDayConverter());
    }
    if (bare != typeof(TimeSpan)) {
      return null;
    }

    return underlying is null
      ? new DurationConverter()
      : new NullableConverter<TimeSpan>(new DurationConverter());
  }
}
