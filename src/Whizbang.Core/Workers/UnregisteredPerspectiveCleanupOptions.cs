// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// #2601: the maintenance step that removes what is left of a perspective no service runs any more: its cursors, its
/// queued rows and its snapshots. A perspective counts as no longer run when the running service does not register it
/// and no service's association rows name it (each service writes its perspectives' rows at its startup and deletes the
/// rest), so a perspective another service, or a newer instance of this one, runs is never touched. Bound from
/// <c>Whizbang:Workers:Perspective:UnregisteredPerspectiveCleanup</c>.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#perspectives-no-longer-registered</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs</tests>
public sealed class UnregisteredPerspectiveCleanupOptions {
  /// <summary>The default <see cref="BatchCursors"/>.</summary>
  public const int DEFAULT_BATCH_CURSORS = 1000;

  /// <summary>The default <see cref="MaxBatchesPerCycle"/>.</summary>
  public const int DEFAULT_MAX_BATCHES_PER_CYCLE = 10;

  /// <summary>Whether the step runs. Default <c>true</c>; <c>false</c> keeps every leftover cursor.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>The most cursors one batch removes, with their queued rows and snapshots. One batch is one statement.</summary>
  public int BatchCursors { get; set; } = DEFAULT_BATCH_CURSORS;

  /// <summary>The most batches one maintenance cycle runs.</summary>
  public int MaxBatchesPerCycle { get; set; } = DEFAULT_MAX_BATCHES_PER_CYCLE;
}
