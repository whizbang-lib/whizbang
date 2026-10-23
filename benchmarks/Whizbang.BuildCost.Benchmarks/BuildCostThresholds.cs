// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>
/// The most each generator, each analyzer assembly and each total may take over the benchmark corpus, in
/// milliseconds (median of the timed runs), read from <c>build-cost-thresholds.json</c>.
/// </summary>
/// <param name="Generators">Per generator, by type name.</param>
/// <param name="AnalyzerAssemblies">Per analyzer assembly, by assembly name: the sum of its analyzers.</param>
/// <param name="GeneratorsTotal">All generators together.</param>
/// <param name="AnalyzersTotal">All analyzers together.</param>
/// <docs>operations/performance/build-cost</docs>
/// <tests>tests/Whizbang.Generators.Tests/BuildCost/BuildCostBenchmarkTests.cs</tests>
public sealed record BuildCostThresholds(
    IReadOnlyDictionary<string, double> Generators,
    IReadOnlyDictionary<string, double> AnalyzerAssemblies,
    double GeneratorsTotal,
    double AnalyzersTotal) {

  /// <summary>Reads the thresholds file. Every key it names is required; the maps may be empty.</summary>
  public static BuildCostThresholds Parse(string json) {
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    return new BuildCostThresholds(
        _map(root.GetProperty("generators")),
        _map(root.GetProperty("analyzerAssemblies")),
        root.GetProperty("generatorsTotalMs").GetDouble(),
        root.GetProperty("analyzersTotalMs").GetDouble());
  }

  private static Dictionary<string, double> _map(JsonElement element) =>
      element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
}
