// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>The thresholds (not yet implemented).</summary>
/// <docs>operations/performance/build-cost</docs>
public sealed record BuildCostThresholds(
    IReadOnlyDictionary<string, double> Generators,
    IReadOnlyDictionary<string, double> AnalyzerAssemblies,
    double GeneratorsTotal,
    double AnalyzersTotal) {
  /// <summary>Reads the thresholds file.</summary>
  public static BuildCostThresholds Parse(string json) => throw new NotImplementedException();
}
