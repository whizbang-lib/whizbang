// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The startup orphaned-lifecycle reconciliation query behind
/// <see cref="Whizbang.Core.Messaging.IWorkCoordinator.GetOrphanedLifecycleEventsAsync"/>, held apart so a plan-shape
/// test can EXPLAIN the very text the coordinator runs.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#brin-indexes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/BrinIndexCandidatesTests.cs:TheOrphanedLifecycleQuery_ReadsTheWindowThroughTheBrinIndexAsync</tests>
internal static class OrphanedLifecycleEventsSql {
  /// <summary>
  /// The query text for the given tables: events inside the lookback window whose expected perspectives all have a
  /// cursor at or past them and that have no completion marker, oldest first. Parameters: {0} the expected event
  /// types, {1} the expected perspective names (parallel arrays), {2} the window's start, {3} the row cap.
  /// </summary>
  /// <remarks>
  /// The window (<c>created_at &gt;= {2}</c>) is the selective predicate: thirty minutes of a store that keeps
  /// every event. #1745 serves it with <c>idx_event_store_created_at_brin</c> (migration 258).
  /// </remarks>
  /// <docs>operations/infrastructure/storage-maintenance#brin-indexes</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/BrinIndexCandidatesTests.cs:TheOrphanedLifecycleQuery_ReadsTheWindowThroughTheBrinIndexAsync</tests>
  internal static string Build(
      string eventStoreTable, string bodyTable, string cursorsTable, string completionsTable) =>
#pragma warning disable S2077
    $@"
      WITH expected AS (
        SELECT * FROM unnest({{0}}::text[], {{1}}::text[]) AS x(event_type, perspective_name)
      ),
      expected_counts AS (
        SELECT event_type, COUNT(*) AS cnt FROM expected GROUP BY event_type
      )
      SELECT e.event_id, e.stream_id, eb.event_data AS event_data, eb.metadata AS metadata, e.event_type, e.scope
      FROM {eventStoreTable} e
      JOIN expected_counts ec ON ec.event_type = e.event_type
      LEFT JOIN {bodyTable} eb ON eb.event_id = e.event_id
      WHERE e.created_at >= {{2}}
        AND NOT EXISTS (
          SELECT 1 FROM {completionsTable} lc WHERE lc.event_id = e.event_id
        )
        AND (
          SELECT COUNT(DISTINCT pc.perspective_name)
          FROM {cursorsTable} pc
          JOIN expected x ON x.event_type = e.event_type AND x.perspective_name = pc.perspective_name
          WHERE pc.stream_id = e.stream_id
            AND pc.last_event_id >= e.event_id
        ) = ec.cnt
      ORDER BY e.created_at
      LIMIT {{3}}";
#pragma warning restore S2077
}
