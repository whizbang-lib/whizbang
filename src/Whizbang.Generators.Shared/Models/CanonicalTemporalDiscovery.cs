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

  /// <summary>A date without a time, stored as microseconds since the Unix epoch at its midnight.</summary>
  Day = 3,

  /// <summary>A time of day, stored as microseconds since midnight.</summary>
  TimeOfDay = 4,

  /// <summary>A duration, stored as microseconds.</summary>
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
/// Finds the temporal properties of a perspective model, for the backfill that rewrites rows written
/// before the canonical form and the index built over the result.
/// </summary>
/// <remarks>
/// Not for the mapping. What Entity Framework converts is decided by a convention walking the model
/// Entity Framework built, which reaches placements a discovery over a type's own members does not;
/// naming a subset of them in the configuration was how the two sides came to disagree.
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Generators.Tests/CanonicalTemporalConfigurationTests.cs</tests>
public static class CanonicalTemporalDiscovery {
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
  public static ImmutableArray<CanonicalTemporalProperty> From(INamedTypeSymbol? model) {
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

}
