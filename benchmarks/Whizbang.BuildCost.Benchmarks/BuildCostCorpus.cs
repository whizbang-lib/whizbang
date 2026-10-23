// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>
/// The benchmark's consumer: <see cref="ConsumerCorpus"/> features (a command, an event, a receptor, a read model
/// and a perspective each) plus <see cref="OrdinaryCodeCorpus"/> files of code no generator cares about, so that a
/// cost growing with the size of the codebase rather than with its use of Whizbang shows up (#1649 did not show
/// on the small sample).
/// </summary>
/// <docs>operations/performance/build-cost</docs>
/// <tests>tests/Whizbang.Generators.Tests/BuildCost/BuildCostBenchmarkTests.cs</tests>
public static class BuildCostCorpus {
  /// <summary>Builds the corpus as (path, source) pairs.</summary>
  public static IReadOnlyList<(string Path, string Source)> Build(int features, int ordinaryFiles) =>
      [.. ConsumerCorpus.Build(features), .. OrdinaryCodeCorpus.Build(ordinaryFiles)];
}
