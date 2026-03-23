using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Roslyn analyzer that detects abstract/polymorphic type properties in perspective models.
/// Reports WHIZ811 info diagnostic suggesting the use of [PolymorphicDiscriminator] for efficient queries.
/// </summary>
/// <remarks>
/// <para>
/// This analyzer finds classes implementing <c>IPerspectiveFor&lt;TModel, TEvent...&gt;</c>
/// and checks the TModel type for properties that are:
/// </para>
/// <list type="bullet">
/// <item>Abstract classes</item>
/// <item>Types with [JsonPolymorphic] attribute</item>
/// </list>
/// <para>
/// The analyzer recursively checks nested types to catch polymorphic properties in
/// complex object hierarchies.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/polymorphic-types</docs>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerspectiveModelPolymorphicAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [DiagnosticDescriptors.PerspectiveModelPolymorphicProperty];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var typeSymbol = (INamedTypeSymbol)context.Symbol;

    // Skip abstract classes - they can't be instantiated as perspectives
    if (typeSymbol.IsAbstract) {
      return;
    }

    // Find IPerspectiveFor<TModel, ...> interfaces
    foreach (var iface in typeSymbol.AllInterfaces) {
      // Must be IPerspectiveFor with at least 2 type arguments (TModel + at least one TEvent)
      if ((!iface.Name.StartsWith("IPerspectiveFor", StringComparison.Ordinal) &&
           !iface.Name.StartsWith("IPerspectiveWithActionsFor", StringComparison.Ordinal) &&
           !iface.Name.StartsWith("IPerspectiveBase", StringComparison.Ordinal)) || iface.TypeArguments.Length < 2) {
        continue;
      }

      // TModel is the first type argument
      if (iface.TypeArguments[0] is not INamedTypeSymbol modelType) {
        continue;
      }

      // Check model for polymorphic properties (recursive with cycle detection)
      var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
      _checkForPolymorphicTypes(context, modelType, visited);
    }
  }

  private static void _checkForPolymorphicTypes(
      SymbolAnalysisContext context,
      INamedTypeSymbol type,
      HashSet<INamedTypeSymbol> visited) {

    // Cycle detection - prevent infinite loops in self-referencing types
    if (!visited.Add(type)) {
      return;
    }

    // Skip system types (except collections)
    if (_isNonCollectionSystemType(type)) {
      return;
    }

    foreach (var member in type.GetMembers().OfType<IPropertySymbol>()) {
      if (_shouldSkipProperty(member)) {
        continue;
      }

      if (member.Type is not INamedTypeSymbol propType) {
        continue;
      }

      // Get the element type if this is a collection
      var elementType = _getCollectionElementType(propType);
      var typeToCheck = elementType ?? propType;

      // Check if this property type is polymorphic (abstract or has [JsonPolymorphic])
      if (_isPolymorphicType(typeToCheck)) {
        _reportPolymorphicDiagnostic(context, member, type, typeToCheck);
        continue;
      }

      // Recursively check nested class/struct types
      if ((typeToCheck.TypeKind == TypeKind.Class || typeToCheck.TypeKind == TypeKind.Struct) &&
          !_isSystemPrimitiveType(typeToCheck)) {
        _checkForPolymorphicTypes(context, typeToCheck, visited);
      }

      // Check generic type arguments (e.g., List<NestedType> where NestedType has polymorphic property)
      _checkTypeArgumentsForPolymorphic(context, propType, visited);
    }
  }

  /// <summary>
  /// Checks if a type is a System namespace type that is NOT a collections type.
  /// </summary>
  private static bool _isNonCollectionSystemType(INamedTypeSymbol type) {
    return type.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true &&
           !type.ContainingNamespace.ToDisplayString().StartsWith("System.Collections", StringComparison.Ordinal);
  }

  /// <summary>
  /// Checks if a property should be skipped during analysis (static, indexer, write-only, or ignored).
  /// </summary>
  private static bool _shouldSkipProperty(IPropertySymbol member) {
    return member.IsStatic || member.IsIndexer || member.IsWriteOnly || _isPropertyIgnored(member);
  }

  /// <summary>
  /// Reports a WHIZ811 diagnostic for a polymorphic property.
  /// </summary>
  private static void _reportPolymorphicDiagnostic(
      SymbolAnalysisContext context,
      IPropertySymbol member,
      INamedTypeSymbol containingType,
      INamedTypeSymbol polymorphicType) {

    var diagnostic = Diagnostic.Create(
        DiagnosticDescriptors.PerspectiveModelPolymorphicProperty,
        member.Locations.FirstOrDefault() ?? Location.None,
        member.Name,
        containingType.Name,
        polymorphicType.Name);
    context.ReportDiagnostic(diagnostic);
  }

  /// <summary>
  /// Checks generic type arguments for polymorphic types or nested types containing polymorphic properties.
  /// </summary>
  private static void _checkTypeArgumentsForPolymorphic(
      SymbolAnalysisContext context,
      INamedTypeSymbol propType,
      HashSet<INamedTypeSymbol> visited) {

    foreach (var typeArg in propType.TypeArguments.OfType<INamedTypeSymbol>()) {
      if (!_isSystemPrimitiveType(typeArg) && !_isPolymorphicType(typeArg)) {
        _checkForPolymorphicTypes(context, typeArg, visited);
      }
    }
  }

  /// <summary>
  /// Checks if a type is polymorphic (abstract or has [JsonPolymorphic] attribute).
  /// </summary>
  private static bool _isPolymorphicType(INamedTypeSymbol type) {
    // Check if type is abstract
    if (type.IsAbstract && type.TypeKind == TypeKind.Class) {
      return true;
    }

    // Check for [JsonPolymorphic] attribute
    foreach (var attr in type.GetAttributes()) {
      var attrName = attr.AttributeClass?.ToDisplayString();
      if (attrName == "System.Text.Json.Serialization.JsonPolymorphicAttribute") {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Gets the element type if the type is a collection (List, IEnumerable, array, etc.).
  /// Returns null if not a collection.
  /// </summary>
  private static INamedTypeSymbol? _getCollectionElementType(INamedTypeSymbol type) {
    // Check for array
    if (type is IArrayTypeSymbol arrayType) {
      return arrayType.ElementType as INamedTypeSymbol;
    }

    // Check for generic collection types
    if (!type.IsGenericType || type.TypeArguments.Length == 0) {
      return null;
    }

    var originalDef = type.ConstructedFrom.ToDisplayString();

    // Common collection interfaces and types
    if (originalDef.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IList<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.ICollection<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IEnumerable<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IReadOnlyCollection<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Immutable.ImmutableList<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Immutable.ImmutableArray<", StringComparison.Ordinal)) {
      return type.TypeArguments[0] as INamedTypeSymbol;
    }

    return null;
  }

  private static bool _isSystemPrimitiveType(INamedTypeSymbol type) {
    var ns = type.ContainingNamespace?.ToDisplayString();
    if (ns == null) {
      return false;
    }

    // Skip common system types that definitely won't contain polymorphic properties
    if (ns == "System") {
      var name = type.Name;
      return name is "String" or "DateTime" or "DateTimeOffset" or "TimeSpan" or
             "Guid" or "Decimal" or "Uri" or "Version" or "DateOnly" or "TimeOnly";
    }

    return false;
  }

  /// <summary>
  /// Checks if a property is marked as ignored by EF Core or JSON serialization.
  /// </summary>
  private static bool _isPropertyIgnored(IPropertySymbol property) {
    foreach (var attr in property.GetAttributes()) {
      var attrName = attr.AttributeClass?.ToDisplayString();
      if (attrName == null) {
        continue;
      }

      // EF Core [NotMapped]
      if (attrName == "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute") {
        return true;
      }

      // System.Text.Json [JsonIgnore]
      if (attrName == "System.Text.Json.Serialization.JsonIgnoreAttribute") {
        return true;
      }

      // Newtonsoft.Json [JsonIgnore]
      if (attrName == "Newtonsoft.Json.JsonIgnoreAttribute") {
        return true;
      }
    }

    return false;
  }
}
