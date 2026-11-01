// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Says, at the moment a snapshot of a row with collectives is taken, how far the row's collectives are complete (#1963).
/// The collective sink applies collectives to rows on its own, a key at a time and a batch at a time, so a live snapshot
/// can hold a collective a replay from it would fold again, or miss one a replay from it would skip. The runner reads the
/// fence before it reads the row and again after it writes the row: when no collective of the row was in flight at
/// either read, and none completed between them, the snapshot holds exactly the row's collectives stamped at or below
/// the first read's <see cref="CollectiveSnapshotFenceReading.CompleteThroughSequence"/>, and records it. Otherwise the
/// snapshot is deferred to a later apply. Reads only: the collective sink's update path writes nothing for it.
/// </summary>
/// <remarks>
/// Both Postgres drivers register one, over the driver's database. A host without one takes snapshots that record no
/// mark, which keep the snapshot-event rule.
/// </remarks>
/// <docs>fundamentals/perspectives/snapshots#collectives</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs</tests>
public interface ICollectiveSnapshotFence {
  /// <summary>
  /// Reads the fence in one statement, so everything it reports comes from one view of the database.
  /// </summary>
  /// <param name="tenantId">The tenant the row's collectives belong to; null for a row whose events carry no tenant.</param>
  /// <param name="collectiveEventTypes">The stored event-type names of the model's collectives
  /// (<see cref="ICollectiveReplayApplier.CollectiveEventTypeNamesFor"/>).</param>
  /// <param name="afterSequence">On the read after the row's write, the mark the read before it returned: a collective of
  /// the row stamped past it completed between the two reads, and counts as in flight. Null on the first read.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The store's highest commit sequence and whether a collective of the row is in flight.</returns>
  Task<CollectiveSnapshotFenceReading> ReadAsync(
    string? tenantId,
    IReadOnlyList<string> collectiveEventTypes,
    long? afterSequence,
    CancellationToken cancellationToken = default);
}

/// <summary>One read of the <see cref="ICollectiveSnapshotFence"/> (#1963).</summary>
/// <param name="CompleteThroughSequence">The store's highest commit sequence, or null for a store with no stamped event.
/// When nothing is in flight, every collective of the row stamped at or below it is complete, and one stamped later is
/// stamped above it.</param>
/// <param name="CollectiveInFlight">Whether a collective of the row is not yet complete: its sink work is unprocessed, its
/// event is unstamped, or it was stamped past the read's <c>afterSequence</c>.</param>
/// <docs>fundamentals/perspectives/snapshots#collectives</docs>
public sealed record CollectiveSnapshotFenceReading(long? CompleteThroughSequence, bool CollectiveInFlight);
