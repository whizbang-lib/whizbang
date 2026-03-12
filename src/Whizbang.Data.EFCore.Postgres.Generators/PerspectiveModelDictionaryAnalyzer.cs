using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Roslyn analyzer that detects Dictionary properties in perspective models.
/// EF Core 10's ComplexProperty().ToJson() does NOT support Dictionary types.
/// </summary>
/// <remarks>
/// <para>
/// This analyzer finds classes implementing <c>IPerspectiveFor&lt;TModel, TEvent...&gt;</c>
/// and checks the TModel type for Dictionary&lt;K,V&gt; properties. When found, it reports
/// WHIZ810 warning suggesting the use of List&lt;T&gt; with Key/Value properties instead.
/// </para>
/// <para>
/// The analyzer recursively checks nested types to catch Dictionary properties in
/// complex object hierarchies.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerspectiveModelDictionaryAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [DiagnosticDescriptors.PerspectiveModelDictionaryProperty];

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
      if (!iface.Name.StartsWith("IPerspectiveFor", StringComparison.Ordinal) || iface.TypeArguments.Length < 2) {
        continue;
      }

      // TModel is the first type argument
      var modelType = iface.TypeArguments[0] as INamedTypeSymbol;
      if (modelType == null) {
        continue;
      }

      // Check model for Dictionary properties (recursive with cycle detection)
      var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
      _checkForDictionary(context, modelType, visited);
    }
  }

  private static void _checkForDictionary(
      SymbolAnalysisContext context,
      INamedTypeSymbol type,
      HashSet<INamedTypeSymbol> visited) {

    // Cycle detection - prevent infinite loops in self-referencing types
    if (!visited.Add(type)) {
      return;
    }

    // Skip system types and types without source
    if (type.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true &&
        !type.ContainingNamespace.ToDisplayString().StartsWith("System.Collections", StringComparison.Ordinal)) {
      return;
    }

    foreach (var member in type.GetMembers().OfType<IPropertySymbol>()) {
      // Skip static, indexers, and write-only properties
      if (member.IsStatic || member.IsIndexer || member.IsWriteOnly) {
        continue;
      }

      // Skip properties marked as not mapped/ignored by EF Core or JSON serialization
      if (_isPropertyIgnored(member)) {
        continue;
      }

      var propType = member.Type as INamedTypeSymbol;
      if (propType == null) {
        continue;
      }

      // Check if this property is a Dictionary<,> or IDictionary<,>
      if (_isDictionaryType(propType)) {
        var keyType = propType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var valueType = propType.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var suggestedType = $"KeyValuePair<{keyType}, {valueType}>";

        var diagnostic = Diagnostic.Create(
            DiagnosticDescriptors.PerspectiveModelDictionaryProperty,
            member.Locations.FirstOrDefault() ?? Location.None,
            member.Name,
            type.Name,
            keyType,
            valueType,
            suggestedType);
        context.ReportDiagnostic(diagnostic);
        continue;
      }

      // Recursively check nested class/struct types
      // Skip common system types that won't contain Dictionary
      if ((propType.TypeKind == TypeKind.Class || propType.TypeKind == TypeKind.Struct) &&
          !_isSystemPrimitiveType(propType)) {
        _checkForDictionary(context, propType, visited);
      }

      // Check generic type arguments (e.g., List<NestedType> where NestedType has Dictionary)
      foreach (var typeArg in propType.TypeArguments.OfType<INamedTypeSymbol>()) {
        if (_isDictionaryType(typeArg)) {
          // Dictionary is inside a collection (e.g., List<Dictionary<string, string>>)
          var keyType = typeArg.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
          var valueType = typeArg.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
          var suggestedType = $"KeyValuePair<{keyType}, {valueType}>";

          var diagnostic = Diagnostic.Create(
              DiagnosticDescriptors.PerspectiveModelDictionaryProperty,
              member.Locations.FirstOrDefault() ?? Location.None,
              member.Name,
              type.Name,
              keyType,
              valueType,
              suggestedType);
          context.ReportDiagnostic(diagnostic);
        } else if (!_isSystemPrimitiveType(typeArg)) {
          _checkForDictionary(context, typeArg, visited);
        }
      }
    }
  }

  private static bool _isDictionaryType(INamedTypeSymbol type) {
    if (!type.IsGenericType || type.TypeArguments.Length != 2) {
      return false;
    }

    var typeName = type.ConstructedFrom.ToDisplayString();
    return typeName == "System.Collections.Generic.Dictionary<TKey, TValue>" ||
           typeName == "System.Collections.Generic.IDictionary<TKey, TValue>" ||
           typeName == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
  }

  private static bool _isSystemPrimitiveType(INamedTypeSymbol type) {
    var ns = type.ContainingNamespace?.ToDisplayString();
    if (ns == null) {
      return false;
    }

    // Skip common system types that definitely won't contain Dictionary
    if (ns == "System") {
      var name = type.Name;
      return name is "String" or "DateTime" or "DateTimeOffset" or "TimeSpan" or
             "Guid" or "Decimal" or "Uri" or "Version" or "DateOnly" or "TimeOnly";
    }

    return false;
  }

  /// <summary>
  /// Checks if a property is marked as ignored by EF Core or JSON serialization.
  /// Properties with these attributes are not persisted, so Dictionary usage is fine.
  /// </summary>
  /// <remarks>
  /// Note: Fluent API .Ignore() in OnModelCreating cannot be detected at compile time.
  /// This only checks attribute-based exclusions.
  /// </remarks>
  private static bool _isPropertyIgnored(IPropertySymbol property) {
    foreach (var attr in property.GetAttributes()) {
      var attrName = attr.AttributeClass?.ToDisplayString();
      if (attrName == null) {
        continue;
      }

      // EF Core [NotMapped] - property is not mapped to database
      if (attrName == "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute") {
        return true;
      }

      // System.Text.Json [JsonIgnore] - property is not serialized
      if (attrName == "System.Text.Json.Serialization.JsonIgnoreAttribute") {
        return true;
      }

      // Newtonsoft.Json [JsonIgnore] - property is not serialized
      if (attrName == "Newtonsoft.Json.JsonIgnoreAttribute") {
        return true;
      }

      // EF Core [BackingField] with no property mapping could indicate custom handling
      // but typically the property is still mapped, so we don't exclude based on this
    }

    return false;
  }
}
