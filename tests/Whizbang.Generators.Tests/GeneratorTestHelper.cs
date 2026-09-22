using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Helper class for testing source generators.
/// Provides utilities to compile source code and run generators.
/// </summary>
public static class GeneratorTestHelper {
  /// <summary>
  /// Runs a source generator against the provided source code.
  /// </summary>
  /// <typeparam name="TGenerator">The type of generator to run</typeparam>
  /// <param name="source">The C# source code to compile</param>
  /// <param name="additionalFiles">Optional (path, content) pairs surfaced to the generator as AdditionalFiles
  /// (e.g. a .whizbang/pinned-type-ledger.json consumed via AdditionalTextsProvider). Null/empty means none.</param>
  /// <returns>The generator driver result containing generated sources and diagnostics</returns>
  [RequiresAssemblyFiles()]
  public static GeneratorDriverRunResult RunGenerator<TGenerator>(
      string source, (string path, string content)[]? additionalFiles = null)
      where TGenerator : IIncrementalGenerator, new() {

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
    // IQueryable lives in these two, not in System.Linq. Without them a source using it still parses,
    // so a generator test over a queryable-returning surface finds no symbol and reports nothing
    // generated, which reads exactly like a generator that declined to emit.
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Expressions.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Queryable.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.ComponentModel.Primitives.dll")));

    // Add reference to System.Text.Json (for [JsonPolymorphic], [JsonDerivedType], etc.)
    references.Add(MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location));

    // Add reference to Whizbang.Core (for ICommand, IEvent, etc.)
    // Load by name since it's referenced by this test project
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

    // Add reference to Whizbang.Transports.HotChocolate (for GraphQLLensAttribute)
    try {
      var hotChocolateAssembly = System.Reflection.Assembly.Load("Whizbang.Transports.HotChocolate");
      references.Add(MetadataReference.CreateFromFile(hotChocolateAssembly.Location));
    } catch {
      var hotChocolateAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Transports.HotChocolate.dll"
      );
      if (File.Exists(hotChocolateAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(hotChocolateAssemblyPath));
      }
    }

    // Add reference to Whizbang.Transports.FastEndpoints (for RestLensAttribute)
    try {
      var fastEndpointsAssembly = System.Reflection.Assembly.Load("Whizbang.Transports.FastEndpoints");
      references.Add(MetadataReference.CreateFromFile(fastEndpointsAssembly.Location));
    } catch {
      var fastEndpointsAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Transports.FastEndpoints.dll"
      );
      if (File.Exists(fastEndpointsAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(fastEndpointsAssemblyPath));
      }
    }

    // Add reference to Whizbang.Transports.Mutations (for CommandEndpointAttribute)
    try {
      var mutationsAssembly = System.Reflection.Assembly.Load("Whizbang.Transports.Mutations");
      references.Add(MetadataReference.CreateFromFile(mutationsAssembly.Location));
    } catch {
      var mutationsAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Transports.Mutations.dll"
      );
      if (File.Exists(mutationsAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(mutationsAssemblyPath));
      }
    }

    // Create compilation
    var compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    // Create generator instance
    var generator = new TGenerator();

    // Create generator driver, surfacing any AdditionalFiles (e.g. the pinned-type ledger) to the generator.
    var driver = additionalFiles is { Length: > 0 }
        ? CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            additionalTexts: additionalFiles
                .Select(f => (AdditionalText)new TestAdditionalText(f.path, f.content))
                .ToImmutableArray())
        : CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], additionalTexts: null, parseOptions: null, optionsProvider: null);

    // Run the generator
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);

    // Get the results
    return driver.GetRunResult();
  }

  /// <summary>
  /// Runs a source generator against the provided source code with custom analyzer options.
  /// </summary>
  /// <typeparam name="TGenerator">The type of generator to run</typeparam>
  /// <param name="source">The C# source code to compile</param>
  /// <param name="globalOptions">Global analyzer options (e.g., MSBuild properties)</param>
  /// <returns>The generator driver result containing generated sources and diagnostics</returns>
  [RequiresAssemblyFiles()]
  public static GeneratorDriverRunResult RunGenerator<TGenerator>(
      string source,
      Dictionary<string, string> globalOptions)
      where TGenerator : IIncrementalGenerator, new() {

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
    // IQueryable lives in these two, not in System.Linq. Without them a source using it still parses,
    // so a generator test over a queryable-returning surface finds no symbol and reports nothing
    // generated, which reads exactly like a generator that declined to emit.
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Expressions.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.Queryable.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.ComponentModel.Primitives.dll")));

    // Add reference to System.Text.Json (for [JsonPolymorphic], [JsonDerivedType], etc.)
    references.Add(MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location));

    // Add reference to Whizbang.Core (for ICommand, IEvent, etc.)
    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch {
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

    // Create generator instance
    var generator = new TGenerator();

    // Create options provider
    var optionsProvider = new TestAnalyzerConfigOptionsProvider(globalOptions);

    // Create generator driver with options provider
    var driver = CSharpGeneratorDriver.Create(
        generators: [generator.AsSourceGenerator()],
        optionsProvider: optionsProvider
    );

    // Run the generator
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);

    // Get the results
    return driver.GetRunResult();
  }

  /// <summary>Minimal in-memory <see cref="AdditionalText"/> for supplying AdditionalFiles content in tests.</summary>
  private sealed class TestAdditionalText(string path, string content) : AdditionalText {
    public override string Path { get; } = path;
    public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default)
      => SourceText.From(content);
  }

  /// <summary>
  /// Gets the generated source by file name from the generator result.
  /// Checks both GeneratedSources (for all files including SQL, JSON, etc.) and GeneratedTrees (C# syntax trees).
  /// </summary>
  public static string? GetGeneratedSource(GeneratorDriverRunResult result, string fileName) {
    // First try GeneratedSources (works for all file types including SQL, JSON, etc.)
    foreach (var generatorResult in result.Results) {
      var source = generatorResult.GeneratedSources
          .FirstOrDefault(s => Path.GetFileName(s.HintName) == fileName);
      if (source.SourceText != null) {
        return source.SourceText.ToString();
      }
    }

    // Fall back to GeneratedTrees (C# syntax trees only)
    return result.GeneratedTrees
        .FirstOrDefault(t => Path.GetFileName(t.FilePath) == fileName)
        ?.ToString();
  }

  /// <summary>
  /// Gets all generated sources from the generator result.
  /// </summary>
  public static IEnumerable<(string FileName, string Source)> GetAllGeneratedSources(GeneratorDriverRunResult result) {
    return result.GeneratedTrees
        .Select(t => (Path.GetFileName(t.FilePath), t.ToString()));
  }

  /// <summary>
  /// Creates a simple compilation from source code for testing Roslyn symbol APIs.
  /// </summary>
  /// <param name="source">The C# source code to compile</param>
  /// <param name="assemblyName">Optional assembly name (defaults to "TestAssembly")</param>
  /// <returns>A CSharpCompilation that can be used to get type symbols</returns>
  public static CSharpCompilation CreateCompilation(string source, string assemblyName = "TestAssembly") {
    // Parse the source code
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    // Get references to basic assemblies
    var references = new List<MetadataReference>();
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));
    // Whizbang.Core too, so a source can carry the framework's own attributes rather than a
    // look-alike declared beside it. Discovery matches an attribute by its display name, so a
    // hand-rolled copy passes while proving nothing about the real one.
    _addWhizbangCore(references);

    // Create compilation
    return CSharpCompilation.Create(
        assemblyName: assemblyName,
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );
  }

  /// <summary>Adds a reference to Whizbang.Core, by name first and then from beside the tests.</summary>
  private static void _addWhizbangCore(List<MetadataReference> references) {
    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch (FileNotFoundException) {
      var coreAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Core.dll"
      );
      if (File.Exists(coreAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(coreAssemblyPath));
      }
    }
  }

  /// <summary>
  /// Runs a source generator and returns the ERROR diagnostics of the resulting compilation
  /// (original source + every generated tree). Unlike <see cref="GeneratorDriverRunResult.Diagnostics"/>,
  /// which only carries the generator's own diagnostics, this surfaces downstream compile errors in the
  /// GENERATED code — e.g. a populator that assigns a <c>Guid?</c> value to a <c>string?</c> property.
  /// References the full shared-framework assembly set (plus Whizbang.Core) so a clean baseline compiles
  /// and the only remaining errors come from the generated output.
  /// </summary>
  [RequiresAssemblyFiles()]
  public static ImmutableArray<Diagnostic> GetGeneratedCompilationErrors<TGenerator>(
      string source, (string path, string content)[]? additionalFiles = null)
      where TGenerator : IIncrementalGenerator, new() =>
    GetGeneratedCompilationErrors([new TGenerator()], source, additionalFiles);

  /// <summary>
  /// The same, for several generators at once.
  /// </summary>
  /// <remarks>
  /// A generator whose output references a sibling generator's output does not compile on its own: the
  /// harness has to run the siblings too, or the caller is left comparing one broken compile against
  /// another and calling the absence of a difference a pass. Running the real set instead lets a test
  /// assert the thing it means, which is that the generated code compiles.
  /// </remarks>
  [RequiresAssemblyFiles()]
  public static ImmutableArray<Diagnostic> GetGeneratedCompilationErrors(
      IReadOnlyList<IIncrementalGenerator> generators,
      string source,
      (string path, string content)[]? additionalFiles = null) {
    ArgumentNullException.ThrowIfNull(generators);

    var syntaxTree = CSharpSyntaxTree.ParseText(source);
    var compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees: [syntaxTree],
        references: _fullFrameworkReferences(),
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    var driver = CSharpGeneratorDriver.Create(
        generators: [.. generators.Select(static g => g.AsSourceGenerator())],
        additionalTexts: [.. (additionalFiles ?? [])
            .Select(f => (AdditionalText)new TestAdditionalText(f.path, f.content))]);
    var ran = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

    _throwIfAnyGeneratorFailed(ran.GetRunResult());

    return [.. outputCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
  }

  /// <summary>
  /// Turns a generator that threw into a loud failure instead of a clean compile.
  /// </summary>
  /// <remarks>
  /// A generator that throws emits nothing, and the driver keeps the exception in its own run result
  /// rather than in the compilation, so the compilation handed back is the one the caller started with
  /// and its error list is empty. Every caller of this helper reads an empty error list as "the
  /// generated code is valid", so without this the two states -- nothing was wrong, and nothing ran --
  /// are the same answer. A develop run failed with one half of a comparison reporting zero errors
  /// against the other's three, and the harness had discarded the only record of why.
  /// </remarks>
  private static void _throwIfAnyGeneratorFailed(GeneratorDriverRunResult result) {
    foreach (var generatorResult in result.Results) {
      if (generatorResult.Exception is null) {
        continue;
      }
      var name = generatorResult.Generator.GetGeneratorType().Name;
      throw new InvalidOperationException(
        $"The generator {name} threw instead of generating. It emitted no source, so the compilation "
        + "carries no errors and would otherwise have been reported as a clean compile.",
        generatorResult.Exception);
    }
  }

  /// <summary>
  /// Builds a reference set covering the entire shared framework (via the trusted-platform-assemblies list)
  /// plus Whizbang.Core, so a snippet + its generated code can be compiled without hunting facade assemblies.
  /// </summary>
  private static List<MetadataReference> _fullFrameworkReferences() {
    var references = new List<MetadataReference>();

    var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
    references.AddRange(trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
      .Where(File.Exists)
      .Select(path => MetadataReference.CreateFromFile(path)));

    // Whizbang.Core is a project reference (not a platform assembly) — add it explicitly for the
    // message/attribute types the generated populator and registry reference.
    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch {
      var coreAssemblyPath = Path.Combine(
          Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
          "Whizbang.Core.dll"
      );
      if (File.Exists(coreAssemblyPath)) {
        references.Add(MetadataReference.CreateFromFile(coreAssemblyPath));
      }
    }

    return references;
  }

  /// <summary>
  /// Test implementation of AnalyzerConfigOptionsProvider for passing MSBuild properties to generators.
  /// </summary>
  private sealed class TestAnalyzerConfigOptionsProvider(Dictionary<string, string> globalOptions) : AnalyzerConfigOptionsProvider {
    private readonly Dictionary<string, string> _globalOptions = globalOptions;

    public override AnalyzerConfigOptions GlobalOptions =>
        new TestAnalyzerConfigOptions(_globalOptions);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
        TestAnalyzerConfigOptions.Empty;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        TestAnalyzerConfigOptions.Empty;
  }

  /// <summary>
  /// Test implementation of AnalyzerConfigOptions.
  /// </summary>
  private sealed class TestAnalyzerConfigOptions(Dictionary<string, string> options) : AnalyzerConfigOptions {
    private readonly Dictionary<string, string> _options = options;

    public static readonly TestAnalyzerConfigOptions Empty =
        new([]);

    public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) {
      return _options.TryGetValue(key, out value!);
    }
  }
}
