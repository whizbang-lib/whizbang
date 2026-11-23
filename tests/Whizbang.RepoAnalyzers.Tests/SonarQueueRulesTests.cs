// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// #2515: SonarCloud's quality gate fails a pull request in the merge queue on cognitive complexity (S3776, above 15)
/// and parameter count (S107, above 7) in library code, so the build reports both as errors in <c>src/</c>, at the same
/// thresholds, before the queue does. Runs the SonarAnalyzer.CSharp the build uses, configured by the repository's own
/// .editorconfig files, over a member just over and one exactly at each threshold.
/// </summary>
/// <remarks>
/// SonarCloud analyzes test projects with its test-code rules only, which include neither rule, and does not analyze
/// samples, benchmarks or tools at all (sonar.config), so the rules stay off there: turning them on would fail the build
/// on 130 findings the gate has never reported. <c>src/</c> had none when the rules were turned on; the reviewed
/// exceptions there carry a <c>[SuppressMessage]</c> or <c>#pragma</c> with the reason, as they did before.
/// </remarks>
public class SonarQueueRulesTests {
  private static readonly string _root = _findRepositoryRoot();

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("src/Whizbang.Core/Probe.cs")]
  [Arguments("src/Whizbang.Testing/Probe.cs")]
  [Arguments("src/Whizbang.Generators/Probe.cs")]
  public async Task LibraryCode_OverEitherThreshold_FailsTheBuildAsync(string path) {
    var diagnostics = await _analyzeAsync(path, complexity: 16, parameters: 8);

    await Assert.That(diagnostics.Select(d => d.Id).Order().ToList()).IsEquivalentTo(["S107", "S3776"]);
    await Assert.That(diagnostics.All(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    await Assert.That(diagnostics.Single(d => d.Id == "S3776").GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("from 16 to the 15 allowed");
    await Assert.That(diagnostics.Single(d => d.Id == "S107").GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("8 parameters, which is greater than the 7 authorized");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task LibraryCode_AtEitherThreshold_IsCleanAsync() {
    var diagnostics = await _analyzeAsync("src/Whizbang.Core/Probe.cs", complexity: 15, parameters: 7);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("tests/Whizbang.Core.Tests/Probe.cs")]
  [Arguments("samples/ECommerce/Probe.cs")]
  [Arguments("benchmarks/Whizbang.Benchmarks/Probe.cs")]
  [Arguments("tools/Whizbang.Migrate/Probe.cs")]
  public async Task CodeTheQualityGateDoesNotAnalyze_IsNotReportedAsync(string path) {
    var diagnostics = await _analyzeAsync(path, complexity: 16, parameters: 8);

    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// The S3776 and S107 diagnostics for a class with one method of <paramref name="complexity"/> sequential ifs and one
  /// of <paramref name="parameters"/> parameters, compiled at <paramref name="path"/> under the repository root with the
  /// severities the repository's .editorconfig files give that path and warnings as errors, as Directory.Build.props sets.
  /// </summary>
  [RequiresAssemblyFiles()]
  private static async Task<ImmutableArray<Diagnostic>> _analyzeAsync(string path, int complexity, int parameters) {
    var source = new StringBuilder("namespace Probe;\n\npublic static class Members {\n  public static int Branchy(int x) {\n    var y = 0;\n");
    for (var i = 0; i < complexity; i++) {
      source.Append("    if (x == ").Append(i).Append(") { y++; }\n");
    }
    source.Append("    return y;\n  }\n\n  public static int Wide(")
      .AppendJoin(", ", Enumerable.Range(0, parameters).Select(i => $"int p{i}"))
      .Append(") => p0;\n}\n");

    var fullPath = Path.Combine(_root, path);
    var tree = CSharpSyntaxTree.ParseText(source.ToString(), path: fullPath);
    var severities = _editorConfigSeverities(fullPath);
    var compilation = RepoAnalyzerTestHelper.CreateCompilation("");
    compilation = compilation
      .ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), tree)
      .WithOptions(compilation.Options
        .WithGeneralDiagnosticOption(ReportDiagnostic.Error)
        .WithSyntaxTreeOptionsProvider(new EditorConfigSeverities(severities)));

    var sonar = new AnalyzerFileReference(Path.Combine(AppContext.BaseDirectory, "sonar", "SonarAnalyzer.CSharp.dll"), new FileAnalyzerLoader())
      .GetAnalyzers(LanguageNames.CSharp)
      .Where(a => a.SupportedDiagnostics.Any(d => d.Id is "S3776" or "S107"))
      .ToImmutableArray();
    var diagnostics = await compilation.WithAnalyzers(sonar).GetAnalyzerDiagnosticsAsync();
    return [.. diagnostics.Where(d => d.Id is "S3776" or "S107")];
  }

  /// <summary>The rule severities the .editorconfig files from the repository root down to its folder give <paramref name="fullPath"/>.</summary>
  private static ImmutableDictionary<string, ReportDiagnostic> _editorConfigSeverities(string fullPath) {
    var configs = new List<AnalyzerConfig>();
    for (var directory = Path.GetDirectoryName(fullPath); directory?.StartsWith(_root, StringComparison.Ordinal) == true; directory = Path.GetDirectoryName(directory)) {
      var file = Path.Combine(directory, ".editorconfig");
      if (File.Exists(file)) {
        configs.Add(AnalyzerConfig.Parse(File.ReadAllText(file), file));
      }
    }
    return AnalyzerConfigSet.Create(configs).GetOptionsForSourcePath(fullPath).TreeOptions;
  }

  private static string _findRepositoryRoot() {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Whizbang.slnx"))) {
      directory = directory.Parent;
    }
    return directory?.FullName ?? throw new InvalidOperationException("The repository root (Whizbang.slnx) was not found above the test output.");
  }

  /// <summary>Hands the compiler a tree's .editorconfig severities the way the build does, so warnings-as-errors applies.</summary>
  private sealed class EditorConfigSeverities(ImmutableDictionary<string, ReportDiagnostic> severities) : SyntaxTreeOptionsProvider {
    public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) => GeneratedKind.NotGenerated;

    public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity) =>
      severities.TryGetValue(diagnosticId, out severity);

    public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity) {
      severity = ReportDiagnostic.Default;
      return false;
    }
  }

  private sealed class FileAnalyzerLoader : IAnalyzerAssemblyLoader {
    public void AddDependencyLocation(string fullPath) { }

    public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
  }
}
