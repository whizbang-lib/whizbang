using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Which of a property's types hold a date, a time or a duration, and so store as a number.
/// </summary>
/// <remarks>
/// Shared, because the index built over a temporal property has to agree with the stored form
/// about the cast it uses. The stored form itself is no longer decided here: the serializer
/// converts every temporal under the persistence profile, Entity Framework converts every temporal
/// it maps by convention, and the rewrite of older rows is derived at runtime from those two
/// readers. A discovery over a type's own members could not reach what they reach.
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
/// Tells a temporal type from any other, for the index built over it.
/// </summary>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Generators.Tests/JsonIndexGenerationTests.cs</tests>
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
}
