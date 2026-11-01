// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Where a replay of one row starts, for the collectives it folds (#1956): a rewind, or a rebuild that starts from a
/// stream's newest snapshot. A replay from a snapshot starts after the event the snapshot was taken at, and the snapshot
/// already holds every collective committed before that event, so the replay folds only the collectives committed after
/// it. A replay from the stream's start names no event and folds every collective, as a full rebuild does.
/// </summary>
/// <param name="TenantId">The tenant whose collectives fold into the row; null for a row whose events carry no tenant.</param>
/// <param name="AfterEventId">The event the replay starts after (the snapshot's), or null for a replay from the stream's start.</param>
/// <param name="AfterCommitSequence">That event's commit sequence, or null when it carries none.</param>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AReplayFromTheStart_FollowsEveryCollectiveAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_ComparesCommitSequences_WhenBothCarryOneAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_ComparesMessageIds_WhenTheCollectiveCarriesNoCommitSequenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_ComparesMessageIds_WhenItCarriesNoCommitSequenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AStart_RefusesANullCollectiveAsync</tests>
public sealed record CollectiveReplayStart(string? TenantId, Guid? AfterEventId = null, long? AfterCommitSequence = null) {
  /// <summary>
  /// Whether <paramref name="collective"/> was committed after the replay's start, so the replay folds it: always for a
  /// replay from the stream's start; otherwise by commit sequence when both carry one, and by message id when either
  /// does not, the order a replay merges collectives among a stream's events in.
  /// </summary>
  /// <param name="collective">A collective the replay may fold.</param>
  /// <returns>True when the replay folds it.</returns>
  public bool Follows(MessageEnvelope<IEvent> collective) {
    ArgumentNullException.ThrowIfNull(collective);
    if (AfterEventId is not Guid after) {
      return true;
    }
    return collective.LocalCommitSequence is long sequence && AfterCommitSequence is long start
      ? sequence > start
      : collective.MessageId.Value.CompareTo(after) > 0;
  }
}
