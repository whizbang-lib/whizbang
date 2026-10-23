// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>
/// Times the generators and analyzers in the given assemblies over a corpus: each generator with a fresh
/// <see cref="GeneratorDriver"/> per run (<see cref="GeneratorDriver.GetTimingInfo"/>), and each analyzer through
/// <see cref="CompilationWithAnalyzers"/> with execution time logged
/// (<see cref="CompilationWithAnalyzers.GetAnalyzerTelemetryInfoAsync"/>) over the compilation the generators
/// produced, as a build does. One warm-up run is discarded; the result is the median of the timed runs.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
public sealed class BuildCostRunner(IReadOnlyList<string> analyzerAssemblyPaths) {
  /// <summary>Runs the benchmark and returns the median timings, generators first, then analyzers, then the totals.</summary>
  [RequiresAssemblyFiles]
  public async Task<IReadOnlyList<BuildCostMeasurement>> RunAsync(IReadOnlyList<(string Path, string Source)> files, int runs, CancellationToken ct) {
    var references = _load();
    var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, parse, f.Path, cancellationToken: ct)).ToArray();
    var compilation = CSharpCompilation.Create("Consumer", trees, _metadataReferences(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    var generators = references.SelectMany(r => r.Reference.GetGenerators(LanguageNames.CSharp)).ToImmutableArray();
    var analyzers = references.SelectMany(r => r.Reference.GetAnalyzers(LanguageNames.CSharp)).ToImmutableArray();
    var analyzerAssembly = analyzers.ToDictionary(a => a, a => a.GetType().Assembly.GetName().Name ?? "?");

    var generatorTimes = new Dictionary<string, List<double>>(StringComparer.Ordinal);
    var analyzerTimes = new Dictionary<string, List<double>>(StringComparer.Ordinal);
    var generatorTotals = new List<double>();
    var analyzerTotals = new List<double>();

    for (var run = 0; run <= runs; run++) {
      GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parse);
      driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var withGenerated, out _, ct);
      var timing = driver.GetTimingInfo();

      var withAnalyzers = withGenerated.WithAnalyzers(analyzers,
          new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
              concurrentAnalysis: true, logAnalyzerExecutionTime: true));
      await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);
      var perAssembly = new Dictionary<string, double>(StringComparer.Ordinal);
      foreach (var analyzer in analyzers) {
        var telemetry = await withAnalyzers.GetAnalyzerTelemetryInfoAsync(analyzer, ct);
        var name = analyzerAssembly[analyzer];
        perAssembly[name] = perAssembly.GetValueOrDefault(name) + telemetry.ExecutionTime.TotalMilliseconds;
      }

      if (run == 0) {
        continue; // the warm-up: JIT and first-load costs a consumer's long-lived compiler server does not pay
      }
      foreach (var g in timing.GeneratorTimes) {
        _add(generatorTimes, g.Generator.GetGeneratorType().Name, g.ElapsedTime.TotalMilliseconds);
      }
      foreach (var (name, ms) in perAssembly) {
        _add(analyzerTimes, name, ms);
      }
      generatorTotals.Add(timing.ElapsedTime.TotalMilliseconds);
      analyzerTotals.Add(perAssembly.Values.Sum());
    }

    return [
      .. generatorTimes.OrderByDescending(p => BuildCostEvaluator.Median(p.Value))
          .Select(p => new BuildCostMeasurement(BuildCostKind.Generator, p.Key, BuildCostEvaluator.Median(p.Value))),
      .. analyzerTimes.OrderBy(p => p.Key, StringComparer.Ordinal)
          .Select(p => new BuildCostMeasurement(BuildCostKind.AnalyzerAssembly, p.Key, BuildCostEvaluator.Median(p.Value))),
      new BuildCostMeasurement(BuildCostKind.GeneratorsTotal, "All generators", BuildCostEvaluator.Median(generatorTotals)),
      new BuildCostMeasurement(BuildCostKind.AnalyzersTotal, "All analyzers", BuildCostEvaluator.Median(analyzerTotals)),
    ];
  }

  private static void _add(Dictionary<string, List<double>> times, string name, double ms) {
    if (!times.TryGetValue(name, out var list)) {
      times[name] = list = [];
    }
    list.Add(ms);
  }

  private List<(string Path, AnalyzerFileReference Reference)> _load() {
    var loader = new DefaultContextLoader();
    return [.. analyzerAssemblyPaths.Select(p => (p, new AnalyzerFileReference(Path.GetFullPath(p), loader)))];
  }

  [RequiresAssemblyFiles]
  private static ImmutableArray<MetadataReference> _metadataReferences() {
    var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
    return [
      .. platform.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
      MetadataReference.CreateFromFile(typeof(Whizbang.Core.IEvent).Assembly.Location),
    ];
  }

  // Loads each analyzer assembly once into the default context, beside the compiler the tool already runs on, so
  // the generators bind to the same Microsoft.CodeAnalysis as the driver.
  private sealed class DefaultContextLoader : IAnalyzerAssemblyLoader {
    public void AddDependencyLocation(string fullPath) {
      // Dependencies resolve from the default context: the generator packages merge their own.
    }

    public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
  }
}
