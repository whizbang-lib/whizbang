// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>What was timed: one generator, one analyzer assembly, or a total.</summary>
/// <docs>operations/performance/build-cost</docs>
public enum BuildCostKind {
  /// <summary>One incremental generator.</summary>
  Generator,

  /// <summary>Every analyzer in one assembly, summed.</summary>
  AnalyzerAssembly,

  /// <summary>All generators together.</summary>
  GeneratorsTotal,

  /// <summary>All analyzers together.</summary>
  AnalyzersTotal,
}

/// <summary>How a measurement compares with its threshold.</summary>
/// <docs>operations/performance/build-cost</docs>
public enum BuildCostStatus {
  /// <summary>At or under its threshold.</summary>
  Pass,

  /// <summary>Over its threshold.</summary>
  Fail,

  /// <summary>Measured, but the thresholds file has no entry for it, so it is checked only through the total.</summary>
  NoThreshold,
}

/// <summary>One median timing.</summary>
/// <param name="Kind">What was timed.</param>
/// <param name="Name">The generator type name, the analyzer assembly name, or the total's label.</param>
/// <param name="MedianMs">The median of the timed runs, in milliseconds.</param>
/// <docs>operations/performance/build-cost</docs>
public sealed record BuildCostMeasurement(BuildCostKind Kind, string Name, double MedianMs);

/// <summary>One measurement against its threshold.</summary>
/// <param name="Measurement">The timing.</param>
/// <param name="ThresholdMs">The threshold, or null when there is none.</param>
/// <param name="Status">How the two compare.</param>
/// <docs>operations/performance/build-cost</docs>
public sealed record BuildCostResult(BuildCostMeasurement Measurement, double? ThresholdMs, BuildCostStatus Status);
