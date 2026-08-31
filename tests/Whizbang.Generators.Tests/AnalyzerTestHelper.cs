using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Helper class for testing Roslyn analyzers.
/// Provides utilities to compile source code and get analyzer diagnostics.
/// </summary>
public static class AnalyzerTestHelper {
  /// <summary>
  /// Runs an analyzer against the provided source code (and optional AdditionalFiles) and returns diagnostics.
  /// </summary>
  /// <typeparam name="TAnalyzer">The type of analyzer to run</typeparam>
  /// <param name="source">The C# source code to compile</param>
  /// <param name="additionalFiles">Optional (path, content) pairs surfaced to the analyzer as AdditionalFiles
  /// (e.g. a pinned-type-ledger.json). Null/empty means no AdditionalFiles.</param>
  /// <returns>The diagnostics reported by the analyzer</returns>
  [RequiresAssemblyFiles()]
  public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync<TAnalyzer>(
      string source, (string path, string content)[]? additionalFiles = null)
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
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Threading.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Threading.Tasks.dll")));
    // System.Net.Http so HttpClient resolves to a symbol: the purity analyzer classifies I/O by
    // the containing type of the resolved method, and an unresolved call is simply not seen.
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Net.Http.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "netstandard.dll")));

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
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    // Create analyzer instance
    var analyzer = new TAnalyzer();

    // Surface any AdditionalFiles (e.g. the pinned-type ledger) to the analyzer.
    AnalyzerOptions? analyzerOptions = null;
    if (additionalFiles is { Length: > 0 }) {
      var texts = additionalFiles
        .Select(f => (AdditionalText)new TestAdditionalText(f.path, f.content))
        .ToImmutableArray();
      analyzerOptions = new AnalyzerOptions(texts);
    }

    // Create compilation with analyzers
    var compilationWithAnalyzers = compilation.WithAnalyzers([analyzer], analyzerOptions);

    // Get analyzer diagnostics only (exclude compiler diagnostics)
    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

    return diagnostics;
  }

  /// <summary>Minimal in-memory <see cref="AdditionalText"/> for supplying AdditionalFiles content in tests.</summary>
  private sealed class TestAdditionalText(string path, string content) : AdditionalText {
    public override string Path { get; } = path;
    public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default)
      => SourceText.From(content);
  }
}
