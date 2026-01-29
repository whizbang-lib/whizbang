using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Helper class for testing Roslyn analyzers.
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

    // Add reference to Whizbang.Core (for TrackedGuid, WhizbangId, etc.)
    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch {
      // If assembly can't be loaded, try to find it in current directory
      var coreAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Core.dll"
      );
      if (File.Exists(coreAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(coreAssemblyPath));
      }
    }

    // Create compilation
    var compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees: new[] { syntaxTree },
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    // Create analyzer instance
    var analyzer = new TAnalyzer();

    // Create compilation with analyzers
    var compilationWithAnalyzers = compilation.WithAnalyzers(
        ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

    // Get analyzer diagnostics only (exclude compiler diagnostics)
    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

    return diagnostics;
  }
}
