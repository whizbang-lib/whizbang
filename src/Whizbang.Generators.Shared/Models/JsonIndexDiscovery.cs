using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Finds the JSON-only fields a perspective model declares an index for, and what to build it over.
/// </summary>
/// <remarks>
/// Shared by the generators that emit perspective schema, so the cast a field is indexed with is
/// decided once. Two generators disagreeing about that would produce two indexes where one is never
/// used, and nothing would fail.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/JsonIndexGenerationTests.cs</tests>
public static class JsonIndexDiscovery {
  private const string JSON_INDEXED = "Whizbang.Core.Perspectives.JsonIndexedAttribute";
  private const string INDEX_ALL_FIELDS = "Whizbang.Core.Perspectives.IndexAllFieldsAttribute";
  private const string PHYSICAL_FIELD = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string VECTOR_FIELD = "Whizbang.Core.Perspectives.VectorFieldAttribute";

  /// <summary>The kinds as the attribute's flag enumeration spells them.</summary>
  private const int KIND_BTREE = 1;
  private const int KIND_TRIGRAM = 2;

  /// <summary>
  /// The store type a field's extraction is cast to, or null when its extraction cannot carry an
  /// index at all.
  /// </summary>
  /// <param name="type">The property's type.</param>
  /// <returns>The cast, or null when the type is not indexable this way.</returns>
  /// <remarks>
  /// <para>
  /// Each entry mirrors the cast Entity Framework emits when it extracts that type, because an index
  /// over any other expression is a different index and goes unused. Every one of them is asserted
  /// against a real query and a real plan in
  /// <c>JsonIndexUsageTests.TheGeneratedIndexExpression_IsTheOneAQueryUsesAsync</c>.
  /// </para>
  /// <para>
  /// Null is the answer for the whole date and time family, and not for want of trying: the cast out
  /// of text to a timestamp or a date is stable rather than immutable, so PostgreSQL refuses to build
  /// an index over it. Those become indexable once their stored form is a number, which is the
  /// canonical-format work in plans/lens-full-index-coverage.md.
  /// </para>
  /// </remarks>
  public static JsonIndexCast? CastFor(ITypeSymbol? type) {
    if (type is null) {
      return null;
    }

    // A nullable value is stored and extracted as its underlying type; the null simply has no entry.
    if (type is INamedTypeSymbol { IsGenericType: true } nullable
        && nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T) {
      type = nullable.TypeArguments[0];
    }

    // An enumeration is stored as its underlying number and extracted as that number's type.
    if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }) {
      type = underlying;
    }

    switch (type.SpecialType) {
      case SpecialType.System_String:
        return JsonIndexCast.None;
      case SpecialType.System_Boolean:
        return JsonIndexCast.Bool;
      case SpecialType.System_Byte:
      case SpecialType.System_SByte:
      case SpecialType.System_Int16:
        return JsonIndexCast.Int2;
      case SpecialType.System_Int32:
      case SpecialType.System_UInt16:
        return JsonIndexCast.Int4;
      case SpecialType.System_Int64:
      case SpecialType.System_UInt32:
        return JsonIndexCast.Int8;
      case SpecialType.System_Decimal:
        return JsonIndexCast.Numeric;
      case SpecialType.System_Single:
        return JsonIndexCast.Float4;
      case SpecialType.System_Double:
        return JsonIndexCast.Float8;
      default:
        return _castForNamedType(type);
    }
  }

  /// <summary>
  /// The cast for a type the special-type switch does not name.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The date family is here because it is stored as a number rather than as a rendering. That is
  /// what makes it indexable at all: the cast from text to a timestamp or a date is STABLE, and
  /// PostgreSQL refuses to build an index over a stable expression because a key computed from a
  /// session setting could go stale. A cast to an integer is IMMUTABLE, so the same extraction
  /// becomes indexable without anything about the index rules changing.
  /// </para>
  /// <para>
  /// The widths follow the stored form exactly: microseconds since the epoch, the epoch day count,
  /// microseconds since midnight, and a tick count. All but the day count need eight bytes. A cast
  /// narrower than the stored value would silently fail to match rows at the extremes, so these
  /// agree with <c>CanonicalTemporalFormat</c> by construction rather than by being kept in step.
  /// </para>
  /// </remarks>
  private static JsonIndexCast? _castForNamedType(ITypeSymbol type) {
    if (TypeNameUtilities.IsNamed(type, "System.Guid")) {
      return JsonIndexCast.Uuid;
    }

    return CanonicalTemporalDiscovery.KindOf(type) switch {
      CanonicalTemporalKind.None => null,
      CanonicalTemporalKind.Day => JsonIndexCast.Int4,
      _ => JsonIndexCast.Int8,
    };
  }

  /// <summary>
  /// The index kinds a property declares for itself, or null when it declares nothing.
  /// </summary>
  /// <param name="property">The property to inspect.</param>
  /// <returns>The combined kinds, zero when the property opts out, or null when it is silent.</returns>
  /// <remarks>
  /// <para>
  /// Zero and null are different answers and the difference is the whole point. Null means the
  /// property said nothing, so a model-level declaration still applies to it. Zero means it asked
  /// for no index, which overrides the model and is the only way to say "every field but this one".
  /// </para>
  /// <para>
  /// Asked here rather than in each caller because three of them need it: the discovery that emits
  /// the index, the diagnostic that reports one that cannot be built, and the advisory that stops
  /// reporting a field once it carries one. Three copies would be three chances to disagree about
  /// whether an opt-out counts as a declaration.
  /// </para>
  /// </remarks>
  public static int? DeclaredKind(IPropertySymbol? property) {
    if (property is null) {
      return null;
    }

    var declared = property.GetAttributes()
        .Where(a => TypeNameUtilities.IsNamed(a.AttributeClass, JSON_INDEXED))
        .ToList();

    // Repeated declarations combine, which is what makes writing the attribute twice meaningful
    // rather than merely allowed.
    return declared.Count == 0
      ? null
      : declared.Aggregate(0, (acc, a) => acc | _kindOf(a, KIND_BTREE));
  }

  /// <summary>
  /// Every JSON-only field on a model that carries a declared index.
  /// </summary>
  /// <param name="model">The perspective's model type.</param>
  /// <returns>One entry per field, empty when nothing is declared.</returns>
  /// <remarks>
  /// <para>
  /// A field promoted to a physical column is skipped: it already has a real column and its index is
  /// emitted for that, and an index over the document extraction of a field that may not even be in
  /// the document would be so much dead weight.
  /// </para>
  /// <para>
  /// A blanket declaration on the model includes only the fields whose extraction can carry an index,
  /// and skips the rest silently. A declaration per field is a claim about that field, so a type that
  /// cannot carry one is worth reporting; a blanket one is not a claim about any particular field, so
  /// reporting each skip would be noise.
  /// </para>
  /// </remarks>
  public static ImmutableArray<JsonIndexInfo> From(INamedTypeSymbol? model) {
    if (model is null) {
      return ImmutableArray<JsonIndexInfo>.Empty;
    }

    var blanket = model.GetAttributes()
        .FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, INDEX_ALL_FIELDS));
    var blanketKind = blanket is null ? 0 : _kindOf(blanket, KIND_BTREE);

    var found = new List<JsonIndexInfo>();

    foreach (var property in model.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic)) {
      var attributes = property.GetAttributes();

      // Promoted fields carry their own column and their own index.
      if (attributes.Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, PHYSICAL_FIELD)
                              || TypeNameUtilities.IsNamed(a.AttributeClass, VECTOR_FIELD))) {
        continue;
      }

      var kind = DeclaredKind(property) ?? blanketKind;

      if (kind == 0) {
        continue;
      }

      var cast = CastFor(property.Type);
      if (cast is null) {
        continue;
      }

      // A trigram index is over text and means nothing over anything else.
      var trigram = (kind & KIND_TRIGRAM) != 0 && cast == JsonIndexCast.None;

      found.Add(new JsonIndexInfo(
          PropertyName: property.Name,
          JsonKey: property.Name,
          Cast: cast.Value,
          Btree: (kind & KIND_BTREE) != 0,
          Trigram: trigram));
    }

    return found.Count == 0 ? ImmutableArray<JsonIndexInfo>.Empty : found.ToImmutableArray();
  }

  /// <summary>
  /// Whether a field declares an index the extraction cannot carry, which is worth reporting because
  /// the declaration is a claim about that specific field.
  /// </summary>
  /// <param name="model">The perspective's model type.</param>
  /// <returns>The properties that asked for an index and cannot have one.</returns>
  public static ImmutableArray<IPropertySymbol> UnindexableDeclarations(INamedTypeSymbol? model) {
    if (model is null) {
      return ImmutableArray<IPropertySymbol>.Empty;
    }

    var offenders = model.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => !p.IsStatic)
        .Where(p => p.GetAttributes().Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, JSON_INDEXED)))
        .Where(p => CastFor(p.Type) is null)
        .ToList();

    return offenders.Count == 0 ? ImmutableArray<IPropertySymbol>.Empty : offenders.ToImmutableArray();
  }

  /// <summary>The kind argument of a declaration, positional or named, defaulting to btree.</summary>
  private static int _kindOf(AttributeData attribute, int fallback) {
    if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int positional) {
      return positional;
    }

    foreach (var named in attribute.NamedArguments) {
      if (named.Key == "Kind" && named.Value.Value is int value) {
        return value;
      }
    }

    return fallback;
  }
}
