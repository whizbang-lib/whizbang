// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>The deterministic half of the benchmark (not yet implemented).</summary>
/// <docs>operations/performance/build-cost</docs>
public static class BuildCostEvaluator {
  /// <summary>The median.</summary>
  public static double Median(IReadOnlyCollection<double> values) => throw new NotImplementedException();

  /// <summary>Each measurement against its threshold.</summary>
  public static IReadOnlyList<BuildCostResult> Evaluate(IEnumerable<BuildCostMeasurement> measurements, BuildCostThresholds thresholds) =>
      throw new NotImplementedException();

  /// <summary>Whether any result failed.</summary>
  public static bool AnyFailed(IEnumerable<BuildCostResult> results) => throw new NotImplementedException();

  /// <summary>The report.</summary>
  public static string ToMarkdown(IReadOnlyList<BuildCostResult> results, string corpus, int runs) => throw new NotImplementedException();
}
