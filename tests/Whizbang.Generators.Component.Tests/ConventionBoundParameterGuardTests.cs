// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Suppressors;
using Whizbang.Generators.Tests;

namespace Whizbang.Generators.Component.Tests;

/// <summary>
/// #1378 guard: a consumer that writes every convention-bound shape and builds with Roslynator and the Sonar
/// analyzer gets no unused-parameter finding the framework caused. These run the real rules from the packages
/// the repository already restores (Component: the analyzers load from disk), first without the suppressor to
/// prove the rules do fire on the fixture, then with it.
/// </summary>
[Category("Analyzers")]
public class ConventionBoundParameterGuardTests {
  private static readonly string[] _unusedParameterRules = ["RCS1163", "S1172"];

  // Every convention-bound shape the generators accept (the audit in #1378 found one kind: [CollectiveApplyFor]),
  // each with a body that ignores the event, as a constant-setter handler does. Internal, as a consumer's
  // handlers usually are, so a rule that skips the public surface still looks at them.
  private const string CONSUMER = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Messaging;
    using Whizbang.Core.Perspectives;

    namespace Consumer;

    internal sealed record ArchiveJobsCollectiveEvent : CollectiveEventBase;
    internal sealed class JobModel { public bool Archived { get; set; } }

    internal sealed class Handlers {
      private readonly ICollectiveSpec<JobModel> _archiveAll = null!;

      [CollectiveApplyFor]
      internal ICollectiveSpec<JobModel> EventOnly(ArchiveJobsCollectiveEvent e) => _archiveAll;

      [CollectiveApplyFor]
      internal ICollectiveSpec<JobModel> WithQuery(ArchiveJobsCollectiveEvent e, ICollectiveQuery query) =>
        query is null ? _archiveAll : _archiveAll;
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WithoutTheSuppressor_TheRulesFlagTheEventParameterAsync() {
    var findings = await _unusedParameterFindingsAsync(withSuppressor: false);

    await Assert.That(findings.Where(d => !d.IsSuppressed)).IsNotEmpty()
      .Because("the guard proves nothing unless the real rules fire on the fixture without the suppressor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WithTheSuppressor_NoUnusedParameterFindingRemainsAsync() {
    var findings = await _unusedParameterFindingsAsync(withSuppressor: true);

    await Assert.That(findings).IsNotEmpty();
    await Assert.That(findings.Where(d => !d.IsSuppressed).Select(d => d.ToString())).IsEmpty()
      .Because("a parameter the framework requires on a convention-bound method must not fail a consumer's gate");
  }

  [RequiresAssemblyFiles()]
  private static async Task<ImmutableArray<Diagnostic>> _unusedParameterFindingsAsync(bool withSuppressor) {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(CONSUMER);
    var analyzers = _load("Roslynator").AddRange(_load("Sonar"));
    if (withSuppressor) {
      analyzers = analyzers.Add(new ConventionBoundParameterSuppressor());
    }
    var options = new CompilationWithAnalyzersOptions(
      new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: false,
      logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
    var diagnostics = await compilation.WithAnalyzers(analyzers, options).GetAnalyzerDiagnosticsAsync();
    return [.. diagnostics.Where(d => _unusedParameterRules.Contains(d.Id))];
  }

  private static ImmutableArray<DiagnosticAnalyzer> _load(string package) {
    var directory = Path.Combine(AppContext.BaseDirectory, "ThirdPartyAnalyzers", package);
    var loader = new DirectoryLoader(directory);
    return [.. Directory.EnumerateFiles(directory, "*.dll")
      .SelectMany(path => new AnalyzerFileReference(path, loader).GetAnalyzers(LanguageNames.CSharp))];
  }

  /// <summary>Loads an analyzer package's assemblies side by side, sharing Roslyn with the test host.</summary>
  private sealed class DirectoryLoader(string directory) : IAnalyzerAssemblyLoader {
    private readonly AssemblyLoadContext _context = new Context(directory);

    public void AddDependencyLocation(string fullPath) { }

    public Assembly LoadFromPath(string fullPath) => _context.LoadFromAssemblyPath(fullPath);

    private sealed class Context(string directory) : AssemblyLoadContext(isCollectible: false) {
      protected override Assembly? Load(AssemblyName assemblyName) {
        var local = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
      }
    }
  }
}
