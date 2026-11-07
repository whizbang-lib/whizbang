// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// The collection types that cannot hold a complex element on the mapped path. An array of a complex element
  /// cannot either, and is judged on its own because an array is not a named type.
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

    return _findUnmappable(modelType, new Walk(), string.Empty);
  }

  /// <summary>
  /// Every property in the model's graph whose type (or collection element) is a type the path to it is already inside:
  /// the property that makes the graph recursive, with its path and the type it returns to.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>The recursive properties, such as <c>Skeleton.Children</c> returning to <c>Node</c>, in walk order.</returns>
  /// <remarks>
  /// <para>
  /// Entity Framework maps a recursive complex type one level deep: the first level of the self-reference is mapped and
  /// everything below it is never written and reads back as null, with no error at model build, save or query (#2112).
  /// Each property listed here alone makes the model stored opaquely, where the serializer writes the whole tree.
  /// </para>
  /// <para>
  /// A type reached twice along different paths is not listed: only a return to a type on the current path is a cycle.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryRecursionTests.cs:RecursiveMembers_ListsEachPropertyThatClosesACycleAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryRecursionTests.cs:ATypeReachedTwice_IsNotRecursionAsync</tests>
  public static IReadOnlyList<(IPropertySymbol Property, string Path, INamedTypeSymbol Recursive)> RecursiveMembers(
      INamedTypeSymbol modelType) {
    var found = new List<(IPropertySymbol Property, string Path, INamedTypeSymbol Recursive)>();
    _collectRecursive(modelType, new Walk(), string.Empty, found);
    return found;
  }

  /// <summary>Whether the type, or anything it holds, reaches a type it is already inside.</summary>
  /// <param name="type">The type to judge, such as the element of an array.</param>
  /// <returns><c>true</c> when the graph below the type is recursive, so it is stored opaquely in any collection shape.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryRecursionTests.cs:IsRecursive_AnswersForTheTypeAndWhatItHoldsAsync</tests>
  public static bool IsRecursive(INamedTypeSymbol type) => RecursiveMembers(type).Count != 0;

  /// <summary>
  /// The state of one depth-first walk: every type already judged, and the types on the path to the current one.
  /// </summary>
  /// <remarks>
  /// A type already judged is not walked again, which keeps the walk finite and linear; a type on the current path is
  /// a cycle. Telling the two apart is the whole of #2112: one set answering both questions stopped the walk at a
  /// recursive type and judged it mappable.
  /// </remarks>
  private sealed class Walk {
    public HashSet<INamedTypeSymbol> Visited { get; } = new(SymbolEqualityComparer.Default);
    public HashSet<INamedTypeSymbol> OnPath { get; } = new(SymbolEqualityComparer.Default);
  }

  private static void _collectRecursive(
      INamedTypeSymbol type, Walk walk, string path,
      List<(IPropertySymbol Property, string Path, INamedTypeSymbol Recursive)> found) {
    if (!walk.Visited.Add(type)) {
      return;
    }
    walk.OnPath.Add(type);

    foreach (var property in type.GetMembers().OfType<IPropertySymbol>()) {
      if (!PolymorphicModelDiscovery.IsAnalyzableProperty(property)) {
        continue;
      }
      var nested = _walkedTypeOf(property);
      if (nested is null) {
        continue;
      }
      var here = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
      if (walk.OnPath.Contains(nested)) {
        found.Add((property, here, nested));
      } else {
        _collectRecursive(nested, walk, here, found);
      }
    }

    walk.OnPath.Remove(type);
  }

  /// <summary>
  /// The type a walk continues into from this property: an array's or a collection's element, or the property's own
  /// type, unless it is a primitive or not a named type.
  /// </summary>
  private static INamedTypeSymbol? _walkedTypeOf(IPropertySymbol property) {
    var walked = property.Type switch {
      IArrayTypeSymbol array => array.ElementType as INamedTypeSymbol,
      INamedTypeSymbol named => _collectionElement(named) ?? named,
      _ => null,
    };
    return walked is null || _isPrimitive(walked) ? null : walked;
  }

  /// <summary>
  /// Every array of records in the model's graph: each array property whose element is not a primitive, at any depth,
  /// with its member path. Each one alone makes the model stored opaquely (<see cref="UnmappableMember"/> names the
  /// first); WHIZ200 reports them all, each at its own property.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>The array properties and their paths, such as <c>Groups.Tags</c>, in declaration order.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnArrayOfRecordsInsideACollectionElement_IsReportedByItsPathAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnArrayOfRecordsUnderANestedObject_IsReportedByItsPathAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnArrayOfPrimitives_IsNotReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs:AnArrayOfRecordsTheDocumentLeavesOut_IsNotReportedAsync</tests>
  public static IReadOnlyList<(IPropertySymbol Property, string Path)> ArraysOfRecords(INamedTypeSymbol modelType) {
    var found = new List<(IPropertySymbol Property, string Path)>();
    _collectArraysOfRecords(modelType, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default), string.Empty, found);
    return found;
  }

  private static void _collectArraysOfRecords(
      INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited, string path, List<(IPropertySymbol Property, string Path)> found) {
    if (!visited.Add(type)) {
      return;
    }

    // A guard rather than Where(IsAnalyzableProperty): the method group would share one cached delegate, and its
    // first-call branch, with _findUnmappable, so which walk ran first in a process decided what coverage this line
    // showed. Generators prefer the allocation-free loop anyway (see .editorconfig, S3267).
    foreach (var property in type.GetMembers().OfType<IPropertySymbol>()) {
      if (!PolymorphicModelDiscovery.IsAnalyzableProperty(property)) {
        continue;
      }
      var here = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
      var nested = _walkedType(property, here, found);
      if (nested is not null) {
        _collectArraysOfRecords(nested, visited, here, found);
      }
    }
  }

  /// <summary>
  /// Records the property when it is an array of records, and returns the type the walk continues into: an array's or a
  /// collection's element, or the property's own type, unless it is a primitive.
  /// </summary>
  private static INamedTypeSymbol? _walkedType(
      IPropertySymbol property, string here, List<(IPropertySymbol Property, string Path)> found) {
    if (property.Type is IArrayTypeSymbol array) {
      var element = array.ElementType as INamedTypeSymbol;
      if (element is not null && _isPrimitive(element)) {
        return null;
      }
      found.Add((property, here));
      return element;
    }

    if (property.Type is not INamedTypeSymbol propertyType) {
      return null;
    }
    var walked = _collectionElement(propertyType) ?? propertyType;
    return _isPrimitive(walked) ? null : walked;
  }

  private static string? _findUnmappable(INamedTypeSymbol type, Walk walk, string path) {
    if (!walk.Visited.Add(type)) {
      return null;
    }
    walk.OnPath.Add(type);

    string? found = null;
    foreach (var property in type.GetMembers().OfType<IPropertySymbol>()
        .Where(PolymorphicModelDiscovery.IsAnalyzableProperty)) {
      found = _judgeProperty(property, walk, path);
      if (found is not null) {
        break;
      }
    }

    walk.OnPath.Remove(type);
    return found;
  }

  /// <summary>The unmappable member at or below this property, or null.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryTests.cs:AnArrayOfAComplexElementIsReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryTests.cs:AnArrayInsideACollectionElementIsNamedByItsPathAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryTests.cs:AnArrayOfPrimitivesIsNotReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryTests.cs:AnArrayOfArraysIsReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryTests.cs:AMemberThatIsNotANamedType_IsNotReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryRecursionTests.cs:ACollectionOfItself_IsNamedByItsPathAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MappedPathDiscoveryRecursionTests.cs:MutualRecursion_IsNamedWhereThePathReturnsAsync</tests>
  private static string? _judgeProperty(IPropertySymbol property, Walk walk, string path) {
    var here = path.Length == 0 ? property.Name : $"{path}.{property.Name}";

    // An array of a complex element: Entity Framework builds a model with it, then refuses it at the first read
    // (collection navigations cannot be arrays) and on the change tracker's snapshot (the array is of a fixed
    // size), so it is reported here rather than left to fail at runtime (#1910). An array of primitives is a
    // value the mapped path reads back.
    if (property.Type is IArrayTypeSymbol array) {
      return array.ElementType is INamedTypeSymbol arrayElement && _isPrimitive(arrayElement) ? null : here;
    }

    if (property.Type is not INamedTypeSymbol propertyType) {
      return null;
    }
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

    // A type the path is already inside: the mapped path maps a recursive type one level deep and drops every level
    // below it on write, with no error anywhere (#2112), so the whole tree has to be one opaque value. A nested type
    // the mapped path cannot construct is named the same way.
    return walk.OnPath.Contains(nested) || _hasNoBindableConstructor(nested)
      ? here
      : _findUnmappable(nested, walk, here);
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
    var containing = TypeNameUtilities.DisplayOrEmpty(type.ContainingNamespace);

    return containing.StartsWith("System", System.StringComparison.Ordinal)
      && !containing.StartsWith("System.Collections", System.StringComparison.Ordinal);
  }
}
