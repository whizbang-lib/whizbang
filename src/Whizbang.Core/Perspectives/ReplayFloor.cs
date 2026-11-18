// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Whether a snapshot can be the floor a rewind restores from (#2379): everything it holds must come before the trigger,
/// and before everything it does not hold, in the order the replay folds the stream (<see cref="ReplayEventOrder"/>).
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is taken at the event with the highest version it holds, and it holds every event of the stream at or
/// below that version (<see cref="FoldedVersionPrefix"/> decides when a replay's fold is such a set). The rewind restores
/// it and replays the events above its version. That is the replay's own fold only when the events the snapshot holds
/// are exactly the first ones in replay order: when a single-origin stream's origin order puts an event above the
/// snapshot's version before one at or below it, the snapshot holds a fold the replay would never produce, and restoring
/// it folds the earlier event after the later one.
/// </para>
/// <para>
/// So the floor is judged by the replay's order, never another. #1355 placed a snapshot by version alone, which is the
/// replay's order only for a stream with no origin to deal; a deployed stream whose first event had the highest id
/// restored a snapshot that held its second version and folded the producer's Started events after its Completed one.
/// When the snapshot does not qualify the rewind replays in full; it never falls back to a floor in another order.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#snapshot-selection</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReplayFloorTests.cs</tests>
public static class ReplayFloor {
  /// <summary>
  /// True when the snapshot taken at <paramref name="snapshotVersion"/> is a floor for a rewind triggered by
  /// <paramref name="triggeringEventId"/>: the trigger sits above the snapshot's version, and the stream's events at or
  /// below that version are exactly the first ones in replay order.
  /// </summary>
  /// <param name="stream">Every event of the stream the replay reads (the perspective's types), in any order.</param>
  /// <param name="snapshotVersion">The stream version the snapshot was taken at; null when it records none.</param>
  /// <param name="triggeringEventId">The event the rewind is for.</param>
  /// <returns>Whether the rewind may restore the snapshot and replay the events above its version.</returns>
  /// <docs>fundamentals/perspectives/rewind#snapshot-selection</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReplayFloorTests.cs:Qualifies_WhenTheOriginOrderPutsAnEventAboveTheSnapshotBeforeOneItHolds_IsFalseAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReplayFloorTests.cs:Qualifies_WhenTheHeldEventsComeFirstInReplayOrder_IsTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReplayFloorTests.cs:AQualifyingFloor_AndTheReplayAboveIt_FoldTheStreamInTheOrderAFullReplayFoldsItAsync</tests>
  public static bool Qualifies(IReadOnlyList<MessageEnvelope<IEvent>> stream, int? snapshotVersion, Guid triggeringEventId) {
    ArgumentNullException.ThrowIfNull(stream);
    if (snapshotVersion is not int version || stream.Any(e => e.LocalStreamVersion is null)) {
      return false;
    }
    var trigger = stream.FirstOrDefault(e => e.MessageId.Value == triggeringEventId);
    if (trigger is null || trigger.LocalStreamVersion <= version) {
      return false;
    }
    var held = stream.Count(e => e.LocalStreamVersion <= version);
    return ReplayEventOrder.Order(stream).Take(held).All(e => e.LocalStreamVersion <= version);
  }
}
