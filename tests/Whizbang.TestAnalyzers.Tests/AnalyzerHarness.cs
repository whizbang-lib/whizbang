// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.TestAnalyzers.Tests;

/// <summary>
/// Compiles a test source in memory against the same TUnit this repository pins, and runs an analyzer
/// over it.
/// </summary>
/// <remarks>
/// The references are every assembly on the running test host's trusted platform list, which includes
/// the TUnit packages this project restores. A rule about which TUnit overload a call binds to is only
/// tested if the call binds the way it does in the real test projects, so the tests never stub TUnit.
/// </remarks>
internal static class AnalyzerHarness {
  private static readonly ImmutableArray<MetadataReference> _references = _loadReferences();
  private static readonly ImmutableArray<MetadataReference> _referencesWithoutTUnit = [.. _references
    .Where(reference => Path.GetFileName(reference.Display)?.StartsWith("TUnit", StringComparison.Ordinal) != true)];

  /// <summary>The compilation of <paramref name="source"/>, failing the test on any compiler error.</summary>
  public static CSharpCompilation Compile(string source, bool referenceTUnit = true) => Compile([source], referenceTUnit);

  /// <summary>The compilation of <paramref name="sources"/>, one syntax tree (file) each, failing the test on any compiler error.</summary>
  public static CSharpCompilation Compile(IReadOnlyList<string> sources, bool referenceTUnit = true) {
    var compilation = CSharpCompilation.Create(
      assemblyName: "AnalyzerHarnessSubject",
      syntaxTrees: [.. sources.Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)))],
      references: referenceTUnit ? _references : _referencesWithoutTUnit,
      options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length > 0) {
      throw new InvalidOperationException("The test source does not compile, so no analyzer result about it means anything: "
        + string.Join("; ", errors.Select(e => e.ToString())));
    }
    return compilation;
  }

  /// <summary>Every diagnostic <paramref name="analyzer"/> reports on <paramref name="source"/>.</summary>
  public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(DiagnosticAnalyzer analyzer, string source, bool referenceTUnit = true) =>
    await AnalyzeAsync(analyzer, source, ImmutableDictionary<string, string>.Empty, referenceTUnit);

  /// <summary>
  /// Every diagnostic <paramref name="analyzer"/> reports on <paramref name="source"/>, with
  /// <paramref name="globalOptions"/> as the project's global analyzer options: the keys MSBuild writes for a
  /// <c>CompilerVisibleProperty</c>, such as <c>build_property.WhizbangShardedTests</c>.
  /// </summary>
  public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
      DiagnosticAnalyzer analyzer, string source, IReadOnlyDictionary<string, string> globalOptions, bool referenceTUnit = true) =>
    await AnalyzeAsync(analyzer, [source], globalOptions, referenceTUnit);

  /// <summary>As <see cref="AnalyzeAsync(DiagnosticAnalyzer, string, IReadOnlyDictionary{string, string}, bool)"/>, over several files.</summary>
  public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
      DiagnosticAnalyzer analyzer, IReadOnlyList<string> sources, IReadOnlyDictionary<string, string> globalOptions, bool referenceTUnit = true) {
    var compilation = Compile(sources, referenceTUnit);
    var options = new AnalyzerOptions([], new GlobalOptionsProvider(globalOptions));
    var analyzed = compilation.WithAnalyzers([analyzer], options);
    return await analyzed.GetAnalyzerDiagnosticsAsync();
  }

  private static ImmutableArray<MetadataReference> _loadReferences() {
    var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
    return [.. trusted.Split(Path.PathSeparator).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
  }

  /// <summary>Global options only, as a project's <c>.globalconfig</c> presents them; no per-file options.</summary>
  private sealed class GlobalOptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider {
    private readonly DictionaryOptions _global = new(values);
    private static readonly DictionaryOptions _empty = new(ImmutableDictionary<string, string>.Empty);

    public override AnalyzerConfigOptions GlobalOptions => _global;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _empty;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _empty;
  }

  private sealed class DictionaryOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions {
    public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
      values.TryGetValue(key, out value);
  }
}
