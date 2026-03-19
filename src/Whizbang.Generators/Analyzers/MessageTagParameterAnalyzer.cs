using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer that enforces constructor parameters in MessageTagAttribute subclasses
/// match property names (case-insensitive).
/// </summary>
/// <remarks>
/// <para>
/// Whizbang's source generators extract attribute values using constructor parameter names.
/// If the parameter name doesn't match a property name (case-insensitive), the value won't
/// be extracted correctly, causing subtle bugs like Tag = "" instead of the expected value.
/// </para>
/// <para>
/// This analyzer catches the issue at compile-time with a clear error message suggesting
/// the correct parameter name to use.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz090</docs>
/// <tests>Whizbang.Generators.Tests/Analyzers/MessageTagParameterAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class MessageTagParameterAnalyzer : DiagnosticAnalyzer {
  private const string MESSAGE_TAG_ATTRIBUTE_NAME = "Whizbang.Core.Attributes.MessageTagAttribute";

  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      ImmutableArray.Create(DiagnosticDescriptors.MessageTagParameterMismatch);

  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeNamedType, SymbolKind.NamedType);
  }

  private static void _analyzeNamedType(SymbolAnalysisContext context) {
    var typeSymbol = (INamedTypeSymbol)context.Symbol;

    // Only analyze classes (attributes are classes)
    if (typeSymbol.TypeKind != TypeKind.Class) {
      return;
    }

    // Check if this type inherits from MessageTagAttribute
    if (!_inheritsFromMessageTagAttribute(typeSymbol)) {
      return;
    }

    // Skip the MessageTagAttribute base class itself
    if (typeSymbol.ToDisplayString() == MESSAGE_TAG_ATTRIBUTE_NAME) {
      return;
    }

    // Get all properties from this type and its base types
    var allProperties = _getAllProperties(typeSymbol);

    // Check each constructor
    foreach (var constructor in typeSymbol.Constructors) {
      // Skip implicit constructors
      if (constructor.IsImplicitlyDeclared) {
        continue;
      }

      foreach (var parameter in constructor.Parameters) {
        // Check if parameter name matches any property (case-insensitive)
        var matchingProperty = _findMatchingProperty(allProperties, parameter.Name);

        if (matchingProperty == null) {
          // Find the best suggestion based on type compatibility
          var suggestion = _findSuggestion(allProperties, parameter);

          var location = parameter.Locations.FirstOrDefault() ?? Location.None;

          context.ReportDiagnostic(Diagnostic.Create(
              DiagnosticDescriptors.MessageTagParameterMismatch,
              location,
              parameter.Name,                                    // {0} - parameter name
              typeSymbol.Name,                                   // {1} - class name
              suggestion?.Name.ToLowerInvariant() ?? "?",        // {2} - suggested parameter name
              suggestion?.Name ?? "?"                            // {3} - property name
          ));
        }
      }
    }
  }

  /// <summary>
  /// Checks if the type inherits from MessageTagAttribute.
  /// </summary>
  private static bool _inheritsFromMessageTagAttribute(INamedTypeSymbol typeSymbol) {
    var current = typeSymbol.BaseType;

    while (current != null) {
      if (current.ToDisplayString() == MESSAGE_TAG_ATTRIBUTE_NAME) {
        return true;
      }
      current = current.BaseType;
    }

    return false;
  }

  /// <summary>
  /// Gets all properties from the type and its base types.
  /// </summary>
  private static ImmutableArray<IPropertySymbol> _getAllProperties(INamedTypeSymbol typeSymbol) {
    var builder = ImmutableArray.CreateBuilder<IPropertySymbol>();
    var current = typeSymbol;

    while (current != null) {
      foreach (var member in current.GetMembers()) {
        if (member is IPropertySymbol property && property.DeclaredAccessibility == Accessibility.Public) {
          builder.Add(property);
        }
      }
      current = current.BaseType;
    }

    return builder.ToImmutable();
  }

  /// <summary>
  /// Finds a property that matches the parameter name (case-insensitive).
  /// </summary>
  private static IPropertySymbol? _findMatchingProperty(ImmutableArray<IPropertySymbol> properties, string parameterName) {
    return properties.FirstOrDefault(p =>
        string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>
  /// Finds a suggested property based on type compatibility.
  /// Prefers properties with matching types, falls back to the first settable property.
  /// </summary>
  private static IPropertySymbol? _findSuggestion(
      ImmutableArray<IPropertySymbol> properties,
      IParameterSymbol parameter) {

    // Try to find a property with matching type that is settable
    var typeMatch = properties.FirstOrDefault(p =>
        SymbolEqualityComparer.Default.Equals(p.Type, parameter.Type) &&
        (p.SetMethod != null || p.IsRequired));

    if (typeMatch != null) {
      return typeMatch;
    }

    // Fall back to any settable property
    return properties.FirstOrDefault(p => p.SetMethod != null || p.IsRequired);
  }
}
