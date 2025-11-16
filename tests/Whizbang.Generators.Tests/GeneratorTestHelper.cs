using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
  /// <returns>The generator driver result containing generated sources and diagnostics</returns>
  [RequiresAssemblyFiles()]
  public static GeneratorDriverRunResult RunGenerator<TGenerator>(string source)
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

    // Create compilation
    var compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees: new[] { syntaxTree },
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );

    // Create generator instance
    var generator = new TGenerator();

    // Create generator driver
    var driver = CSharpGeneratorDriver.Create(generator);

    // Run the generator
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);

    // Get the results
    return driver.GetRunResult();
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
}
