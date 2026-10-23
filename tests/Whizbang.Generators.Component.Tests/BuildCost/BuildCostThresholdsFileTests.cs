// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.BuildCost.Benchmarks;

namespace Whizbang.Generators.Tests.BuildCost;

/// <summary>
/// The timing benchmark's checked-in thresholds (#1651) name every core generator, so a regression in one is
/// reported by name rather than hidden in the total. Reads the file the tool ships beside itself.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("BuildCost")]
public class BuildCostThresholdsFileTests {
  [Test]
  public async Task Thresholds_CheckedInFile_CoversEveryGeneratorAndAnalyzerPackageAsync() {
    var path = Path.Combine(AppContext.BaseDirectory, "build-cost-thresholds.json");
    var thresholds = BuildCostThresholds.Parse(await File.ReadAllTextAsync(path));
    var generators = typeof(ReceptorDiscoveryGenerator).Assembly.GetTypes()
        .Where(t => t.GetCustomAttributes(typeof(Microsoft.CodeAnalysis.GeneratorAttribute), false).Length > 0)
        .Select(t => t.Name)
        .ToList();

    await Assert.That(generators.Where(g => !thresholds.Generators.ContainsKey(g))).IsEmpty()
        .Because("every core generator needs a threshold of its own, so a regression in one is named, not hidden in the total");
    await Assert.That(thresholds.AnalyzerAssemblies.Keys).Contains("Whizbang.Generators");
    await Assert.That(thresholds.GeneratorsTotal).IsGreaterThan(0);
    await Assert.That(thresholds.AnalyzersTotal).IsGreaterThan(0);
  }
}
