// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Where a replay of one row starts, for the collectives it folds (#1956): a rewind, or a rebuild that starts from a
/// stream's newest snapshot. A replay from a snapshot that records its collectives' mark (#1963) folds the collectives
/// stamped past that mark, plus every unstamped one; a replay from a snapshot written before the mark existed folds the
/// collectives committed after the snapshot's event. A replay from the stream's start names no event and folds every
/// collective, as a full rebuild does.
/// </summary>
/// <param name="TenantId">The tenant whose collectives fold into the row; null for a row whose events carry no tenant.</param>
/// <param name="AfterEventId">The event the replay starts after (the snapshot's), or null for a replay from the stream's start.</param>
/// <param name="AfterCommitSequence">That event's commit sequence, or null when it carries none.</param>
/// <docs>fundamentals/messaging/collective-events#collective-replay-from-a-start</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AReplayFromTheStart_FollowsEveryCollectiveAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_ComparesCommitSequences_WhenBothCarryOneAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_ComparesMessageIds_WhenTheCollectiveCarriesNoCommitSequenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_ComparesMessageIds_WhenItCarriesNoCommitSequenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_RefusesANullCollectiveAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_FromASnapshotThatRecordsItsCollectives_FollowsTheStampedCollectivesAboveTheMark_OnlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_FromASnapshotThatRecordsItsCollectives_FollowsAnUnstampedCollective_WhateverItsIdAsync</tests>
public sealed record CollectiveReplayStart(string? TenantId, Guid? AfterEventId = null, long? AfterCommitSequence = null) {
  /// <summary>
  /// The commit sequence through which every collective of the row was complete (applied in full) when the snapshot the
  /// replay starts from was taken (#1963), or null for a snapshot that records none (one taken before the mark existed),
  /// which keeps the snapshot-event rule. A collective in flight when the snapshot was taken counts as after the mark,
  /// whatever its position against the snapshot's event, so the replay folds it.
  /// </summary>
  /// <docs>fundamentals/perspectives/snapshots#collectives</docs>
  public long? CollectivesThroughSequence { get; init; }

  /// <summary>
  /// Whether <paramref name="collective"/> was committed after the replay's start, so the replay folds it: always for a
  /// replay from the stream's start; past the snapshot's <see cref="CollectivesThroughSequence"/> when it records one (a
  /// collective still unstamped is past it, because every collective the snapshot holds was stamped when it was taken);
  /// otherwise by commit sequence when both carry one, and by message id when either does not, the order a replay merges
  /// collectives among a stream's events in.
  /// </summary>
  /// <param name="collective">A collective the replay may fold.</param>
  /// <returns>True when the replay folds it.</returns>
  public bool Follows(MessageEnvelope<IEvent> collective) {
    ArgumentNullException.ThrowIfNull(collective);
    if (CollectivesThroughSequence is long through) {
      return collective.LocalCommitSequence is not long stamped || stamped > through;
    }
    if (AfterEventId is not Guid after) {
      return true;
    }
    return collective.LocalCommitSequence is long sequence && AfterCommitSequence is long start
      ? sequence > start
      : collective.MessageId.Value.CompareTo(after) > 0;
  }
}
