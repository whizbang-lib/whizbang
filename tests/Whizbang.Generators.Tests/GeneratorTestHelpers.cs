using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Helper methods for testing source generators.
/// Provides utilities to run generators and inspect their output.
/// </summary>
public static class GeneratorTestHelpers {
  /// <summary>
  /// Runs the EFCorePerspectiveConfigurationGenerator on the provided source code.
  /// Returns the generator output for inspection.
  /// </summary>
  public static async Task<GeneratorResult> RunEFCoreGeneratorAsync(string source) {
    // Create compilation from source
    var compilation = _createCompilation(source);

    // Create generator driver
    var generator = new EFCorePerspectiveConfigurationGenerator();
    var driver = CSharpGeneratorDriver.Create(generator);

    // Run generator
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);

    // Get results
    var runResult = driver.GetRunResult();

    return await Task.FromResult(new GeneratorResult {
      Compilation = compilation,
      GeneratedSources = runResult.GeneratedTrees
        .Select(t => new GeneratedSource {
          HintName = _getHintName(runResult, t),
          SourceText = t.GetText()
        })
        .ToImmutableArray(),
      Diagnostics = runResult.Diagnostics
    });
  }

  /// <summary>
  /// Runs the EFCoreServiceRegistrationGenerator on the provided source code.
  /// Tests attribute-based DbContext discovery and generated registration code.
  /// Returns the generator output for inspection.
  /// </summary>
  public static async Task<GeneratorResult> RunServiceRegistrationGeneratorAsync(string source) {
    // Create compilation from source with EF Core references
    var compilation = _createCompilationWithEFCore(source);

    // Create generator driver
    var generator = new EFCoreServiceRegistrationGenerator();
    var driver = CSharpGeneratorDriver.Create(generator);

    // Run generator
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);

    // Get results
    var runResult = driver.GetRunResult();

    return await Task.FromResult(new GeneratorResult {
      Compilation = compilation,
      GeneratedSources = runResult.GeneratedTrees
        .Select(t => new GeneratedSource {
          HintName = _getHintName(runResult, t),
          SourceText = t.GetText()
        })
        .ToImmutableArray(),
      Diagnostics = runResult.Diagnostics
    });
  }

  /// <summary>
  /// Creates a CSharpCompilation from source code with necessary references.
  /// </summary>
  private static CSharpCompilation _createCompilation(string source) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new[] {
      MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location),  // System.Private.CoreLib
      MetadataReference.CreateFromFile(typeof(Console).GetTypeInfo().Assembly.Location), // System.Console
      MetadataReference.CreateFromFile(typeof(Enumerable).GetTypeInfo().Assembly.Location), // System.Linq
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("netstandard").Location),
      MetadataReference.CreateFromFile(typeof(Core.IEvent).GetTypeInfo().Assembly.Location), // Whizbang.Core
    };

    return CSharpCompilation.Create(
      assemblyName: "TestAssembly",
      syntaxTrees: new[] { syntaxTree },
      references: references,
      options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );
  }

  /// <summary>
  /// Creates a CSharpCompilation from source code with EF Core and Whizbang.Data references.
  /// Used for testing EFCore generators that require DbContext and perspective types.
  /// </summary>
  private static CSharpCompilation _createCompilationWithEFCore(string source) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new[] {
      MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location),  // System.Private.CoreLib
      MetadataReference.CreateFromFile(typeof(Console).GetTypeInfo().Assembly.Location), // System.Console
      MetadataReference.CreateFromFile(typeof(Enumerable).GetTypeInfo().Assembly.Location), // System.Linq
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
      MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("netstandard").Location),
      MetadataReference.CreateFromFile(typeof(Core.IEvent).GetTypeInfo().Assembly.Location), // Whizbang.Core
      MetadataReference.CreateFromFile(typeof(DbContext).GetTypeInfo().Assembly.Location), // EF Core
      MetadataReference.CreateFromFile(typeof(WhizbangDbContextAttribute).GetTypeInfo().Assembly.Location), // Whizbang.Data.EFCore.Custom
    };

    return CSharpCompilation.Create(
      assemblyName: "TestAssembly",
      syntaxTrees: new[] { syntaxTree },
      references: references,
      options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    );
  }

  /// <summary>
  /// Gets the hint name for a generated syntax tree from the run result.
  /// </summary>
  private static string _getHintName(GeneratorDriverRunResult runResult, SyntaxTree tree) {
    foreach (var result in runResult.Results) {
      foreach (var generated in result.GeneratedSources) {
        if (generated.SyntaxTree == tree) {
          return generated.HintName;
        }
      }
    }
    return "unknown.g.cs";
  }
}

/// <summary>
/// Result from running a source generator.
/// </summary>
public record GeneratorResult {
  public required CSharpCompilation Compilation { get; init; }
  public required ImmutableArray<GeneratedSource> GeneratedSources { get; init; }
  public required ImmutableArray<Diagnostic> Diagnostics { get; init; }
}

/// <summary>
/// A single file generated by a source generator.
/// </summary>
public record GeneratedSource {
  public required string HintName { get; init; }
  public required Microsoft.CodeAnalysis.Text.SourceText SourceText { get; init; }
}
