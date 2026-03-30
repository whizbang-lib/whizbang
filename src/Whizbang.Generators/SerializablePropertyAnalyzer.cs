using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer that detects non-serializable properties on ICommand/IEvent types.
/// Ensures AOT compatibility by flagging object, dynamic, and non-generic interface properties.
/// Recursively checks nested child types to ensure entire object graph is serializable.
/// </summary>
/// <docs>operations/diagnostics/serializable-property-analyzer</docs>
/// <tests>tests/Whizbang.Generators.Tests/SerializablePropertyAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class SerializablePropertyAnalyzer : DiagnosticAnalyzer {
  private const string I_COMMAND = "Whizbang.Core.ICommand";
  private const string I_EVENT = "Whizbang.Core.IEvent";
  private const string WHIZBANG_SERIALIZABLE = "Whizbang.WhizbangSerializableAttribute";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
  [
    DiagnosticDescriptors.NonSerializablePropertyObject,
    DiagnosticDescriptors.NonSerializablePropertyDynamic,
    DiagnosticDescriptors.NonSerializablePropertyInterface,
    DiagnosticDescriptors.NonSerializableNestedProperty
,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var typeSymbol = (INamedTypeSymbol)context.Symbol;

    // Only check public types (non-public can't be serialized anyway)
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return;
    }

    // Only check message types (ICommand, IEvent, [WhizbangSerializable])
    if (!_isMessageType(typeSymbol)) {
      return;
    }

    // Track visited types to prevent infinite loops
    var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

    // Check properties recursively
    _checkPropertiesRecursively(context, typeSymbol, typeSymbol, null, visited);
  }

  private static void _checkPropertiesRecursively(
      SymbolAnalysisContext context,
      INamedTypeSymbol rootType,
      INamedTypeSymbol currentType,
      IPropertySymbol? parentProperty,
      HashSet<INamedTypeSymbol> visited) {
    if (!visited.Add(currentType)) {
      return; // Already checked - prevent infinite loops
    }

    // Walk inheritance chain to check properties from base classes
    foreach (var property in currentType.GetAllProperties()) {

      var propType = property.Type;

      // Check for non-serializable types
      if (_isObjectType(propType)) {
        _reportDiagnostic(context, property, rootType, currentType, parentProperty, "object",
            DiagnosticDescriptors.NonSerializablePropertyObject);
      } else if (_isDynamicType(propType)) {
        _reportDiagnostic(context, property, rootType, currentType, parentProperty, "dynamic",
            DiagnosticDescriptors.NonSerializablePropertyDynamic);
      } else if (_isNonSerializableInterface(propType)) {
        _reportDiagnostic(context, property, rootType, currentType, parentProperty, propType.ToDisplayString(),
            DiagnosticDescriptors.NonSerializablePropertyInterface);
      }

      // Recurse into nested types (unwrap collections/arrays)
      var elementType = _getElementType(propType);
      if (elementType is INamedTypeSymbol namedElementType &&
          !_isPrimitiveOrFrameworkType(namedElementType)) {
        _checkPropertiesRecursively(context, rootType, namedElementType, property, visited);
      }
    }
  }

  private static void _reportDiagnostic(
      SymbolAnalysisContext context,
      IPropertySymbol property,
      INamedTypeSymbol rootType,
      INamedTypeSymbol currentType,
      IPropertySymbol? parentProperty,
      string typeName,
      DiagnosticDescriptor descriptor) {
    // Determine if this is a nested type issue
    var isNested = !SymbolEqualityComparer.Default.Equals(currentType, rootType);

    if (isNested) {
      // WHIZ063: Nested type violation
      var location = parentProperty?.Locations.FirstOrDefault() ?? Location.None;
      var diagnostic = Diagnostic.Create(
          DiagnosticDescriptors.NonSerializableNestedProperty,
          location,
          currentType.Name,           // Nested type name
          rootType.Name,              // Root type name
          parentProperty?.Name ?? "", // Property on root that references nested type
          property.Name,              // Property on nested type with issue
          typeName                    // The problematic type
      );
      context.ReportDiagnostic(diagnostic);
    } else {
      // Direct property issue (WHIZ060, WHIZ061, WHIZ062)
      var location = property.Locations.FirstOrDefault() ?? Location.None;

      if (descriptor.Id == "WHIZ062") {
        // Interface type - include the interface name
        var diagnostic = Diagnostic.Create(
            descriptor,
            location,
            property.Name,
            currentType.Name,
            typeName
        );
        context.ReportDiagnostic(diagnostic);
      } else {
        // object or dynamic
        var diagnostic = Diagnostic.Create(
            descriptor,
            location,
            property.Name,
            currentType.Name
        );
        context.ReportDiagnostic(diagnostic);
      }
    }
  }

  private static bool _isMessageType(INamedTypeSymbol typeSymbol) {
    // Check for ICommand interface
    foreach (var iface in typeSymbol.AllInterfaces) {
      var interfaceName = iface.ToDisplayString();
      if (interfaceName == I_COMMAND || interfaceName == I_EVENT) {
        return true;
      }
    }

    // Check for [WhizbangSerializable] attribute
    foreach (var attr in typeSymbol.GetAttributes()) {
      if (attr.AttributeClass?.ToDisplayString() == WHIZBANG_SERIALIZABLE) {
        return true;
      }
    }

    return false;
  }

  private static bool _isObjectType(ITypeSymbol typeSymbol) {
    // Check for System.Object (object)
    if (typeSymbol.SpecialType == SpecialType.System_Object) {
      return true;
    }

    // Also check for Nullable<object> (object?)
    if (typeSymbol is INamedTypeSymbol namedType &&
        namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
        namedType.TypeArguments.Length > 0 &&
        namedType.TypeArguments[0].SpecialType == SpecialType.System_Object) {
      return true;
    }

    return false;
  }

  private static bool _isDynamicType(ITypeSymbol typeSymbol) {
    // dynamic is represented as object with DynamicAttribute
    return typeSymbol.TypeKind == TypeKind.Dynamic;
  }

  private static bool _isNonSerializableInterface(ITypeSymbol typeSymbol) {
    // Only flag interface types
    if (typeSymbol.TypeKind != TypeKind.Interface) {
      return false;
    }

    // If it's a generic interface with type arguments, it's likely serializable
    // (e.g., IEnumerable<T>, IList<T>, IDictionary<K,V>)
    if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0) {
      return false;
    }

    // Non-generic interface (e.g., IEnumerable, IList) - not serializable
    return true;
  }

  private static ITypeSymbol? _getElementType(ITypeSymbol typeSymbol) {
    // Handle arrays
    if (typeSymbol is IArrayTypeSymbol arrayType) {
      return arrayType.ElementType;
    }

    // Handle Nullable<T>
    if (typeSymbol is INamedTypeSymbol namedType) {
      if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
          namedType.TypeArguments.Length > 0) {
        return namedType.TypeArguments[0];
      }

      // Handle generic collections (List<T>, IEnumerable<T>, Dictionary<K,V>, etc.)
      if (namedType.TypeArguments.Length > 0) {
        // For dictionaries or multi-type-argument generics, we'd need to check all type args
        // For simplicity, return the first type argument (usually the element type)
        // Dictionary<K,V> -> check V (the value type)
        var lastTypeArg = namedType.TypeArguments[namedType.TypeArguments.Length - 1];
        return lastTypeArg;
      }
    }

    // Not a collection - return the type itself for recursion
    return typeSymbol;
  }

  private static bool _isPrimitiveOrFrameworkType(INamedTypeSymbol typeSymbol) {
    // Value types (primitives, enums, structs) are generally serializable
    if (typeSymbol.IsValueType) {
      return true;
    }

    // String is serializable
    if (typeSymbol.SpecialType == SpecialType.System_String) {
      return true;
    }

    // Check if it's a System.* or Microsoft.* type
    var containingNamespace = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
    if (containingNamespace.StartsWith("System", StringComparison.Ordinal) ||
        containingNamespace.StartsWith("Microsoft", StringComparison.Ordinal)) {
      return true;
    }

    return false;
  }
}
