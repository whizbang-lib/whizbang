using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer that detects [VectorField] usage without Pgvector.EntityFrameworkCore reference.
/// Emits WHIZ070 error when [VectorField] attribute is used but the required package is not referenced.
/// This ensures users get a helpful compile-time error guiding them to add the necessary package.
/// </summary>
/// <docs>operations/diagnostics/whiz070</docs>
/// <tests>tests/Whizbang.Generators.Tests/VectorDependencyAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class VectorDependencyAnalyzer : DiagnosticAnalyzer {
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";
  private const string PGVECTOR_ASSEMBLY_NAME = "Pgvector.EntityFrameworkCore";

  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      ImmutableArray.Create(DiagnosticDescriptors.VectorFieldMissingPackage);

  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(_onCompilationStart);
  }

  private static void _onCompilationStart(CompilationStartAnalysisContext context) {
    // Check if Pgvector.EntityFrameworkCore is referenced
    var hasPgvectorReference = _hasPgvectorReference(context.Compilation);

    // If package is present, no need to analyze - skip all property analysis
    if (hasPgvectorReference) {
      return;
    }

    // Package is NOT present, register to check for [VectorField] usage
    context.RegisterSymbolAction(ctx => _analyzeProperty(ctx), SymbolKind.Property);
  }

  private static bool _hasPgvectorReference(Compilation compilation) {
    // Check if any referenced assembly is Pgvector.EntityFrameworkCore
    foreach (var reference in compilation.References) {
      var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
      if (assemblySymbol is not null &&
          assemblySymbol.Name.Equals(PGVECTOR_ASSEMBLY_NAME, StringComparison.Ordinal)) {
        return true;
      }
    }

    return false;
  }

  private static void _analyzeProperty(SymbolAnalysisContext context) {
    var propertySymbol = (IPropertySymbol)context.Symbol;

    // Check if property has [VectorField] attribute
    foreach (var attribute in propertySymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() == VECTOR_FIELD_ATTRIBUTE) {
        // Found [VectorField] but package is not referenced - report diagnostic
        var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ??
                       propertySymbol.Locations.FirstOrDefault() ??
                       Location.None;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.VectorFieldMissingPackage,
            location,
            propertySymbol.Name
        ));
      }
    }
  }
}
