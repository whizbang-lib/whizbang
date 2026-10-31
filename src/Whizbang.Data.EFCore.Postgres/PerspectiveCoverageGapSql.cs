// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The stream-integrity Phase L query behind <see cref="Whizbang.Core.Messaging.IWorkCoordinator.GetPerspectiveCoverageGapsAsync"/>:
/// the (stream, perspective) pairs a registered perspective should fold and has never touched.
/// </summary>
/// <docs>resilience/stream-integrity#phase-l-local-perspective-coverage-audit</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTests.cs</tests>
internal static class PerspectiveCoverageGapSql {
  /// <summary>
  /// The query text for one schema. It takes <c>@p_settle</c> (an interval) and <c>@p_max</c> (the report cap)
  /// and returns <c>(stream_id, target_name, event_count)</c> ordered by stream and perspective.
  /// </summary>
  /// <remarks>
  /// <para>
  /// An event counts toward a gap only once it is settled (older than the settle window), durable (not ephemeral)
  /// and stamped (#1791): an event without a commit sequence is held back from every perspective by the stamp fence,
  /// so its missing cursor is the fence at work, not a perspective that never folded it. History an upgrade leaves
  /// unstamped is old by its creation time and would otherwise be reported, and rebuilt, until the stamper reaches it.
  /// </para>
  /// <para>
  /// The cap bounds what the query reads, not only what it returns. The streams are walked one at a time through
  /// <c>idx_event_store_stream</c> (a recursive step finds the next stream id above the last one), each stream's
  /// rows are read once and grouped by type before they meet the perspective associations, and the walk stops as soon as the cap is reached. A grouped join
  /// of the whole store followed by a limit reads and groups every event before it can return the first gap.
  /// </para>
  /// </remarks>
  /// <param name="schema">The schema-qualified prefix the coordinator resolved for its tables.</param>
  /// <returns>The SQL text.</returns>
  internal static string Build(string schema) => $"""
    WITH RECURSIVE walk(stream_id) AS (
      (SELECT es.stream_id FROM {schema}.wh_event_store es ORDER BY es.stream_id LIMIT 1)
      UNION ALL
      SELECT (SELECT es.stream_id FROM {schema}.wh_event_store es
              WHERE es.stream_id > w.stream_id
              ORDER BY es.stream_id LIMIT 1)
      FROM walk w
      WHERE w.stream_id IS NOT NULL
    )
    SELECT found.stream_id, found.target_name, found.event_count
    FROM (
      SELECT w.stream_id, gap.target_name, gap.event_count
      FROM walk w
      CROSS JOIN LATERAL (
        SELECT ma.target_name, SUM(held.event_count)::int AS event_count
        FROM (
          SELECT es.event_type, COUNT(*) AS event_count
          FROM {schema}.wh_event_store es
          WHERE es.stream_id = w.stream_id
            AND es.commit_sequence IS NOT NULL
            AND es.created_at < NOW() - @p_settle::interval
            AND COALESCE(es.flags, 0) & 8 = 0
            AND NOT EXISTS (
              SELECT 1 FROM {schema}.wh_perspective_events pe
              WHERE pe.stream_id = w.stream_id AND pe.event_id = es.event_id AND pe.processed_at IS NULL)
          GROUP BY es.event_type
        ) held
        JOIN {schema}.wh_message_associations ma
          ON ma.normalized_message_type = held.event_type AND ma.association_type = 'perspective'
        WHERE NOT EXISTS (
          SELECT 1 FROM {schema}.wh_perspective_cursors c
          WHERE c.stream_id = w.stream_id AND c.perspective_name = ma.target_name)
        GROUP BY ma.target_name
        ORDER BY ma.target_name
      ) gap
      WHERE w.stream_id IS NOT NULL
      LIMIT @p_max
    ) found
    ORDER BY 1, 2
    """;
}
