using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Helper class for testing Roslyn analyzers in the EFCore Postgres project.
/// Provides utilities to compile source code and get analyzer diagnostics.
/// </summary>
public static class AnalyzerTestHelper {
  /// <summary>
  /// Runs an analyzer against the provided source code and returns diagnostics.
  /// </summary>
  /// <typeparam name="TAnalyzer">The type of analyzer to run</typeparam>
  /// <param name="source">The C# source code to compile</param>
  /// <returns>The diagnostics reported by the analyzer</returns>
  [RequiresAssemblyFiles()]
  public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync<TAnalyzer>(string source)
      where TAnalyzer : DiagnosticAnalyzer, new() {
    return await GetDiagnosticsAsync<TAnalyzer>(source, includePgvector: true, includePgvectorEfCore: true);
  }

  /// <summary>
  /// Runs an analyzer against the provided source code with configurable package references.
  /// Used for testing package reference analyzers.
  /// </summary>
  /// <typeparam name="TAnalyzer">The type of analyzer to run</typeparam>
  /// <param name="source">The C# source code to compile</param>
  /// <param name="includePgvector">Whether to include Pgvector assembly reference</param>
  /// <param name="includePgvectorEfCore">Whether to include Pgvector.EntityFrameworkCore assembly reference</param>
  /// <returns>The diagnostics reported by the analyzer</returns>
  [RequiresAssemblyFiles()]
  public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync<TAnalyzer>(
      string source,
      bool includePgvector,
      bool includePgvectorEfCore)
      where TAnalyzer : DiagnosticAnalyzer, new() {

    // Parse the source code
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    // Get references to assemblies we need
    var references = new List<MetadataReference>();

    // Add reference to System.Runtime and other basic assemblies
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.ComponentModel.Primitives.dll")));

    // Add reference to System.ComponentModel.Annotations (for [NotMapped] attribute)
    // Note: NotMappedAttribute is in System.ComponentModel.Annotations, not DataAnnotations
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.ComponentModel.Annotations.dll")));

    // Add reference to System.Text.Json (for [JsonIgnore] attribute)
    references.Add(MetadataReference.CreateFromFile(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute).Assembly.Location));

    // Add reference to Whizbang.Core (for IPerspectiveFor, VectorFieldAttribute, etc.)
    _tryAddAssemblyReference(references, "Whizbang.Core");

    // Conditionally add Pgvector package reference
    if (includePgvector) {
      _tryAddAssemblyReference(references, "Pgvector");
    }

    // Conditionally add Pgvector.EntityFrameworkCore package reference
    if (includePgvectorEfCore) {
      _tryAddAssemblyReference(references, "Pgvector.EntityFrameworkCore");
    }

    // Create compilation
    var compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    // Create analyzer instance
    var analyzer = new TAnalyzer();

    // Create compilation with analyzers
    var compilationWithAnalyzers = compilation.WithAnalyzers(
        [analyzer]);

    // Get analyzer diagnostics only (exclude compiler diagnostics)
    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

    return diagnostics;
  }

  /// <summary>
  /// Attempts to add an assembly reference by name.
  /// Tries loading from AppDomain first, then from executing assembly directory.
  /// </summary>
  private static void _tryAddAssemblyReference(List<MetadataReference> references, string assemblyName) {
    try {
      var assembly = System.Reflection.Assembly.Load(assemblyName);
      references.Add(MetadataReference.CreateFromFile(assembly.Location));
    } catch {
      // If assembly can't be loaded, try to find it in current directory
      var assemblyFileName = assemblyName + ".dll";
      var assemblyFilePath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          assemblyFileName
      );
      if (File.Exists(assemblyFilePath)) {
        references.Add(MetadataReference.CreateFromFile(assemblyFilePath));
      }
    }
  }
}
