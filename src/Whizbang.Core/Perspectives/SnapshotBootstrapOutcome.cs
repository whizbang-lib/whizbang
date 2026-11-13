// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// What <see cref="IPerspectiveRunner.TryBootstrapSnapshotAsync"/> did (#2294).
/// </summary>
/// <docs>fundamentals/perspectives/snapshots#bootstrap</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/SnapshotBootstrapOutcomeTests.cs</tests>
public enum SnapshotBootstrapOutcome {
  /// <summary>A snapshot of the row was written at the requested event.</summary>
  Written = 0,

  /// <summary>
  /// The stream has no row to snapshot: the perspective purged it, it was evicted, or no event ever produced one.
  /// Nothing was written.
  /// </summary>
  RowMissing = 1,

  /// <summary>
  /// The snapshot was put off to a later bootstrap: the row's collectives moved while it was read, or its store
  /// could not say whose collectives the row holds. Nothing was written.
  /// </summary>
  Deferred = 2,

  /// <summary>
  /// The runner takes no snapshots: snapshots are disabled, there is no snapshot store, or the perspective keeps no
  /// rewind floor (it applies only ephemeral events and does not opt in). Nothing was written.
  /// </summary>
  SnapshotsOff = 3,

  /// <summary>
  /// The runner implements only <see cref="IPerspectiveRunner.BootstrapSnapshotAsync"/>, which says nothing about
  /// what it did. Whether a snapshot was written is unknown.
  /// </summary>
  NotReported = 4,
}
