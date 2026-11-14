// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// How much of the historical metadata conversion one maintenance cycle does, under
/// <c>Whizbang:Workers:Maintenance:MetadataHistoryConversion</c> (#2322). The conversion rewrites event rows written with
/// full metadata before slim stored metadata existed, a batch per transaction; a cycle runs batches until the table is
/// read, <see cref="MaxBatchesPerCycle"/> batches have run, or <see cref="TimeBudget"/> has passed, whichever comes first,
/// so a large history drains in hours rather than days while no cycle holds maintenance for longer than the budget.
/// </summary>
/// <docs>data/event-store#history-conversion</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/MetadataHistoryConversionOptionsTests.cs</tests>
public sealed class MetadataHistoryConversionOptions {
  /// <summary>Event rows one batch reads, each batch its own transaction. Default 1000; at least 1.</summary>
  public int BatchSize { get; set; } = 1000;

  /// <summary>Batches one cycle runs at most. Default 200 (200,000 rows at the default batch); at least 1.</summary>
  public int MaxBatchesPerCycle { get; set; } = 200;

  /// <summary>
  /// How long one cycle keeps starting batches: none starts once this has passed since the cycle's first. Default
  /// 30 seconds; greater than zero. A batch in progress is never cut short.
  /// </summary>
  public TimeSpan TimeBudget { get; set; } = TimeSpan.FromSeconds(30);
}
