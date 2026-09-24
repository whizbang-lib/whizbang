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
  private const string JSON_INDEXED = "Whizbang.Core.Perspectives.IndexedAttribute";
  private const string INDEX_ALL_FIELDS = "Whizbang.Core.Perspectives.IndexAllFieldsAttribute";
  private const string PHYSICAL_FIELD = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string PERSPECTIVE_INDEX = "Whizbang.Core.Perspectives.PerspectiveIndexAttribute";
  private const string VECTOR_FIELD = "Whizbang.Core.Perspectives.VectorFieldAttribute";

  /// <summary>The kinds as the attribute's flag enumeration spells them.</summary>
  private const int KIND_ORDERED = 1;
  private const int KIND_SUBSTRING = 2;

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

    return type.SpecialType switch {
      SpecialType.System_String => JsonIndexCast.None,
      SpecialType.System_Boolean => JsonIndexCast.Bool,
      SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 => JsonIndexCast.Int2,
      SpecialType.System_Int32 or SpecialType.System_UInt16 => JsonIndexCast.Int4,
      SpecialType.System_Int64 or SpecialType.System_UInt32 => JsonIndexCast.Int8,
      SpecialType.System_Decimal => JsonIndexCast.Numeric,
      SpecialType.System_Single => JsonIndexCast.Float4,
      SpecialType.System_Double => JsonIndexCast.Float8,
      _ => _castForNamedType(type),
    };
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

    // One cast for the whole family, because every kind stores in the same eight-byte unit. A date
    // once cast through int4 over a day count, which baked a second unit into the schema.
    return CanonicalTemporalDiscovery.KindOf(type) switch {
      CanonicalTemporalKind.None => null,
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
  public static int? DeclaredKind(IPropertySymbol? property) => _declaredKind(property, folding: null);

  /// <summary>
  /// The index kinds a property declares for one comparison form, or null when it declares none.
  /// </summary>
  /// <param name="property">The property to inspect.</param>
  /// <param name="caseInsensitive">Which form to ask about: the folded comparison or the plain one.</param>
  /// <returns>The combined kinds declared for that form, or null when none is.</returns>
  /// <remarks>
  /// Asked per form because the fold is part of the indexed expression, so the two are separate
  /// indexes and neither answers the other's query. A field compared both ways declares the attribute
  /// twice and needs one of each; reading the kinds across both at once would collapse the pair and
  /// emit a single index, leaving one of the two comparisons scanning.
  /// </remarks>
  public static int? DeclaredKind(IPropertySymbol? property, bool caseInsensitive) =>
    _declaredKind(property, caseInsensitive);

  /// <summary>The kinds declared by the declarations matching a folding, or all of them for null.</summary>
  private static int? _declaredKind(IPropertySymbol? property, bool? folding) {
    if (property is null) {
      return null;
    }

    var declared = property.GetAttributes()
        .Where(a => TypeNameUtilities.IsNamed(a.AttributeClass, JSON_INDEXED))
        .Where(a => folding is null || _foldsCase(a) == folding.Value)
        .ToList();

    // Repeated declarations combine, which is what makes writing the attribute twice meaningful
    // rather than merely allowed.
    return declared.Count == 0
      ? null
      : declared.Aggregate(0, (acc, a) => acc | _kindOf(a, KIND_ORDERED));
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
      return [];
    }

    var blanket = model.GetAttributes()
        .FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, INDEX_ALL_FIELDS));
    var blanketKind = blanket is null ? 0 : _kindOf(blanket, KIND_ORDERED);

    var found = new List<JsonIndexInfo>();

    foreach (var property in model.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic)) {
      var attributes = property.GetAttributes();

      // Promoted fields carry their own column and their own index.
      if (attributes.Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, PHYSICAL_FIELD)
                              || TypeNameUtilities.IsNamed(a.AttributeClass, VECTOR_FIELD))) {
        continue;
      }

      var cast = CastFor(property.Type);
      if (cast is null) {
        continue;
      }

      // Substring matching and case folding are both over text and mean nothing over anything else.
      // Dropped rather than emitted here, and reported by the declaration analyzer, so the author
      // hears about it instead of getting an index that answers nothing.
      var text = cast == JsonIndexCast.None;
      var (plain, folded) = _kindsFor(property, blanketKind, text);

      if (plain != 0) {
        found.Add(_infoFor(property, cast.Value, plain, caseInsensitive: false, text));
      }

      // One entry each, because the fold is part of the expression: two comparison forms are two
      // indexes, and collapsing them into one leaves whichever form lost the collapse scanning.
      if (folded != 0) {
        found.Add(_infoFor(property, cast.Value, folded, caseInsensitive: true, text));
      }
    }

    return [.. found];
  }

  /// <summary>
  /// The kinds to build over the plain expression and over the folded one.
  /// </summary>
  /// <param name="property">The property being considered.</param>
  /// <param name="blanketKind">The kinds a model-level declaration asks for, zero when there is none.</param>
  /// <param name="text">Whether the field's stored form is text, which is the only foldable one.</param>
  /// <returns>The kinds for each expression, zero for one that needs no index.</returns>
  /// <remarks>
  /// <para>
  /// A silent property takes the model's declaration, which asks for no fold and so is answered
  /// entirely by the plain expression.
  /// </para>
  /// <para>
  /// On a field that is not text there is nothing to fold, and a declaration that asked for it still
  /// asks for the index its type can carry: the fold is dropped and its kinds join the plain
  /// expression rather than being lost with it. Dropping the whole declaration instead would turn
  /// one wrong word into no index at all.
  /// </para>
  /// </remarks>
  private static (int Plain, int Folded) _kindsFor(IPropertySymbol property, int blanketKind, bool text) {
    if (DeclaredKind(property) is null) {
      return (blanketKind, 0);
    }

    return text
      ? (DeclaredKind(property, caseInsensitive: false) ?? 0, DeclaredKind(property, caseInsensitive: true) ?? 0)
      : (DeclaredKind(property) ?? 0, 0);
  }

  /// <summary>One declared index, with the capabilities its stored form can actually answer.</summary>
  /// <remarks>
  /// A date once cast through <c>integer</c> over a day count, and now casts through <c>bigint</c>
  /// over microseconds. An index whose expression changed under the same name is never rebuilt, so
  /// the date's index is the one that carries a superseded cast: its old index is dropped and the
  /// new one takes a new name.
  /// </remarks>
  private static JsonIndexInfo _infoFor(
      IPropertySymbol property, JsonIndexCast cast, int kind, bool caseInsensitive, bool text) =>
    new(
        PropertyName: property.Name,
        JsonKey: property.Name,
        Cast: cast,
        Ordered: IncludesOrdered(kind),
        Substring: IncludesSubstring(kind) && text,
        CaseInsensitive: caseInsensitive,
        Superseded: CanonicalTemporalDiscovery.KindOf(property.Type) == CanonicalTemporalKind.Day
          ? JsonIndexCast.Int4
          : JsonIndexCast.None);

  /// <summary>
  /// The composite and partial indexes a model declares with <c>[PerspectiveIndex]</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Each named property is resolved to the SQL the index is built over: a promoted property gives
  /// its column, one in the document gives its extraction. Doing that here rather than in the
  /// renderer keeps the promoted-or-document decision in one place, and is what lets an author name
  /// a property without knowing which it is.
  /// </para>
  /// <para>
  /// An index naming a property the model does not have, or one whose type cannot carry an index,
  /// is dropped whole rather than emitted partially: a composite index missing one of its columns
  /// is a different index, and one that silently answered fewer filters would be worse than none.
  /// </para>
  /// </remarks>
  /// <param name="model">The perspective model.</param>
  /// <returns>One entry per declaration that fully resolved.</returns>
  public static ImmutableArray<CompositeIndexInfo> CompositesFrom(INamedTypeSymbol? model) {
    if (model is null) {
      return [];
    }

    var properties = model.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(static p => !p.IsStatic)
        .ToDictionary(static p => p.Name, static p => p, StringComparer.Ordinal);

    var found = new List<CompositeIndexInfo>();

    foreach (var declaration in model.GetAttributes()
        .Where(a => TypeNameUtilities.IsNamed(a.AttributeClass, PERSPECTIVE_INDEX))) {
      if (_compositeFor(declaration, properties) is { } index) {
        found.Add(index);
      }
    }

    return [.. found];
  }

  /// <summary>
  /// One declaration resolved against the model, or null when it cannot be.
  /// </summary>
  /// <remarks>
  /// Separate from the walk above so each does one thing: the walk finds declarations, this decides
  /// what one means. Inline it was a loop inside a loop inside a loop, which the quality gate
  /// measured at cognitive complexity 21.
  /// </remarks>
  /// <summary>The index methods, in the order the declaring enum defines them.</summary>
  private static readonly string[] _methodNames = ["btree", "hash", "gin", "gist", "spgist", "brin"];

  private static CompositeIndexInfo? _compositeFor(
      AttributeData declaration, Dictionary<string, IPropertySymbol> properties) {
    var elements = _elementsFor(declaration, properties);
    if (elements is null) {
      return null;
    }

    string? declaredName = null;
    string? where = null;
    string? method = null;
    string? operatorClass = null;
    var unique = false;
    var expressions = new List<string>();

    foreach (var argument in declaration.NamedArguments) {
      switch (argument.Key) {
        case "Name":
          declaredName = argument.Value.Value as string;
          break;
        case "Where":
          where = argument.Value.Value as string;
          break;
        case "Unique":
          unique = argument.Value.Value is true;
          break;
        case "OperatorClass":
          operatorClass = argument.Value.Value as string;
          break;
        case "Method":
          // Btree is the default and is left unwritten, so an index that never asked for a method
          // produces the statement it always produced.
          method = argument.Value.Value is int m && m > 0 ? _methodNames[m] : null;
          break;
        case "Expressions":
          foreach (var value in argument.Value.Values) {
            if (value.Value is string expression && !string.IsNullOrWhiteSpace(expression)) {
              expressions.Add(expression);
            }
          }
          break;
      }
    }

    // Properties first and expressions after, which is the order the index is built in and so the
    // order a filter has to lead with.
    var covered = elements.Concat(expressions.Select(e => new CompositeIndexElement(e, e)));

    return new CompositeIndexInfo([.. covered], declaredName, where, unique, method, operatorClass);
  }

  /// <summary>
  /// The declaration's properties resolved to the SQL each is indexed over, or null when any one of
  /// them cannot be.
  /// </summary>
  /// <remarks>
  /// All or nothing. A composite missing one of its columns is a different index, and one that
  /// silently answered fewer filters would be worse than none at all.
  /// </remarks>
  private static List<CompositeIndexElement>? _elementsFor(
      AttributeData declaration, Dictionary<string, IPropertySymbol> properties) {
    var names = declaration.ConstructorArguments.Length > 0
      ? declaration.ConstructorArguments[0].Values
          .Select(static v => v.Value as string)
          .Where(static n => !string.IsNullOrWhiteSpace(n))
          .Select(static n => n!)
          .ToArray()
      : [];

    if (names.Length == 0) {
      return null;
    }

    var elements = new List<CompositeIndexElement>(names.Length);

    foreach (var name in names) {
      if (!properties.TryGetValue(name, out var property) || _elementFor(property) is not { } element) {
        return null;
      }

      elements.Add(new CompositeIndexElement(name, element));
    }

    return elements;
  }

  /// <summary>
  /// The SQL one element of a composite index is built over, or null when the property cannot carry
  /// one.
  /// </summary>
  private static string? _elementFor(IPropertySymbol property) {
    var promoted = property.GetAttributes().FirstOrDefault(
      a => TypeNameUtilities.IsNamed(a.AttributeClass, PHYSICAL_FIELD));

    if (promoted is not null) {
      // A promoted property is indexed as its column. The declared name wins over the convention,
      // the same way the column itself is named.
      var declared = promoted.NamedArguments
          .FirstOrDefault(a => a.Key == "ColumnName").Value.Value as string;

      return declared ?? NamingConventionUtilities.ToSnakeCase(property.Name);
    }

    // In the document, so the element is the extraction a query produces, cast the same way a
    // single-property declaration would cast it. A type with no cast cannot carry an index at all.
    var cast = CastFor(property.Type);

    return cast is null
      ? null
      : JsonIndexSql.Expression("data", property.Name, cast.Value);
  }

  /// <summary>
  /// Whether a combined kind includes substring matching.
  /// </summary>
  /// <param name="kind">The combined kinds, as <see cref="DeclaredKind"/> returns them.</param>
  /// <returns><c>true</c> when substring matching was asked for.</returns>
  /// <remarks>
  /// Asked here so the bit values stay in one place. Two callers already need them and a third copy
  /// of a magic number is how they would come to disagree about what a declaration said.
  /// </remarks>
  public static bool IncludesSubstring(int kind) => (kind & KIND_SUBSTRING) != 0;

  /// <summary>
  /// Whether a combined kind includes the ordered capability.
  /// </summary>
  /// <param name="kind">The combined kinds, as <see cref="DeclaredKind"/> returns them.</param>
  /// <returns><c>true</c> when equality, ranges, ordering or null tests were asked for.</returns>
  public static bool IncludesOrdered(int kind) => (kind & KIND_ORDERED) != 0;

  /// <summary>
  /// Whether any declaration on this property asks for the folded form.
  /// </summary>
  /// <param name="property">The property to inspect.</param>
  /// <returns><c>true</c> when a declaration asked for case folding.</returns>
  /// <remarks>
  /// <para>
  /// Answered separately from the kinds because it is not one. The kinds say what the index has to
  /// answer; this says which expression it is built over, and both kinds can be built over either.
  /// </para>
  /// <para>
  /// Any declaration asking for it is enough, which matters because the attribute repeats: a field
  /// compared both ways carries an unfolded declaration and a folded one, and the folded index has to
  /// be emitted for the second without the first cancelling it.
  /// </para>
  /// </remarks>
  public static bool DeclaresCaseInsensitive(IPropertySymbol? property) =>
    DeclaredKind(property, caseInsensitive: true) is not null;

  /// <summary>Whether one declaration asks for the folded expression.</summary>
  /// <remarks>
  /// Positional, like the kind: the attribute declares both as get-only, so a named argument does
  /// not compile and none can appear here. Absent means the default, which is to respect case.
  /// </remarks>
  private static bool _foldsCase(AttributeData attribute) =>
    attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is true;

  /// <summary>The kind argument of a declaration, defaulting to the ordered capability.</summary>
  /// <remarks>
  /// Positional only, and that is a property of the attributes rather than a limitation here: both
  /// declare <c>Kind</c> as get-only, so <c>[Indexed(Kind = …)]</c> does not compile and no caller can
  /// produce a named argument to read. The parameter is defaulted, so the constructor argument is
  /// always present; the fallback covers source that does not bind, which an analyzer sees mid-edit.
  /// </remarks>
  private static int _kindOf(AttributeData attribute, int fallback) =>
    attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int positional
      ? positional
      : fallback;
}
