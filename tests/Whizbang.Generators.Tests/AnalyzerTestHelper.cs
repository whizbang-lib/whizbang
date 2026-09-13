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
  /// A compilation over the source with the framework references the analyzers need.
  /// </summary>
  /// <param name="source">The source to compile.</param>
  /// <returns>The compilation, whose diagnostics say whether the framework attributes bound.</returns>
  /// <remarks>
  /// Shared with <see cref="GetDiagnosticsAsync"/> rather than copied, because a test that asks a
  /// question about symbols has to compile against the same references the analyzer will see. A
  /// separate reference list drifts, and the failure is quiet: an attribute that does not bind looks
  /// exactly like an attribute nobody wrote, so a test measures the reference list and reads as a
  /// statement about behavior.
  /// </remarks>
  [RequiresAssemblyFiles()]
  public static Compilation CreateCompilationWithFrameworkReferences(string source) {
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
    // System.Linq.Expressions and System.Linq.Queryable so IQueryable<T> and the Queryable
    // operators resolve: a query analyzer infers the lambda parameter's type from the operator's
    // Expression<Func<T, bool>> signature, and an unresolved operator leaves the parameter untyped.
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Expressions.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Queryable.dll")));
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

    return compilation;
  }

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
      string source,
      (string path, string content)[]? additionalFiles = null,
      Dictionary<string, string>? globalOptions = null)
      where TAnalyzer : DiagnosticAnalyzer, new() {

    var compilation = CreateCompilationWithFrameworkReferences(source);

    // Create analyzer instance
    var analyzer = new TAnalyzer();

    // Surface any AdditionalFiles (e.g. the pinned-type ledger) and any build properties an analyzer
    // reads through AnalyzerConfigOptions. Both have to be supplied here or the analyzer sees an
    // empty configuration and a test about a configured option would pass for the wrong reason.
    AnalyzerOptions? analyzerOptions = null;
    if (additionalFiles is { Length: > 0 } || globalOptions is { Count: > 0 }) {
      var texts = (additionalFiles ?? [])
        .Select(f => (AdditionalText)new TestAdditionalText(f.path, f.content))
        .ToImmutableArray();
      analyzerOptions = new AnalyzerOptions(
        texts, new TestAnalyzerConfigOptionsProvider(globalOptions ?? []));
    }

    // Create compilation with analyzers
    var compilationWithAnalyzers = compilation.WithAnalyzers([analyzer], analyzerOptions);

    // Get analyzer diagnostics only (exclude compiler diagnostics)
    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

    return diagnostics;
  }

  /// <summary>
  /// Supplies build properties to an analyzer that reads <c>AnalyzerConfigOptions</c>.
  /// </summary>
  /// <remarks>
  /// Only the global options are populated. A per-tree lookup would need a real editorconfig, and no
  /// analyzer here reads one; returning the same empty set for every tree keeps the harness honest
  /// about what it actually provides.
  /// </remarks>
  private sealed class TestAnalyzerConfigOptionsProvider(Dictionary<string, string> globalOptions)
    : AnalyzerConfigOptionsProvider {
    public override AnalyzerConfigOptions GlobalOptions { get; } = new TestAnalyzerConfigOptions(globalOptions);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
      new TestAnalyzerConfigOptions([]);

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
      new TestAnalyzerConfigOptions([]);
  }

  private sealed class TestAnalyzerConfigOptions(Dictionary<string, string> options) : AnalyzerConfigOptions {
    public override bool TryGetValue(string key, out string value) =>
      options.TryGetValue(key, out value!);
  }

  /// <summary>Minimal in-memory <see cref="AdditionalText"/> for supplying AdditionalFiles content in tests.</summary>
  private sealed class TestAdditionalText(string path, string content) : AdditionalText {
    public override string Path { get; } = path;
    public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default)
      => SourceText.From(content);
  }
}
