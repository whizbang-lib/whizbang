using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Which of a perspective model's properties hold a date, a time or a duration, and how each is
/// stored as a number.
/// </summary>
/// <remarks>
/// Shared, because three things have to agree about it and disagreement between any two is a silent
/// wrong answer: the configuration that writes the value, the backfill that rewrites rows written
/// before it, and the index built over the result.
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Generators.Tests/CanonicalTemporalConfigurationTests.cs</tests>
public enum CanonicalTemporalKind {
  /// <summary>Not a temporal value.</summary>
  None = 0,

  /// <summary>An instant, stored as microseconds since the Unix epoch.</summary>
  Instant = 1,

  /// <summary>An instant carrying an offset, normalized to the instant and stored the same way.</summary>
  OffsetInstant = 2,

  /// <summary>A date without a time, stored as days since the Unix epoch.</summary>
  Day = 3,

  /// <summary>A time of day, stored as microseconds since midnight.</summary>
  TimeOfDay = 4,

  /// <summary>A duration, stored as its tick count.</summary>
  Duration = 5,
}

/// <summary>
/// A property whose value is stored as a number rather than as a rendering.
/// </summary>
/// <param name="PropertyName">The property's name, which is also the document key.</param>
/// <param name="Kind">Which temporal shape it holds.</param>
/// <param name="IsNullable">Whether the property is optional.</param>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public sealed record CanonicalTemporalProperty(
    string PropertyName,
    CanonicalTemporalKind Kind,
    bool IsNullable
);

/// <summary>
/// Finds the temporal properties of a perspective model and renders their configuration.
/// </summary>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Generators.Tests/CanonicalTemporalConfigurationTests.cs</tests>
public static class CanonicalTemporalDiscovery {
  private const string FORMAT = "global::Whizbang.Core.Perspectives.CanonicalTemporalFormat";

  /// <summary>Which temporal shape a type holds, if any.</summary>
  /// <param name="type">The property's type.</param>
  /// <returns>The shape, or none.</returns>
  public static CanonicalTemporalKind KindOf(ITypeSymbol? type) {
    if (type is null) {
      return CanonicalTemporalKind.None;
    }

    if (type is INamedTypeSymbol { IsGenericType: true } nullable
        && nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T) {
      type = nullable.TypeArguments[0];
    }

    if (type.SpecialType == SpecialType.System_DateTime) {
      return CanonicalTemporalKind.Instant;
    }

    return TypeNameUtilities.Display(type) switch {
      "System.DateTimeOffset" => CanonicalTemporalKind.OffsetInstant,
      "System.DateOnly" => CanonicalTemporalKind.Day,
      "System.TimeOnly" => CanonicalTemporalKind.TimeOfDay,
      "System.TimeSpan" => CanonicalTemporalKind.Duration,
      _ => CanonicalTemporalKind.None,
    };
  }

  /// <summary>
  /// Every temporal property directly on a model.
  /// </summary>
  /// <param name="model">The perspective's model type.</param>
  /// <returns>One entry per property, empty when the model holds none.</returns>
  /// <remarks>
  /// <para>
  /// Only properties directly on the model, not those inside a nested complex type. That is a
  /// limitation rather than a decision, and it is safe in the direction that matters: a nested date
  /// keeps the rendering it has always had, so it is stored correctly and read correctly and simply
  /// cannot carry an index. Nothing about it becomes wrong.
  /// </para>
  /// <para>
  /// A property promoted to a real column is skipped, because it is stored in a column of its own
  /// typed for it, where none of this applies.
  /// </para>
  /// </remarks>
  public static ImmutableArray<CanonicalTemporalProperty> From(INamedTypeSymbol? model) =>
    _from(model, mappableOnly: false);

  /// <summary>
  /// The temporal properties Entity Framework can be told to convert.
  /// </summary>
  /// <param name="model">The perspective's model type.</param>
  /// <returns>The subset of <see cref="From"/> that Entity Framework is able to map.</returns>
  /// <remarks>
  /// <para>
  /// Narrower than <see cref="From"/>, and deliberately so. The serializer writes every temporal
  /// property including a computed one, and the backfill rewrites what the serializer wrote, so both
  /// of those work from the full set. Entity Framework maps only what it can write back, and naming
  /// anything else in the configuration is what makes it try: the model then fails to validate, which
  /// takes the service down at startup rather than affecting one query.
  /// </para>
  /// <para>
  /// Keeping the stored form decided by one set and the mapping by another is what stops this fix
  /// from quietly changing how a derived value is written.
  /// </para>
  /// </remarks>
  public static ImmutableArray<CanonicalTemporalProperty> MappableFrom(INamedTypeSymbol? model) =>
    _from(model, mappableOnly: true);

  private static ImmutableArray<CanonicalTemporalProperty> _from(INamedTypeSymbol? model, bool mappableOnly) {
    if (model is null) {
      return [];
    }

    var found = new List<CanonicalTemporalProperty>();

    foreach (var property in model.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic)) {
      if (property.GetAttributes().Any(a =>
          TypeNameUtilities.IsNamed(a.AttributeClass, "Whizbang.Core.Perspectives.PhysicalFieldAttribute")
          || TypeNameUtilities.IsNamed(a.AttributeClass, "Whizbang.Core.Perspectives.VectorFieldAttribute"))) {
        continue;
      }

      if (mappableOnly && !_canBeMapped(property)) {
        continue;
      }

      var kind = KindOf(property.Type);
      if (kind == CanonicalTemporalKind.None) {
        continue;
      }

      var nullable = property.Type is INamedTypeSymbol { IsGenericType: true } n
          && n.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

      found.Add(new CanonicalTemporalProperty(property.Name, kind, nullable));
    }

    return [.. found];
  }

  /// <summary>
  /// Whether Entity Framework can map this property, and so whether configuring it is safe.
  /// </summary>
  /// <param name="property">The property to judge.</param>
  /// <returns><c>true</c> when the property is storage rather than a calculation.</returns>
  /// <remarks>
  /// <para>
  /// Two shapes are storage. One has a setter. The other is an automatic property declared get-only,
  /// which is ordinary for an immutable model: the compiler gives it a backing field and the
  /// constructor writes it, so Entity Framework maps it like any other value.
  /// </para>
  /// <para>
  /// A calculation has neither, and Entity Framework leaves such a property unmapped by convention.
  /// Naming it explicitly overrides that convention and turns a property it was happy to ignore into
  /// one it must map and cannot, failing model validation for the whole context.
  /// </para>
  /// <para>
  /// A property excluded with <c>[NotMapped]</c> is skipped for the same reason from the other
  /// direction: the author has said it is not storage, and configuring it contradicts them.
  /// </para>
  /// <para>
  /// The derived value is still written to the document, because the serializer writes read-only
  /// properties, and that is correct: nothing has to reconstruct it on the way back in.
  /// </para>
  /// </remarks>
  private static bool _canBeMapped(IPropertySymbol property) {
    if (property.GetAttributes().Any(a => TypeNameUtilities.IsNamed(
        a.AttributeClass, "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute"))) {
      return false;
    }

    return property.SetMethod is not null
      || property.ContainingType.GetMembers()
          .OfType<IFieldSymbol>()
          .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));
  }

  /// <summary>
  /// The configuration lines that make a model's temporal properties store as numbers.
  /// </summary>
  /// <param name="properties">The properties discovered on the model.</param>
  /// <param name="builder">The complex-property builder's parameter name in the generated code.</param>
  /// <returns>One configuration statement per property.</returns>
  /// <remarks>
  /// Each calls the framework's own conversion rather than inlining an expression. The stored form is
  /// a compatibility contract, and one definition that every caller shares is what keeps the writer,
  /// the reader and the backfill from drifting apart.
  /// </remarks>
  public static IEnumerable<string> ConfigurationFor(
      ImmutableArray<CanonicalTemporalProperty> properties, string builder) {
    foreach (var property in properties) {
      var (clrType, toProvider, fromProvider) = _conversion(property);
      var storedType = property.Kind == CanonicalTemporalKind.Day ? "int" : "long";

      var model = property.IsNullable ? $"{clrType}?" : clrType;
      var stored = property.IsNullable ? $"{storedType}?" : storedType;

      // A nullable value converts through its underlying type, and a null is simply absent.
      var to = property.IsNullable
        ? $"v => v == null ? ({stored})null : {toProvider}(v.Value)"
        : $"v => {toProvider}(v)";
      var from = property.IsNullable
        ? $"v => v == null ? ({model})null : {fromProvider}(v.Value)"
        : $"v => {fromProvider}(v)";

      yield return
        $"{builder}.Property(p => p.{property.PropertyName}).HasConversion<{stored}>({to}, {from});";
    }
  }

  private static (string ClrType, string ToProvider, string FromProvider) _conversion(
      CanonicalTemporalProperty property) => property.Kind switch {
        CanonicalTemporalKind.Instant =>
          ("global::System.DateTime", $"{FORMAT}.ToEpochMicroseconds", $"{FORMAT}.FromEpochMicroseconds"),
        CanonicalTemporalKind.OffsetInstant =>
          ("global::System.DateTimeOffset", $"{FORMAT}.ToEpochMicroseconds", $"{FORMAT}.OffsetFromEpochMicroseconds"),
        CanonicalTemporalKind.Day =>
          ("global::System.DateOnly", $"{FORMAT}.ToEpochDays", $"{FORMAT}.FromEpochDays"),
        CanonicalTemporalKind.TimeOfDay =>
          ("global::System.TimeOnly", $"{FORMAT}.ToMicrosecondsOfDay", $"{FORMAT}.FromMicrosecondsOfDay"),
        _ => ("global::System.TimeSpan", $"{FORMAT}.ToTicks", $"{FORMAT}.FromTicks"),
      };
}
