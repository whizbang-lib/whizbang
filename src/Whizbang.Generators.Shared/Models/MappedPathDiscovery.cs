using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Whether a perspective model holds a member the mapped path cannot materialize.
/// </summary>
/// <remarks>
/// <para>
/// The mapped path stores a document as a complex property, which means Entity Framework walks the
/// whole object graph and has to be able to construct every type in it when reading a row back.
/// Some shapes it cannot, and it says so while the model is being built, which takes the process
/// down at startup rather than affecting one query.
/// </para>
/// <para>
/// The answer here routes such a model to the opaque form instead, where the document is one
/// serialized value the serializer handles and Entity Framework never looks inside. That form has a
/// real cost, so it is reported rather than applied in silence: nothing inside an opaque document is
/// a mapped property, so there is no extraction to index, nothing for the containment rewrite to
/// recognize, and nowhere to hang a value conversion.
/// </para>
/// <para>
/// <strong>The shapes are measured, not reasoned.</strong> Each entry below was run against a real
/// model build; the list is exactly what failed and nothing more, because the two ways of being
/// wrong are both quiet. Too narrow and a service cannot start. Too broad and a model loses its
/// indexes with nobody asking for it.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectionMemberShapeProbeTests.cs</tests>
public static class MappedPathDiscovery {
  /// <summary>
  /// The collection types that cannot hold a complex element on the mapped path.
  /// </summary>
  /// <remarks>
  /// Entity Framework needs to create and fill the collection while materializing a row. It manages
  /// that for <c>List</c>, <c>IList</c> and <c>ImmutableList</c>, and refuses these, reporting the
  /// member as a navigation that complex types do not support. A collection of a primitive element
  /// is a different thing entirely and is accepted in any of these shapes, which is why the element
  /// type is checked as well.
  /// </remarks>
  private static readonly string[] _unfillableCollections = [
    "System.Collections.Generic.IEnumerable<",
    "System.Collections.Generic.ICollection<",
    "System.Collections.Generic.IReadOnlyList<",
    "System.Collections.Generic.IReadOnlyCollection<",
  ];

  /// <summary>
  /// Whether the document has to be stored as one opaque value rather than as mapped properties.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns><c>true</c> when the mapped path cannot round-trip this model.</returns>
  /// <remarks>
  /// <para>
  /// The one question every caller actually has. Two unrelated conditions force the same answer: a
  /// polymorphic member, which the mapped path would round-trip as its declared type and lose the
  /// derived one, and a member the mapped path cannot construct at all. Asking them separately is
  /// how a generator and an analyzer come to disagree about how a model is stored.
  /// </para>
  /// <para>
  /// The reason still matters for what to tell the author, which is why it stays available
  /// separately rather than being folded into this answer.
  /// </para>
  /// </remarks>
  public static bool MustStoreOpaquely(INamedTypeSymbol? modelType) =>
    PolymorphicModelDiscovery.IsPolymorphic(modelType) || UnmappableMember(modelType) is not null;

  /// <summary>
  /// The first member the mapped path cannot materialize, named for a diagnostic, or null.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>A member path such as <c>Turns.AttachedFiles</c>, or null when the model maps.</returns>
  public static string? UnmappableMember(INamedTypeSymbol? modelType) {
    if (modelType is null) {
      return null;
    }

    return _findUnmappable(modelType, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default), string.Empty);
  }

  private static string? _findUnmappable(
      INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited, string path) {
    if (!visited.Add(type)) {
      return null;
    }

    foreach (var property in type.GetMembers().OfType<IPropertySymbol>()
        .Where(PolymorphicModelDiscovery.IsAnalyzableProperty)) {
      var found = _judgeProperty(property, visited, path);
      if (found is not null) {
        return found;
      }
    }

    return null;
  }

  /// <summary>The unmappable member at or below this property, or null.</summary>
  private static string? _judgeProperty(
      IPropertySymbol property, HashSet<INamedTypeSymbol> visited, string path) {
    if (property.Type is not INamedTypeSymbol propertyType) {
      return null;
    }

    var here = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
    var element = _collectionElement(propertyType);

    // A collection the mapped path cannot fill, holding something other than a primitive. A
    // collection of primitives is stored as a value whatever interface declares it.
    if (element is not null && !_isPrimitive(element) && _cannotBeFilled(propertyType)) {
      return here;
    }

    // Walk through a collection to its element: the element is what the mapped path has to
    // construct, and stepping into the collection type itself would inspect List rather than the
    // model's own type.
    var nested = element ?? propertyType;
    if (_isPrimitive(nested)) {
      return null;
    }

    // A nested type the mapped path cannot construct.
    return _hasNoBindableConstructor(nested)
      ? here
      : _findUnmappable(nested, visited, here);
  }

  /// <summary>The element type when the property is a collection, fillable or not.</summary>
  private static INamedTypeSymbol? _collectionElement(INamedTypeSymbol type) =>
    type.IsGenericType
    && type.TypeArguments.Length > 0
    && _isCollectionDefinition(TypeNameUtilities.Display(type.ConstructedFrom))
      ? type.TypeArguments[0] as INamedTypeSymbol
      : null;

  /// <summary>Whether the mapped path cannot create and fill this collection type.</summary>
  private static bool _cannotBeFilled(INamedTypeSymbol type) {
    var definition = TypeNameUtilities.Display(type.ConstructedFrom);
    return _unfillableCollections.Any(c => definition.StartsWith(c, System.StringComparison.Ordinal));
  }

  /// <summary>
  /// Whether every constructor takes something that cannot come from a mapped property.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Constructor parameters are bound to mapped properties by name, and only a scalar qualifies: a
  /// collection cannot be bound to one at all. A positional record whose constructor takes a
  /// collection therefore offers nothing usable, and having no other constructor, cannot be
  /// constructed. Adding a parameterless one is what makes such a type mappable again.
  /// </para>
  /// <para>
  /// The copy constructor a record also declares is skipped: it takes the record itself, which is
  /// never a mapped property, so counting it would make every record look constructible.
  /// </para>
  /// </remarks>
  private static bool _hasNoBindableConstructor(INamedTypeSymbol type) {
    if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract) {
      return false;
    }

    var candidates = type.InstanceConstructors
        .Where(c => c.DeclaredAccessibility == Accessibility.Public)
        .Where(c => !_isCopyConstructor(c, type))
        .ToList();

    // No public constructor at all is Entity Framework's own business; it has ways in.
    return candidates.Count != 0
      && candidates.TrueForAll(c => c.Parameters.Any(p => _cannotComeFromAMappedProperty(p.Type)));
  }

  private static bool _isCopyConstructor(IMethodSymbol constructor, INamedTypeSymbol type) =>
    constructor.Parameters.Length == 1
    && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type);

  /// <summary>Whether a constructor parameter of this type could never be bound.</summary>
  /// <remarks>
  /// Only collections are judged here. A nested complex parameter is bindable, because the mapped
  /// path maps the nested type too; a collection is the shape that has no binding at all.
  /// </remarks>
  private static bool _cannotComeFromAMappedProperty(ITypeSymbol parameterType) =>
    parameterType is INamedTypeSymbol { IsGenericType: true } named
    && _isCollectionDefinition(TypeNameUtilities.Display(named.ConstructedFrom));

  private static bool _isCollectionDefinition(string definition) =>
    definition.StartsWith("System.Collections.Generic.", System.StringComparison.Ordinal)
    || definition.StartsWith("System.Collections.Immutable.", System.StringComparison.Ordinal);

  /// <summary>Whether the type is one the mapped path stores as a value rather than walking into.</summary>
  private static bool _isPrimitive(INamedTypeSymbol type) {
    if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum) {
      return true;
    }

    if (type is { IsGenericType: true } nullable
        && nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T) {
      return true;
    }

    // Empty rather than null for a type with no containing namespace: it answers both checks
    // correctly and leaves no null for one analyzer to want guarded and another to want conditional.
    var containing = type.ContainingNamespace is { } ns ? TypeNameUtilities.Display(ns) : string.Empty;

    return containing.StartsWith("System", System.StringComparison.Ordinal)
      && !containing.StartsWith("System.Collections", System.StringComparison.Ordinal);
  }
}
