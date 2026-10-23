// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>The command line around <see cref="BuildCostRunner"/> and <see cref="BuildCostEvaluator"/>.</summary>
/// <docs>operations/performance/build-cost</docs>
internal static class BuildCostProgram {
  private static readonly string[] _generatorPackages = [
    "Whizbang.Generators",
    "Whizbang.Data.EFCore.Postgres.Generators",
    "Whizbang.Sagas.Generators",
    "Whizbang.Transports.FastEndpoints.Generators",
    "Whizbang.Transports.HotChocolate.Generators",
  ];

  [RequiresAssemblyFiles]
  public static async Task<int> RunAsync(string[] args) {
    var options = BuildCostOptions.Parse(args);
    var root = _repositoryRoot();
    var configuration = typeof(BuildCostProgram).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
    var paths = _generatorPackages
        .Select(p => Path.Combine(root, "src", p, "bin", configuration, "netstandard2.0", p + ".dll"))
        .ToList();
    var missing = paths.Where(p => !File.Exists(p)).ToList();
    if (missing.Count > 0) {
      await Console.Error.WriteLineAsync("Build the generator packages first; not found: " + string.Join(", ", missing));
      return 2;
    }

    var files = BuildCostCorpus.Build(options.Features, options.OrdinaryFiles);
    var corpus = string.Create(CultureInfo.InvariantCulture,
        $"{files.Count} files ({options.Features} features of messages, receptors and perspectives, and {options.OrdinaryFiles} files of ordinary code)");
    Console.WriteLine($"Timing {paths.Count} generator packages over {corpus}, {options.Runs} runs");

    var measurements = await new BuildCostRunner(paths).RunAsync(files, options.Runs, CancellationToken.None);
    var thresholds = BuildCostThresholds.Parse(await File.ReadAllTextAsync(options.ThresholdsPath ?? Path.Combine(AppContext.BaseDirectory, "build-cost-thresholds.json")));
    var results = BuildCostEvaluator.Evaluate(measurements, thresholds);
    var report = BuildCostEvaluator.ToMarkdown(results, corpus, options.Runs);

    var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (string.IsNullOrEmpty(summary)) {
      Console.WriteLine(report);
    } else {
      await File.AppendAllTextAsync(summary, report);
    }
    if (options.OutputPath is { } output) {
      await File.WriteAllTextAsync(output, report);
    }
    return BuildCostEvaluator.AnyFailed(results) ? 1 : 0;
  }

  private static string _repositoryRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Whizbang.Generators"))) {
      dir = Path.GetDirectoryName(dir);
    }
    return dir ?? throw new InvalidOperationException("Run the benchmark from a build inside the repository.");
  }
}
