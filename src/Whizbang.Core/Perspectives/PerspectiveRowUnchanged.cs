// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Why a run that folded events left its row untouched (#2252). A rebuild reads it to tell a read model that was already
/// right from a write the store refused and from a fold that produced no model.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#reading-the-result</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRebuilderUnchangedReasonTests.cs:RebuildStreamsAsync_AMixedRun_SeparatesRewrittenAlreadyCorrectRefusedNoModelAndNoEventsAsync</tests>
public enum PerspectiveRowUnchangedReason {
  /// <summary>The run did not say: it wrote the row, folded nothing, or predates the reason.</summary>
  Unreported = 0,

  /// <summary>Every event declined to produce a model, so there was nothing to write.</summary>
  NoModel = 1,

  /// <summary>The row's ordering guard refused the write, and the row differs from what the run folded.</summary>
  WriteRefused = 2,

  /// <summary>The write was refused, but the row already holds exactly what the run folded: nothing needed writing.</summary>
  AlreadyCorrect = 3,
}

/// <summary>
/// Reads a refused rebuild write (#2252): the fold compared with the stored row, as documents written by the same
/// serializer, so equal documents mean the read model was already right.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#reading-the-result</docs>
public static class PerspectiveRowUnchanged {
  /// <summary>
  /// <see cref="PerspectiveRowUnchangedReason.AlreadyCorrect"/> when <paramref name="folded"/> and
  /// <paramref name="stored"/> are equal JSON (property order aside); otherwise
  /// <see cref="PerspectiveRowUnchangedReason.WriteRefused"/>, including when either is missing.
  /// </summary>
  /// <param name="folded">The model the run folded, serialized before the write.</param>
  /// <param name="stored">The row the store holds, serialized the same way; null when there is none.</param>
  /// <returns>Why the row was left untouched.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowUnchangedTests.cs:OfRefusedWrite_AFoldEqualToTheStoredRow_IsAlreadyCorrectAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowUnchangedTests.cs:OfRefusedWrite_AFoldThatDiffersFromTheStoredRow_IsAWriteRefusedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowUnchangedTests.cs:OfRefusedWrite_WithNoStoredRow_IsAWriteRefusedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowUnchangedTests.cs:OfRefusedWrite_WithNoFold_IsAWriteRefusedAsync</tests>
  public static PerspectiveRowUnchangedReason OfRefusedWrite(JsonDocument? folded, JsonDocument? stored) =>
    folded is not null && stored is not null && JsonElement.DeepEquals(folded.RootElement, stored.RootElement)
      ? PerspectiveRowUnchangedReason.AlreadyCorrect
      : PerspectiveRowUnchangedReason.WriteRefused;
}
