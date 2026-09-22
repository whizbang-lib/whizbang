using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Whether a perspective model's document has to be stored as one opaque value rather than as a
/// mapped complex property.
/// </summary>
/// <remarks>
/// <para>
/// A model holding an abstract member, or one marked for polymorphic serialization, cannot be
/// round-tripped by the mapped path: that path stores each property under a key of its own and
/// reconstructs the declared type, which loses the derived type it was actually given. Such a model
/// is stored as a single serialized value instead, where the serializer writes a discriminator.
/// </para>
/// <para>
/// The choice reaches much further than which statement is emitted. In the opaque form nothing
/// inside the document is a mapped property, so there is no extraction to build an index over, no
/// comparison for the containment rewrite to recognize, and nowhere to attach a value conversion. A
/// model classified this way sits outside all of it, which is why the question is answered in one
/// place and asked by both the generator that emits the configuration and the analyzer that warns
/// when an index declared on such a model cannot be served.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorCoverageTests.cs</tests>
public static class PolymorphicModelDiscovery {
  private const string JSON_POLYMORPHIC_ATTRIBUTE = "System.Text.Json.Serialization.JsonPolymorphicAttribute";

  /// <summary>
  /// Whether a model has to take the opaque path.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns><c>true</c> when the model holds something the mapped path cannot round-trip.</returns>
  public static bool IsPolymorphic(INamedTypeSymbol? modelType) {
    if (modelType is null) {
      return false;
    }

    var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    return _checkForPolymorphicTypes(modelType, visited);
  }

  /// <summary>
  /// Whether a property is one the question can be asked about.
  /// </summary>
  /// <param name="property">The property.</param>
  /// <returns><c>true</c> when the property is mapped and can therefore carry polymorphism.</returns>
  /// <remarks>
  /// <para>
  /// Public is the load-bearing condition, and it is the rule both serializers already follow:
  /// neither the mapped path nor the serialized one writes a non-public property, so a non-public
  /// property cannot be what forces a model out of the mapped path.
  /// </para>
  /// <para>
  /// Without it, a model declared as a <c>record</c> answered yes on its first member every time.
  /// The compiler generates a protected <c>EqualityContract</c> of type <c>System.Type</c>, and
  /// <c>System.Type</c> is an abstract class, so <c>record</c> against <c>class</c> silently decided
  /// how every document was stored and quietly excluded records from indexing, containment and value
  /// conversion alike.
  /// </para>
  /// </remarks>
  public static bool IsAnalyzableProperty(IPropertySymbol property) =>
    property is {
      IsStatic: false,
      IsIndexer: false,
      IsWriteOnly: false,
      DeclaredAccessibility: Accessibility.Public,
    } && !_isPropertyIgnored(property);

  /// <summary>
  /// Recursively checks whether a type or a type it holds contains polymorphic properties.
  /// </summary>
  private static bool _checkForPolymorphicTypes(INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited) {
    if (!visited.Add(type)) {
      return false;
    }

    if (_isNonCollectionSystemType(type)) {
      return false;
    }

    return type.GetMembers().OfType<IPropertySymbol>()
        .Where(IsAnalyzableProperty)
        .Select(p => p.Type)
        .OfType<INamedTypeSymbol>()
        .Any(propType => _isPropertyTypePolymorphic(propType, visited));
  }

  /// <summary>
  /// Checks whether a type is a System namespace type that is NOT a collections type.
  /// </summary>
  private static bool _isNonCollectionSystemType(INamedTypeSymbol type) {
    var ns = type.ContainingNamespace is { } containingNamespace
      ? TypeNameUtilities.Display(containingNamespace)
      : null;
    return ns?.StartsWith("System", System.StringComparison.Ordinal) == true &&
           !ns.StartsWith("System.Collections", System.StringComparison.Ordinal);
  }

  /// <summary>
  /// Checks whether a property type, or its element or argument types, contains polymorphic types.
  /// </summary>
  private static bool _isPropertyTypePolymorphic(INamedTypeSymbol propType, HashSet<INamedTypeSymbol> visited) {
    var elementType = _getCollectionElementType(propType);
    var typeToCheck = elementType ?? propType;

    if (IsPolymorphicType(typeToCheck)) {
      return true;
    }

    if (_isRecursivelyPolymorphic(typeToCheck, visited)) {
      return true;
    }

    return _hasPolymorphicTypeArguments(propType, visited);
  }

  /// <summary>
  /// Checks whether a class or struct type recursively contains polymorphic properties.
  /// </summary>
  private static bool _isRecursivelyPolymorphic(INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited) =>
    (type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct) &&
    !_isSystemPrimitiveType(type) &&
    _checkForPolymorphicTypes(type, visited);

  /// <summary>
  /// Checks whether any generic type argument is polymorphic or contains polymorphic properties.
  /// </summary>
  private static bool _hasPolymorphicTypeArguments(INamedTypeSymbol propType, HashSet<INamedTypeSymbol> visited) {
    // S3267: the loop mutates the visited set through _checkForPolymorphicTypes, so LINQ is not
    // appropriate here.
#pragma warning disable S3267
    foreach (var typeArg in propType.TypeArguments.OfType<INamedTypeSymbol>()) {
      if (IsPolymorphicType(typeArg)) {
        return true;
      }
      if (!_isSystemPrimitiveType(typeArg) && _checkForPolymorphicTypes(typeArg, visited)) {
        return true;
      }
    }
#pragma warning restore S3267

    return false;
  }

  /// <summary>
  /// Whether a type is itself polymorphic: an abstract class, or one marked for polymorphic
  /// serialization.
  /// </summary>
  /// <param name="type">The type to inspect.</param>
  /// <returns><c>true</c> when the declared type can be given a value of some other type.</returns>
  public static bool IsPolymorphicType(INamedTypeSymbol type) {
    if (type is null) {
      return false;
    }

    if (type.IsAbstract && type.TypeKind == TypeKind.Class) {
      return true;
    }

    return type.GetAttributes()
        .Any(attr => TypeNameUtilities.IsNamed(attr.AttributeClass, JSON_POLYMORPHIC_ATTRIBUTE));
  }

  /// <summary>
  /// Checks whether a property is marked as ignored by the mapped path or by serialization.
  /// </summary>
  private static bool _isPropertyIgnored(IPropertySymbol property) =>
    property.GetAttributes().Any(attr =>
        TypeNameUtilities.IsNamed(attr.AttributeClass, "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute") ||
        TypeNameUtilities.IsNamed(attr.AttributeClass, "System.Text.Json.Serialization.JsonIgnoreAttribute") ||
        TypeNameUtilities.IsNamed(attr.AttributeClass, "Newtonsoft.Json.JsonIgnoreAttribute"));

  /// <summary>
  /// Gets the element type when the type is a collection.
  /// </summary>
  private static INamedTypeSymbol? _getCollectionElementType(INamedTypeSymbol type) {
    if (!type.IsGenericType || type.TypeArguments.Length == 0) {
      return null;
    }

    var originalDef = TypeNameUtilities.Display(type.ConstructedFrom);

    if (originalDef.StartsWith("System.Collections.Generic.List<", System.StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IList<", System.StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.ICollection<", System.StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IEnumerable<", System.StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IReadOnlyList<", System.StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IReadOnlyCollection<", System.StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Immutable.ImmutableList<", System.StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Immutable.ImmutableArray<", System.StringComparison.Ordinal)) {
      return type.TypeArguments[0] as INamedTypeSymbol;
    }

    return null;
  }

  /// <summary>
  /// Checks whether a type is a system primitive that cannot contain polymorphic properties.
  /// </summary>
  private static bool _isSystemPrimitiveType(INamedTypeSymbol type) {
    if (TypeNameUtilities.IsNamed(type.ContainingNamespace, "System")) {
      var name = type.Name;
      return name is "String" or "DateTime" or "DateTimeOffset" or "TimeSpan" or
             "Guid" or "Decimal" or "Uri" or "Version" or "DateOnly" or "TimeOnly";
    }
    return false;
  }
}
