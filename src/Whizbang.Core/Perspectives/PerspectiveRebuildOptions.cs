// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Settings of every perspective rebuild, whatever its mode, bound from <c>Whizbang:Perspectives:Rebuild</c>.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#batched-reads</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveRebuilderBatchedReadTests.cs:PerspectiveRebuildOptions_ReadsAHundredStreamsPerBatch_ByDefaultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ProcessWideOptionsBindingTests.cs:PerspectiveRebuildOptions_EveryKeyBindsAsync</tests>
public class PerspectiveRebuildOptions {
  /// <summary>
  /// How many streams a rebuild reads the events of with one read (#2513). The streams are read a batch at a time, each
  /// stream's events in its own order, and folded one by one from that read. 1 reads each stream on its own, as a
  /// rebuild did before; a value below 1 is taken as 1. Default 100.
  /// </summary>
  /// <docs>fundamentals/perspectives/rebuild#batched-reads</docs>
  public int StreamReadBatchSize { get; set; } = 100;
}
