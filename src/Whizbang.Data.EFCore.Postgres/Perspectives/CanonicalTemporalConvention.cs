using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Converts every date, time and duration Entity Framework maps inside a JSON document to the
/// canonical stored form, by walking the model Entity Framework built.
/// </summary>
/// <remarks>
/// <para>
/// A perspective document has two writers and they have to agree. The serializer converts every
/// temporal in a document it writes under the persistence profile. If Entity Framework converted
/// only the properties a generator had discovered, every property the generator missed would be
/// written as a number and read as a rendering, and the row would be unreadable. So the model
/// Entity Framework built is what decides what Entity Framework converts: a member inherited from a
/// base class, a nested object, an element of a complex collection and the framework's own metadata
/// are all reached because Entity Framework's own walk reaches them.
/// </para>
/// <para>
/// Only inside a document. A timestamp column is typed for what it holds, and a complex type spread
/// over columns has a typed column per property; the stored form is a property of the document,
/// not of the type.
/// </para>
/// <para>
/// Each converted property also reads and writes through the reader/writer for its kind
/// (<see cref="CanonicalTemporalJsonReaderWriters"/>), so the mapped path reads a document the
/// way the serializer's converters read an opaque one: a number in the canonical unit or, for
/// now, a rendering, and a refusal of anything else in the same words on both paths.
/// </para>
/// <para>
/// A conversion the model author configured explicitly wins, because a convention configures at
/// convention precedence and Entity Framework does not let it override an explicit one.
/// </para>
/// <para>
/// The convention rides the Whizbang options extension, and a context built by hand from a plain
/// connection string, as a lens context often is, does not carry it. So the generated
/// OnModelCreating also calls <see cref="Apply(ModelBuilder)"/>, the same walk over the model as
/// built, after the consumer's own configuration. A context that maps the perspectives converts
/// them whatever options it was built with; the two applications agree and the second is a no-op.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CanonicalTemporalConventionTests.cs</tests>
public sealed class CanonicalTemporalConvention : IModelFinalizingConvention {
  private static readonly ValueConverter<DateTime, long> _instant = new(
    v => CanonicalTemporalFormat.ToEpochMicroseconds(v),
    v => CanonicalTemporalFormat.FromEpochMicroseconds(v));

  private static readonly ValueConverter<DateTimeOffset, long> _offsetInstant = new(
    v => CanonicalTemporalFormat.ToEpochMicroseconds(v),
    v => CanonicalTemporalFormat.OffsetFromEpochMicroseconds(v));

  private static readonly ValueConverter<DateOnly, long> _day = new(
    v => CanonicalTemporalFormat.ToEpochMicroseconds(v),
    v => CanonicalTemporalFormat.DayFromEpochMicroseconds(v));

  private static readonly ValueConverter<TimeOnly, long> _timeOfDay = new(
    v => CanonicalTemporalFormat.ToMicrosecondsOfDay(v),
    v => CanonicalTemporalFormat.FromMicrosecondsOfDay(v));

  private static readonly ValueConverter<TimeSpan, long> _duration = new(
    v => CanonicalTemporalFormat.ToMicroseconds(v),
    v => CanonicalTemporalFormat.DurationFromMicroseconds(v));

  /// <inheritdoc/>
  public void ProcessModelFinalizing(
      IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context) {
    ArgumentNullException.ThrowIfNull(modelBuilder);

    foreach (var entityType in modelBuilder.Metadata.GetEntityTypes()) {
      foreach (var complex in entityType.GetComplexProperties()) {
        _convert(complex, inDocument: false);
      }
    }
  }

  /// <summary>
  /// Applies the conversion to every temporal the model as built so far maps inside a document,
  /// as explicit configuration; called by the generated OnModelCreating after the consumer's own.
  /// </summary>
  /// <remarks>
  /// A property the model author already converted is left alone. Explicit configuration is what
  /// the finalizing convention then respects, so a context that carries the options extension as
  /// well converts once, not twice.
  /// </remarks>
  /// <param name="modelBuilder">The builder, after every entity and complex property is configured.</param>
  public static void Apply(ModelBuilder modelBuilder) {
    ArgumentNullException.ThrowIfNull(modelBuilder);

    foreach (var entityType in modelBuilder.Model.GetEntityTypes()) {
      foreach (var complex in entityType.GetComplexProperties()) {
        _apply(complex, inDocument: false);
      }
    }
  }

  private static void _apply(IMutableComplexProperty complex, bool inDocument) {
    inDocument = inDocument || complex.ComplexType.IsMappedToJson();

    if (inDocument) {
      foreach (var property in complex.ComplexType.GetProperties()) {
        var kind = KindOf(property.ClrType);
        var converter = _converterFor(kind);
        if (converter is not null && property.GetValueConverter() is null) {
          property.SetValueConverter(converter);
          property.SetJsonValueReaderWriterType(CanonicalTemporalJsonReaderWriters.TypeFor(kind));
        }
      }
    }

    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _apply(nested, inDocument);
    }
  }

  /// <summary>Which kind of temporal a CLR type is, optional or not, or null when it is none.</summary>
  /// <param name="clrType">The property's CLR type.</param>
  /// <returns>The kind, or <see langword="null"/>.</returns>
  public static StoredTemporalKind? KindOf(Type clrType) {
    ArgumentNullException.ThrowIfNull(clrType);
    var bare = Nullable.GetUnderlyingType(clrType) ?? clrType;

    if (bare == typeof(DateTime)) {
      return StoredTemporalKind.Instant;
    }
    if (bare == typeof(DateTimeOffset)) {
      return StoredTemporalKind.OffsetInstant;
    }
    if (bare == typeof(DateOnly)) {
      return StoredTemporalKind.Day;
    }
    if (bare == typeof(TimeOnly)) {
      return StoredTemporalKind.TimeOfDay;
    }

    return bare == typeof(TimeSpan) ? StoredTemporalKind.Duration : null;
  }

  private static void _convert(IConventionComplexProperty complex, bool inDocument) {
    // Once inside a document, everything beneath is in it: a nested complex type shares its
    // container column. A complex type mapped to columns is descended for completeness and
    // converts nothing.
    inDocument = inDocument || complex.ComplexType.IsMappedToJson();

    if (inDocument) {
      foreach (var property in complex.ComplexType.GetProperties()) {
        var kind = KindOf(property.ClrType);
        var converter = _converterFor(kind);
        if (converter is not null) {
          property.Builder.HasConversion(converter, fromDataAnnotation: false);
          property.SetJsonValueReaderWriterType(CanonicalTemporalJsonReaderWriters.TypeFor(kind), fromDataAnnotation: false);
        }
      }
    }

    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _convert(nested, inDocument);
    }
  }

  private static ValueConverter? _converterFor(StoredTemporalKind? kind) => kind switch {
    StoredTemporalKind.Instant => _instant,
    StoredTemporalKind.OffsetInstant => _offsetInstant,
    StoredTemporalKind.Day => _day,
    StoredTemporalKind.TimeOfDay => _timeOfDay,
    StoredTemporalKind.Duration => _duration,
    _ => null,
  };
}

/// <summary>
/// Adds <see cref="CanonicalTemporalConvention"/> to the convention set of every context that
/// carries the Whizbang options extension, which every generated perspective context does.
/// </summary>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CanonicalTemporalConventionTests.cs</tests>
public sealed class CanonicalTemporalConventionSetPlugin : IConventionSetPlugin {
  /// <inheritdoc/>
  public ConventionSet ModifyConventions(ConventionSet conventionSet) {
    ArgumentNullException.ThrowIfNull(conventionSet);
    conventionSet.ModelFinalizingConventions.Add(new CanonicalTemporalConvention());
    return conventionSet;
  }
}
