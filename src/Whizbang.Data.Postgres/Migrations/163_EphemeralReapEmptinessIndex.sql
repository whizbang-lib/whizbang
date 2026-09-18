-- Migration: 163_EphemeralReapEmptinessIndex.sql
-- Date: 2026-09-18
-- Description: The ephemeral reap's two candidate scans answer from an index instead of scanning
--              the whole event store and the whole body table.
--
--              Every maintenance cycle the reap asks two questions, and both are built the same way
--              (EFCoreWorkCoordinator.GetEphemeralPairsNeedingSnapshotAsync and
--              GetEphemeralBodiesToDestroyAsync):
--
--                FROM __SCHEMA__.wh_event_body eb
--                JOIN __SCHEMA__.wh_event_store es ON es.event_id = eb.event_id
--                LEFT JOIN __SCHEMA__.wh_ephemeral_type_grace g ON g.event_type = es.event_type
--                WHERE (es.flags & 8) = 8
--                  AND es.created_at < NOW() - (COALESCE(g.grace_seconds, ...) * INTERVAL '1 second')
--                  ...
--
--              Nothing served that predicate. wh_event_store carries seven indexes and not one of
--              them keys or filters on flags, so there was no selective path into the ephemeral
--              rows, and the planner drove the join from wh_event_body -- which has exactly one
--              index, its primary key -- and read both tables end to end.
--
--              This is the same defect as the outbox and perspective emptiness probes (160, 161),
--              one subsystem over, and it is by far the most expensive instance of it. The answer
--              the reap gets almost always is "there is nothing to reap", and that is precisely the
--              case that scans to exhaustion.
--
--              Measured on a deployed fleet, on an IDLE environment with no import running, from
--              pg_stat_statements:
--
--                                                        calls    rows      total     per call
--                the snapshot-pairs scan                   205       0     1,405 s     6,851 ms
--                the bodies-to-destroy scan                205       0       714 s     3,484 ms
--
--              Two thousand one hundred and nineteen seconds of database CPU, and 9.6 million
--              block reads from disk, to return zero rows four hundred and ten times. In a single
--              304-second window the two of them accounted for 60 seconds of statement time while
--              the system had no work at all.
--
--              Why zero rows: of 2,150,816 rows in wh_event_store, 4,464 carry the ephemeral flag
--              (0.2 percent), and none of those currently passes the grace window and
--              snapshot-coverage gates. The scan cost is paid in full to discover that.
--
--              The table sizes are what make it expensive rather than merely wasteful:
--              wh_event_body is 5,158 MB over 2,132,018 rows with only its primary key, and
--              wh_event_store is 1,470 MB over 2,153,501 rows.
--
-- RECLAIM: not applicable -- this migration adds an index and drops nothing.

-- The flag is EventFlags.Ephemeral (1 << 3) from src/Whizbang.Core/Messaging/EventFlags.cs, written
-- as the literal 8 because that is exactly how both callers write it, and a partial index is only
-- usable when the planner can prove the query's predicate implies the index's. It is deliberately
-- NOT promoted to Migrations/constants.txt: rule 12's check is a substring match on the constant's
-- VALUE, so a single-digit constant would flag every line in every migration containing an 8,
-- including the migration numbers and the dates.
--
-- The predicate carries the flag test ALONE. The snapshot-pairs query additionally requires
-- commit_sequence IS NOT NULL, and narrowing the index with it would make the index unusable for the
-- bodies-to-destroy query, which does not. One index serving both is worth more than a marginally
-- smaller one serving half.
--
-- created_at leads the key because it is the range both queries apply after the flag test, so the
-- grace window is an index condition rather than a filter. event_id follows so the nested-loop probe
-- into wh_event_body -- a primary-key lookup -- is fed straight from the index.
--
-- INCLUDE covers the remaining wh_event_store columns both queries project or join on, which keeps
-- the event-store side of the scan index-only. eb is then touched only through its primary key, so
-- neither 5 GB table is read sequentially.
CREATE INDEX IF NOT EXISTS idx_event_store_ephemeral_reap
  ON __SCHEMA__.wh_event_store (created_at, event_id)
  INCLUDE (stream_id, event_type, commit_sequence)
  WHERE (flags & 8) = 8;

COMMENT ON INDEX __SCHEMA__.idx_event_store_ephemeral_reap IS
  'Serves the ephemeral reap''s two candidate scans (snapshot pairs, bodies to destroy). Partial on '
  'EventFlags.Ephemeral (8), keyed on the grace-window range, covering the columns those queries '
  'read from wh_event_store so the ephemeral side is index-only. Before it, both queries read '
  'wh_event_store and wh_event_body end to end to return zero rows.';

-- The planner will not choose the new index over the sequential scans it has been costing until the
-- table's statistics describe the partial index's selectivity. ANALYZE is cheap next to what this
-- migration exists to remove, and leaving it out means the fix reads as ineffective on the first
-- cycle after deployment for no reason.
ANALYZE __SCHEMA__.wh_event_store;
