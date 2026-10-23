// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.BuildCost.Benchmarks;

namespace Whizbang.Generators.Tests.BuildCost;

/// <summary>
/// The deterministic half of the generator and analyzer timing benchmark (#1651): the median, each measurement
/// against its threshold, the report, the command line, the thresholds format, and the corpus's shape. The timing
/// itself is not tested here (a timing test is not deterministic); the advisory CI job runs it.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("BuildCost")]
public class BuildCostBenchmarkTests {
  private static readonly BuildCostThresholds _thresholds = new(
      new Dictionary<string, double> { ["FastGenerator"] = 100, ["SlowGenerator"] = 100 },
      new Dictionary<string, double> { ["Analyzers.A"] = 50 },
      GeneratorsTotal: 500,
      AnalyzersTotal: 80);

  [Test]
  [Arguments(new[] { 3.0 }, 3.0)]
  [Arguments(new[] { 5.0, 1.0, 3.0 }, 3.0)]
  [Arguments(new[] { 4.0, 1.0, 3.0, 2.0 }, 2.5)]
  public async Task Median_IsTheMiddleValueOrTheMeanOfTheTwoMiddleValuesAsync(double[] values, double expected) {
    await Assert.That(BuildCostEvaluator.Median(values)).IsEqualTo(expected);
  }

  [Test]
  public async Task Median_OfNoValues_ThrowsAsync() {
    await Assert.That(() => BuildCostEvaluator.Median([])).Throws<ArgumentException>();
  }

  [Test]
  public async Task Evaluate_ComparesEachKindWithItsOwnThresholdAsync() {
    var results = BuildCostEvaluator.Evaluate([
      new BuildCostMeasurement(BuildCostKind.Generator, "FastGenerator", 10),
      new BuildCostMeasurement(BuildCostKind.Generator, "SlowGenerator", 100.5),
      new BuildCostMeasurement(BuildCostKind.Generator, "NewGenerator", 1),
      new BuildCostMeasurement(BuildCostKind.AnalyzerAssembly, "Analyzers.A", 50),
      new BuildCostMeasurement(BuildCostKind.AnalyzerAssembly, "Analyzers.B", 1),
      new BuildCostMeasurement(BuildCostKind.GeneratorsTotal, "All generators", 501),
      new BuildCostMeasurement(BuildCostKind.AnalyzersTotal, "All analyzers", 79),
    ], _thresholds);

    await Assert.That(string.Join(",", results.Select(r => r.Status)))
        .IsEqualTo("Pass,Fail,NoThreshold,Pass,NoThreshold,Fail,Pass");
    await Assert.That(string.Join(",", results.Select(r => r.ThresholdMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")))
        .IsEqualTo("100,100,-,50,-,500,80");
    await Assert.That(BuildCostEvaluator.AnyFailed(results)).IsTrue();
  }

  [Test]
  public async Task AnyFailed_NoRowOverItsThreshold_IsFalseAsync() {
    var results = BuildCostEvaluator.Evaluate([
      new BuildCostMeasurement(BuildCostKind.Generator, "FastGenerator", 100),
      new BuildCostMeasurement(BuildCostKind.Generator, "Unlisted", 1_000),
    ], _thresholds);

    await Assert.That(BuildCostEvaluator.AnyFailed(results)).IsFalse()
        .Because("a generator with no threshold of its own is checked only through the total");
  }

  [Test]
  public async Task ToMarkdown_WritesOneRowPerResultWithMedianThresholdAndStatusAsync() {
    var results = BuildCostEvaluator.Evaluate([
      new BuildCostMeasurement(BuildCostKind.Generator, "SlowGenerator", 123.456),
      new BuildCostMeasurement(BuildCostKind.AnalyzerAssembly, "Analyzers.B", 7),
      new BuildCostMeasurement(BuildCostKind.AnalyzersTotal, "All analyzers", 7),
    ], _thresholds);

    var report = BuildCostEvaluator.ToMarkdown(results, "3 files", runs: 5);

    await Assert.That(report).Contains("Median of 5 runs after one warm-up, over 3 files.");
    await Assert.That(report).Contains("**Over threshold:**");
    await Assert.That(report).Contains("| Name | Kind | Median (ms) | Threshold (ms) | Status |");
    await Assert.That(report).Contains("| SlowGenerator | generator | 123.5 | 100 | **over** |");
    await Assert.That(report).Contains("| Analyzers.B | analyzers | 7.0 | - | no threshold |");
    await Assert.That(report).Contains("| All analyzers | total | 7.0 | 80 | pass |");
  }

  [Test]
  public async Task ToMarkdown_EveryRowWithinItsThreshold_SaysSoAsync() {
    var results = BuildCostEvaluator.Evaluate([new BuildCostMeasurement(BuildCostKind.GeneratorsTotal, "All generators", 1)], _thresholds);

    var report = BuildCostEvaluator.ToMarkdown(results, "1 file", runs: 1);

    await Assert.That(report).Contains("Every row is within its threshold.");
    await Assert.That(report).DoesNotContain("Over threshold");
  }

  [Test]
  public async Task Thresholds_Parse_ReadsEveryMapAndTotalAsync() {
    var thresholds = BuildCostThresholds.Parse("""
      { "note": "ignored", "generatorsTotalMs": 900, "analyzersTotalMs": 400.5,
        "generators": { "ReceptorDiscoveryGenerator": 120 }, "analyzerAssemblies": {} }
      """);

    await Assert.That(thresholds.GeneratorsTotal).IsEqualTo(900);
    await Assert.That(thresholds.AnalyzersTotal).IsEqualTo(400.5);
    await Assert.That(thresholds.Generators["ReceptorDiscoveryGenerator"]).IsEqualTo(120);
    await Assert.That(thresholds.AnalyzerAssemblies).IsEmpty();
  }

  [Test]
  public async Task Options_Defaults_AreFiveRunsOverAThousandFeaturesAndThreeThousandFilesAsync() {
    var options = BuildCostOptions.Parse([]);

    await Assert.That(options).IsEqualTo(new BuildCostOptions(5, 1000, 3000, null, null));
  }

  [Test]
  public async Task Options_Parse_ReadsEveryOptionAsync() {
    var options = BuildCostOptions.Parse(["--runs", "3", "--features", "10", "--ordinary-files", "20", "--thresholds", "t.json", "--output", "r.md"]);

    await Assert.That(options).IsEqualTo(new BuildCostOptions(3, 10, 20, "t.json", "r.md"));
  }

  [Test]
  [Arguments("--runs", "0")]
  [Arguments("--features", "-1")]
  [Arguments("--ordinary-files", "many")]
  [Arguments("--no-such-option", "1")]
  public async Task Options_Parse_RejectsABadValueOrAnUnknownOptionAsync(string name, string value) {
    await Assert.That(() => BuildCostOptions.Parse([name, value])).Throws<ArgumentException>();
  }

  [Test]
  public async Task Options_Parse_RejectsAnOptionWithNoValueAsync() {
    await Assert.That(() => BuildCostOptions.Parse(["--runs"])).Throws<ArgumentException>();
  }

  [Test]
  public async Task Corpus_HasEachFeatureAndEachOrdinaryFileAsync() {
    var files = BuildCostCorpus.Build(features: 4, ordinaryFiles: 6);

    await Assert.That(files.Count).IsEqualTo((4 * 3) + 2 + 6);
    await Assert.That(files.Count(f => f.Path.EndsWith("/Receptors.cs", StringComparison.Ordinal))).IsEqualTo(4);
    await Assert.That(files.Count(f => f.Path.StartsWith("Ordinary/", StringComparison.Ordinal))).IsEqualTo(6);
    await Assert.That(files.Select(f => f.Path).Distinct().Count()).IsEqualTo(files.Count);
  }
}
