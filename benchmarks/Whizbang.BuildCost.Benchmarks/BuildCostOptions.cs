// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>The benchmark's command line.</summary>
/// <param name="Runs">Timed runs after the warm-up. Default 5.</param>
/// <param name="Features">Features of messages, receptors and perspectives in the corpus. Default 1,000.</param>
/// <param name="OrdinaryFiles">Files of ordinary code in the corpus. Default 3,000.</param>
/// <param name="ThresholdsPath">The thresholds file, or null for the copy beside the tool.</param>
/// <param name="OutputPath">Where to write the report as well, or null.</param>
/// <docs>operations/performance/build-cost</docs>
/// <tests>tests/Whizbang.Generators.Tests/BuildCost/BuildCostBenchmarkTests.cs</tests>
public sealed record BuildCostOptions(int Runs, int Features, int OrdinaryFiles, string? ThresholdsPath, string? OutputPath) {
  /// <summary>Reads <c>--runs</c>, <c>--features</c>, <c>--ordinary-files</c>, <c>--thresholds</c> and <c>--output</c>.</summary>
  public static BuildCostOptions Parse(IReadOnlyList<string> args) {
    var options = new BuildCostOptions(5, 1000, 3000, null, null);
    var i = 0;
    while (i < args.Count) {
      var name = args[i];
      var value = i + 1 < args.Count ? args[i + 1] : throw new ArgumentException($"{name} needs a value.", nameof(args));
      options = name switch {
        "--runs" => options with { Runs = _positive(name, value) },
        "--features" => options with { Features = _positive(name, value) },
        "--ordinary-files" => options with { OrdinaryFiles = _positive(name, value) },
        "--thresholds" => options with { ThresholdsPath = value },
        "--output" => options with { OutputPath = value },
        _ => throw new ArgumentException($"Unknown option {name}.", nameof(args)),
      };
      i += 2;
    }
    return options;
  }

  private static int _positive(string name, string value) =>
      int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
          ? n
          : throw new ArgumentException($"{name} needs a positive whole number, not '{value}'.", nameof(value));
}
