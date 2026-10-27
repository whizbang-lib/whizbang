// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// Compiles a source snippet in memory and runs one analyzer over it.
/// </summary>
internal static class RepoAnalyzerTestHelper {
  /// <summary>
  /// The analyzer's diagnostics for <paramref name="source"/>.
  /// </summary>
  /// <param name="source">The C# source to compile.</param>
  /// <param name="withJsonReference">
  /// Whether System.Text.Json is referenced. A compilation without it proves the analyzer stays quiet
  /// when the types it looks for do not exist.
  /// </param>
  /// <returns>The analyzer diagnostics, without the compiler's own.</returns>
  [RequiresAssemblyFiles()]
  public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync<TAnalyzer>(
      string source, bool withJsonReference = true)
      where TAnalyzer : DiagnosticAnalyzer, new() {
    var compilation = CreateCompilation(source, withJsonReference);
    return await compilation
      .WithAnalyzers([new TAnalyzer()])
      .GetAnalyzerDiagnosticsAsync();
  }

  /// <summary>
  /// A compilation over <paramref name="source"/> with the framework references the snippets use.
  /// </summary>
  /// <param name="source">The C# source to compile.</param>
  /// <param name="withJsonReference">Whether System.Text.Json is referenced.</param>
  /// <returns>The compilation; its own diagnostics say whether the snippet bound.</returns>
  [RequiresAssemblyFiles()]
  public static Compilation CreateCompilation(string source, bool withJsonReference = true) {
    var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    var references = new List<MetadataReference> {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
      MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Memory.dll")),
      MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Threading.Tasks.dll")),
      MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "netstandard.dll")),
    };
    if (withJsonReference) {
      references.Add(MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonDocument).Assembly.Location));
    }

    return CSharpCompilation.Create(
      assemblyName: "RepoAnalyzerTestAssembly",
      syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
      references: references,
      options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
  }
}
