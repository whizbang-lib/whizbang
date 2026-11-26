// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The queue tables whose indexes the maintenance cycle rebuilds, and the catalog reads behind the rebuild (#1746).
/// </summary>
/// <remarks>
/// <para>
/// A queue table here is one that churns to empty: a row is written per unit of work and deleted when that work
/// completes, so the table drains to nothing whenever the service is idle. Derived from the migrations: the outbox and
/// inbox (rows deleted on completion, 088), the inbox's work state (deleted with its inbox row by ON DELETE CASCADE,
/// 162), perspective events (one row per event and perspective, deleted once processed, 088), active streams (a row per
/// stream with work, cleaned up when it completes, 007 and 088) and the doorbell queue (rows deleted as they are rung,
/// 146).
/// </para>
/// <para>
/// Left out on purpose: the deduplication window (<c>wh_message_deduplication</c>) holds a fixed-size working set for
/// its retention and never drains, and the destruction hold keeps a cancel decision indefinitely. Neither returns to
/// empty, so neither strands empty index pages the way a drained queue does.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#reindex-which-tables</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperTableMaintenanceTests.cs</tests>
internal static class QueueIndexSql {
  /// <summary>The queue tables, unqualified.</summary>
  internal static readonly string[] TABLES =
    ["wh_outbox", "wh_inbox", "wh_inbox_state", "wh_perspective_events", "wh_active_streams", "wh_doorbell_queue"];

  /// <summary>The action a log names when an index is not dropped.</summary>
  internal const string DROPPED = "dropped";

  /// <summary>The action a log names when an index is not rebuilt.</summary>
  internal const string REBUILT = "rebuilt";

  /// <summary>
  /// Every B-tree index on the queue tables (<c>@prefix</c> is the schema qualifier, <c>@tables</c> the table names): name,
  /// table, size, expected size (null when unknown), validity, and whether an index build is running on its table.
  /// </summary>
  internal const string SURVEY = """
    WITH q AS (
      SELECT ic.oid AS index_oid, tc.oid AS table_oid, ic.relname AS index_name, tc.relname AS table_name,
             n.nspname AS schema_name, ic.reltuples, i.indkey, i.indisvalid,
             pg_relation_size(ic.oid) AS size_bytes,
             current_setting('block_size')::BIGINT AS block_size,
             COALESCE((SELECT substring(o FROM 12)::INT FROM unnest(ic.reloptions) o WHERE o LIKE 'fillfactor=%'), 90) AS fill
      FROM pg_index i
      JOIN pg_class ic ON ic.oid = i.indexrelid
      JOIN pg_class tc ON tc.oid = i.indrelid
      JOIN pg_namespace n ON n.oid = tc.relnamespace
      JOIN pg_am am ON am.oid = ic.relam
      WHERE am.amname = 'btree' AND ic.relkind = 'i'
        AND tc.oid IN (SELECT to_regclass(@prefix || quote_ident(t)) FROM unnest(@tables) t)
    )
    SELECT q.index_name, q.table_name, q.size_bytes,
      CASE
        WHEN q.reltuples < 0 THEN NULL
        WHEN q.reltuples = 0 THEN 2 * q.block_size
        WHEN w.width IS NULL THEN NULL
        ELSE GREATEST(2, 1 + ceil(q.reltuples * (ceil((8 + w.width) / 8.0) * 8 + 4)
                                  / ((q.block_size - 40) * q.fill / 100.0)))::BIGINT * q.block_size
      END,
      q.indisvalid,
      EXISTS (
        SELECT 1 FROM pg_stat_progress_create_index p
        WHERE p.datid = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database())
          AND (p.relid = q.table_oid OR p.relid IS NULL))
    FROM q
    LEFT JOIN LATERAL (
      SELECT CASE WHEN bool_and(c.width IS NOT NULL) THEN sum(c.width) END AS width
      FROM (
        SELECT COALESCE(
          (SELECT s.avg_width FROM pg_stats s
           WHERE s.schemaname = q.schema_name
             AND ((k.attnum > 0 AND s.tablename = q.table_name
                   AND s.attname = (SELECT ta.attname FROM pg_attribute ta WHERE ta.attrelid = q.table_oid AND ta.attnum = k.attnum))
               OR (k.attnum = 0 AND s.tablename = q.index_name
                   AND s.attname = (SELECT ia.attname FROM pg_attribute ia WHERE ia.attrelid = q.index_oid AND ia.attnum = k.ord)))),
          (SELECT NULLIF(ia.attlen, -1) FROM pg_attribute ia WHERE ia.attrelid = q.index_oid AND ia.attnum = k.ord)) AS width
        FROM unnest(q.indkey::INT2[]) WITH ORDINALITY AS k(attnum, ord)
      ) c
    ) w ON TRUE
    ORDER BY q.table_name, q.index_name
    """;

  /// <summary>The queue table <c>@index</c> is on, and whether it is valid; no row when it is not an index on a queue table.</summary>
  internal const string LOOKUP = """
    SELECT tc.relname, i.indisvalid
    FROM pg_index i
    JOIN pg_class tc ON tc.oid = i.indrelid
    WHERE i.indexrelid = to_regclass(@prefix || quote_ident(@index))
      AND tc.oid IN (SELECT to_regclass(@prefix || quote_ident(t)) FROM unnest(@tables) t)
    """;

  /// <summary>
  /// The sessions a concurrent rebuild of <c>@q</c> that started at <c>@started</c> was waiting on (#2611): every other
  /// session of this database whose transaction began before the rebuild and still holds a snapshot or a transaction id,
  /// and every session holding a lock on the index's table. One row per session (pid, seconds its transaction has been
  /// open, or NULL when this role may not read it), oldest first, then one row with a NULL pid carrying how many sessions
  /// in the database this role may not read at all.
  /// </summary>
  internal const string BLOCKERS = """
    WITH tbl AS (SELECT i.indrelid FROM pg_index i WHERE i.indexrelid = to_regclass(@q)),
    blockers AS (
      SELECT l.pid FROM pg_locks l, tbl
      WHERE l.locktype = 'relation' AND l.relation = tbl.indrelid AND l.granted AND l.pid <> pg_backend_pid()
        AND l.database = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database())
      UNION
      SELECT a.pid FROM pg_stat_activity a
      WHERE a.datname = current_database() AND a.pid <> pg_backend_pid() AND a.xact_start <= @started
        AND (a.backend_xmin IS NOT NULL OR a.backend_xid IS NOT NULL)
    )
    SELECT r.pid, r.age_seconds, r.hidden FROM (
      SELECT b.pid, EXTRACT(EPOCH FROM clock_timestamp() - a.xact_start)::FLOAT8 AS age_seconds, 0 AS hidden, a.xact_start
      FROM blockers b LEFT JOIN pg_stat_activity a ON a.pid = b.pid
      UNION ALL
      SELECT NULL::INT, NULL::FLOAT8, count(*)::INT, NULL::TIMESTAMPTZ
      FROM pg_stat_activity a
      WHERE a.datname = current_database() AND a.pid <> pg_backend_pid() AND a.query = '<insufficient privilege>'
    ) r
    ORDER BY r.pid IS NULL, r.xact_start NULLS LAST, r.pid
    """;
}
