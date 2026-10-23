// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>
/// The deterministic half of the benchmark: the median of a set of runs, each measurement against its threshold,
/// and the report. The timing itself lives in <see cref="BuildCostRunner"/>.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
/// <tests>tests/Whizbang.Generators.Tests/BuildCost/BuildCostBenchmarkTests.cs</tests>
public static class BuildCostEvaluator {
  /// <summary>The median of <paramref name="values"/>: the middle value, or the mean of the two middle values.</summary>
  public static double Median(IReadOnlyCollection<double> values) {
    if (values.Count == 0) {
      throw new ArgumentException("There is no median of no values.", nameof(values));
    }
    var sorted = values.Order().ToArray();
    var middle = sorted.Length / 2;
    return (sorted.Length % 2) == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
  }

  /// <summary>Compares each measurement with its threshold, in the order given.</summary>
  public static IReadOnlyList<BuildCostResult> Evaluate(IEnumerable<BuildCostMeasurement> measurements, BuildCostThresholds thresholds) =>
      [.. measurements.Select(m => _evaluate(m, _thresholdFor(m, thresholds)))];

  /// <summary>Whether any result is over its threshold.</summary>
  public static bool AnyFailed(IEnumerable<BuildCostResult> results) => results.Any(r => r.Status == BuildCostStatus.Fail);

  /// <summary>
  /// The report: a heading, what was measured, and one table row per result (name, median, threshold, status),
  /// as GitHub-flavored markdown for a job summary.
  /// </summary>
  public static string ToMarkdown(IReadOnlyList<BuildCostResult> results, string corpus, int runs) {
    var sb = new StringBuilder();
    sb.AppendLine("## Generator and analyzer timing");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"Median of {runs} runs after one warm-up, over {corpus}.");
    sb.AppendLine(AnyFailed(results)
        ? "**Over threshold:** at least one row is slower than its threshold."
        : "Every row is within its threshold.");
    sb.AppendLine();
    sb.AppendLine("| Name | Kind | Median (ms) | Threshold (ms) | Status |");
    sb.AppendLine("|---|---|---:|---:|---|");
    foreach (var r in results) {
      var threshold = r.ThresholdMs is { } t ? t.ToString("F0", CultureInfo.InvariantCulture) : "-";
      sb.AppendLine(CultureInfo.InvariantCulture,
          $"| {r.Measurement.Name} | {_kind(r.Measurement.Kind)} | {r.Measurement.MedianMs:F1} | {threshold} | {_status(r.Status)} |");
    }
    return sb.ToString();
  }

  private static double? _thresholdFor(BuildCostMeasurement m, BuildCostThresholds thresholds) => m.Kind switch {
    BuildCostKind.Generator => thresholds.Generators.TryGetValue(m.Name, out var g) ? g : null,
    BuildCostKind.AnalyzerAssembly => thresholds.AnalyzerAssemblies.TryGetValue(m.Name, out var a) ? a : null,
    BuildCostKind.GeneratorsTotal => thresholds.GeneratorsTotal,
    _ => thresholds.AnalyzersTotal,
  };

  private static BuildCostResult _evaluate(BuildCostMeasurement m, double? threshold) => threshold switch {
    null => new BuildCostResult(m, null, BuildCostStatus.NoThreshold),
    { } t when m.MedianMs > t => new BuildCostResult(m, t, BuildCostStatus.Fail),
    { } t => new BuildCostResult(m, t, BuildCostStatus.Pass),
  };

  private static string _kind(BuildCostKind kind) => kind switch {
    BuildCostKind.Generator => "generator",
    BuildCostKind.AnalyzerAssembly => "analyzers",
    _ => "total",
  };

  private static string _status(BuildCostStatus status) => status switch {
    BuildCostStatus.Pass => "pass",
    BuildCostStatus.Fail => "**over**",
    _ => "no threshold",
  };
}
