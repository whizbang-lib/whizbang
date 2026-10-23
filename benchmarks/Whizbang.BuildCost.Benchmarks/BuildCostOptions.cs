// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>The command line (not yet implemented).</summary>
/// <docs>operations/performance/build-cost</docs>
public sealed record BuildCostOptions(int Runs, int Features, int OrdinaryFiles, string? ThresholdsPath, string? OutputPath) {
  /// <summary>Reads the command line.</summary>
  public static BuildCostOptions Parse(IReadOnlyList<string> args) => throw new NotImplementedException();
}
