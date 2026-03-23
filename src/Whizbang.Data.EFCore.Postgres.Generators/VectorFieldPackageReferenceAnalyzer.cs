using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Roslyn analyzer that detects missing Pgvector package references when [VectorField] is used.
/// Reports WHIZ070 when Pgvector.EntityFrameworkCore is missing and WHIZ071 when Pgvector is missing.
/// </summary>
/// <remarks>
/// <para>
/// This analyzer finds classes implementing <c>IPerspectiveFor&lt;TModel, TEvent...&gt;</c>
/// and checks the TModel type for [VectorField] attributes on properties. When found, it verifies
/// that both Pgvector and Pgvector.EntityFrameworkCore packages are referenced.
/// </para>
/// <para>
/// The check can be suppressed by adding <c>[assembly: SuppressVectorPackageCheck]</c> to the project,
/// which is useful for custom vector implementations or testing scenarios.
/// </para>
/// </remarks>
/// <docs>diagnostics/WHIZ070</docs>
/// <docs>diagnostics/WHIZ071</docs>
/// <tests>VectorFieldPackageReferenceAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VectorFieldPackageReferenceAnalyzer : DiagnosticAnalyzer {
  private const string PGVECTOR_ASSEMBLY = "Pgvector";
  private const string PGVECTOR_EFCORE_ASSEMBLY = "Pgvector.EntityFrameworkCore";
  private const string SUPPRESS_ATTRIBUTE = "Whizbang.Core.Perspectives.SuppressVectorPackageCheckAttribute";
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [DiagnosticDescriptors.VectorFieldMissingPgvectorEFCorePackage,
       DiagnosticDescriptors.VectorFieldMissingPgvectorPackage];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(_analyzeCompilationStart);
  }

  private static void _analyzeCompilationStart(CompilationStartAnalysisContext context) {
    var compilation = context.Compilation;

    // Check for [SuppressVectorPackageCheck] assembly attribute early
    if (_hasSuppressAttribute(compilation)) {
      return;
    }

    // Track if any vector field is found across all types
    var hasVectorField = new ThreadSafeFlag();

    // Analyze each named type symbol
    context.RegisterSymbolAction(symbolContext => _analyzeType(symbolContext, hasVectorField), SymbolKind.NamedType);

    // At the end of compilation, report missing packages if vector fields were found
    context.RegisterCompilationEndAction(endContext => {
      if (!hasVectorField.IsSet) {
        return;
      }

      var hasPgvector = _hasAssemblyReference(endContext.Compilation, PGVECTOR_ASSEMBLY);
      var hasPgvectorEfCore = _hasAssemblyReference(endContext.Compilation, PGVECTOR_EFCORE_ASSEMBLY);

      if (!hasPgvectorEfCore) {
        endContext.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.VectorFieldMissingPgvectorEFCorePackage,
            Location.None));
      }

      if (!hasPgvector) {
        endContext.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.VectorFieldMissingPgvectorPackage,
            Location.None));
      }
    });
  }

  private static void _analyzeType(SymbolAnalysisContext context, ThreadSafeFlag hasVectorField) {
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

      // Check if model has any [VectorField] properties
      if (_hasVectorFieldProperty(modelType)) {
        hasVectorField.Set();
        return; // No need to check further once we found one
      }
    }
  }

  private static bool _hasSuppressAttribute(Compilation compilation) {
    return compilation.Assembly.GetAttributes()
        .Any(attr => attr.AttributeClass?.ToDisplayString() == SUPPRESS_ATTRIBUTE);
  }

  private static bool _hasAssemblyReference(Compilation compilation, string assemblyName) {
    return compilation.ReferencedAssemblyNames
        .Any(a => a.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
  }

  private static bool _hasVectorFieldProperty(INamedTypeSymbol type) {
    foreach (var member in type.GetMembers().OfType<IPropertySymbol>()) {
      if (member.IsStatic || member.IsIndexer || member.IsWriteOnly) {
        continue;
      }

      // Check for [VectorField] attribute
      foreach (var attr in member.GetAttributes()) {
        if (attr.AttributeClass?.ToDisplayString() == VECTOR_FIELD_ATTRIBUTE) {
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>
  /// Thread-safe flag for tracking state across concurrent symbol analysis.
  /// </summary>
  private sealed class ThreadSafeFlag {
    private int _value;

    public bool IsSet => Volatile.Read(ref _value) == 1;

    public void Set() {
      Interlocked.Exchange(ref _value, 1);
    }
  }
}
