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
  /// <summary>
  /// Where the probes are compiled. The repository's .editorconfig files are embedded in this assembly under their
  /// repository-relative paths (the project file), and placed under this root as they sit under the repository's.
  /// </summary>
  private const string ROOT = "/repo";

  /// <summary>The embedded .editorconfig files' resource names start with this, followed by the repository-relative path.</summary>
  private const string EDITORCONFIG_RESOURCE_PREFIX = "editorconfig:";

  private static readonly AnalyzerConfigSet _editorConfigs = _loadEditorConfigs();

  [Test]
  public async Task TheRepositoryEditorConfigs_AreEmbeddedAsync() {
    var embedded = typeof(SonarQueueRulesTests).Assembly.GetManifestResourceNames()
      .Where(n => n.StartsWith(EDITORCONFIG_RESOURCE_PREFIX, StringComparison.Ordinal))
      .ToList();

    await Assert.That(embedded).IsEquivalentTo(["editorconfig:.editorconfig", "editorconfig:tests/.editorconfig"]);
  }

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

    var fullPath = $"{ROOT}/{path}";
    var tree = CSharpSyntaxTree.ParseText(source.ToString(), path: fullPath);
    var severities = _editorConfigs.GetOptionsForSourcePath(fullPath).TreeOptions;
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

  /// <summary>The repository's .editorconfig files, embedded in this assembly, as they sit under <see cref="ROOT"/>.</summary>
  private static AnalyzerConfigSet _loadEditorConfigs() {
    var assembly = typeof(SonarQueueRulesTests).Assembly;
    var configs = assembly.GetManifestResourceNames()
      .Where(n => n.StartsWith(EDITORCONFIG_RESOURCE_PREFIX, StringComparison.Ordinal))
      .Select(n => {
        using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
        return AnalyzerConfig.Parse(reader.ReadToEnd(), $"{ROOT}/{n[EDITORCONFIG_RESOURCE_PREFIX.Length..]}");
      })
      .ToArray();
    return AnalyzerConfigSet.Create(configs);
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
