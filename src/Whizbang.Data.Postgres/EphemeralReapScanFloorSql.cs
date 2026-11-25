// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The floor the ephemeral reap's candidate scans start at (migration 597, #2574): an instant below which no ephemeral
/// event still has a body, advanced by <c>wh_ephemeral_reap_scan_floor()</c> each time a scan reads it.
/// </summary>
/// <remarks>
/// The reap deletes a body and keeps its pointer, so <c>idx_event_store_ephemeral_reap</c> holds every ephemeral pointer
/// the store has kept, almost none of which still has a body. The snapshot-pairs scan and the about-to-reap scan walked
/// all of them every cycle. Read as an uncorrelated subquery, the floor is evaluated once per statement and becomes an
/// index condition on the key the index leads with, so each scan reads only the pointers above it.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#the-reap-scans-start-at-the-oldest-body</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapScanFloorSqlTests.cs:Scans_ACycleAfterTheFirst_ReadOnlyThePointersAboveTheFloorAsync</tests>
internal static class EphemeralReapScanFloorSql {
  /// <summary>The predicate that keeps a scan of <c>wh_event_store es</c> at or above the floor.</summary>
  /// <param name="prefix">The quoted schema and a dot, or empty to resolve through <c>search_path</c>.</param>
  internal static string AtOrAboveFloor(string prefix) =>
    $"es.created_at >= (SELECT {prefix}wh_ephemeral_reap_scan_floor())";
}
