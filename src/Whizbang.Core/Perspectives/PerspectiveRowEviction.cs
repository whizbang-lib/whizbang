// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Whether row retention can evict a perspective model's rows: a row TTL, a row cap, or membership of a
/// stream group (whose cascade evicts a member's rows when an announcing member evicts the same stream).
/// </summary>
/// <remarks>
/// The generated runner asks this on a missing row. For a model whose rows can be evicted, a missing row
/// on a stream that already holds events this perspective folds is an evicted row, not a new stream, and
/// the runner rebuilds it from the latest snapshot plus the history after it (issue #1483). A model that
/// nothing can evict never pays for that probe. All three answers come from registries populated by
/// generated module initializers, so the check is reflection-free.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention#what-an-eviction-removes</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowEvictionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/EvictedRowRebuildTests.cs</tests>
public static class PerspectiveRowEviction {
  /// <summary>
  /// True when <paramref name="modelType"/>'s rows can be evicted by row retention: it resolves a row TTL
  /// (<see cref="PerspectiveTtlRegistry"/>), declares a row cap (<see cref="PerspectiveRowCapRegistry"/>),
  /// or belongs to a stream group (<see cref="PerspectiveStreamGroupRegistry"/>).
  /// </summary>
  /// <param name="modelType">The perspective's read-model CLR type.</param>
  public static bool CanEvict(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);
    return PerspectiveTtlRegistry.ResolveSeconds(modelType) >= 0
      || PerspectiveRowCapRegistry.Resolve(modelType) is not null
      || PerspectiveStreamGroupRegistry.Resolve(modelType).Count > 0;
  }
}
