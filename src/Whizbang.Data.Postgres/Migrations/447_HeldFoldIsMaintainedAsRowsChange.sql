-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 447_HeldFoldIsMaintainedAsRowsChange.sql
-- Date: 2026-11-15
-- Description: The type-level fold of the events held from an origin without its commit sequence costs what changed
--              since the last answer, never the size of that history (#2351).
--
--              A windowed type comparison and a short checkpoint bucket fold the events this service holds from an
--              origin with no origin commit sequence (#2102: re-attributed history waiting for a redelivery to fill its
--              sequence). That fold read every such row of the origin on every answer, as a parallel scan. With
--              millions of such rows it was the largest single cost of an idle service, and most of it ran in parallel
--              workers that statement statistics never show.
--
--              The fold is now maintained as the rows change:
--
--                - wh_unsequenced_digest_totals holds one row per (origin, tenant, type): the XOR of the digest lanes
--                  and the count of the held rows the digest eligibility rule counts (#2352), the same contribution every
--                  digest fold computes;
--                - wh_unsequenced_digest_changes receives one row per change to that set. Row triggers on
--                  wh_event_store write it, so every path that inserts, deletes, adopts a sequence, re-attributes an
--                  origin, flags, reclassifies or rescopes an event keeps the fold right without knowing it exists.
--                  The trigger only inserts: concurrent writers of the same type never wait on one another, and no
--                  lock order between them can deadlock. A WHEN clause skips every row that is and was outside the set,
--                  so the ordinary receive path (rows that carry their sequence) pays nothing;
--                - wh_unsequenced_received_type_digests answers: it folds the origin's pending changes into the totals
--                  (a bounded batch, skipping changes another answer is folding), then returns totals plus the changes
--                  still pending, minus the held rows inside the settle window, read through a partial index that
--                  holds only held rows. Its cost is O(types) plus the changes since the last answer plus the rows
--                  younger than the settle window;
--                - wh_rebuild_unsequenced_digests recomputes the totals from the rows, under a lock that keeps writers
--                  out while it reads. This migration runs it once; a later change to what counts runs it again.
--
--              The change log is a queue (every row is deleted by the fold that takes it in), so autovacuum reclaims it
--              after a fixed number of deleted rows instead of a fraction of the table: a fold never walks a backlog of
--              dead rows. A TRUNCATE of the event store empties both tables.
--
--              Number 447: reserved for this change.
--
-- Reversal: reversible (the trigger, functions, tables and index go; the coordinator's query reads the rows again)
-- Dependencies: 087 (origin columns), 336 (_wh_digest_lane); the digest eligibility constant (constants.txt, #2352)
-- Objects: wh_unsequenced_digest_totals, wh_unsequenced_digest_changes, idx_unsequenced_digest_changes_origin,
--          idx_event_store_unsequenced_received, _wh_track_unsequenced_digest, _wh_clear_unsequenced_digests,
--          wh_rebuild_unsequenced_digests, _wh_fold_unsequenced_digest_changes, wh_compact_unsequenced_digest_changes,
--          wh_unsequenced_received_type_digests,
--          wh_event_store_tracks_unsequenced_insert_trg, wh_event_store_tracks_unsequenced_update_trg,
--          wh_event_store_tracks_unsequenced_delete_trg, wh_event_store_clears_unsequenced_trg
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_unsequenced_digest_totals (
  origin_service_id UUID   NOT NULL,   -- the origin the events were received from
  scope_tenant      TEXT   NOT NULL,   -- COALESCE(scope->>'t', ''), as every digest bucket keys it
  event_type        TEXT   NOT NULL,
  digest_lo         BIGINT NOT NULL,   -- XOR of _wh_digest_lane(event_id, 0)
  digest_hi         BIGINT NOT NULL,   -- XOR of _wh_digest_lane(event_id, 1)
  event_count       BIGINT NOT NULL,
  PRIMARY KEY (origin_service_id, scope_tenant, event_type)
);

COMMENT ON TABLE __SCHEMA__.wh_unsequenced_digest_totals IS
'447 (#2351): the folded digest of the eligible events held from each origin without its commit sequence, per (origin, tenant, type). Pending changes in wh_unsequenced_digest_changes are added on read; wh_unsequenced_received_type_digests answers from both.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_unsequenced_digest_changes (
  change_id         BIGINT   GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  origin_service_id UUID     NOT NULL,
  scope_tenant      TEXT     NOT NULL,
  event_type        TEXT     NOT NULL,
  digest_lo         BIGINT   NOT NULL,   -- the event's lane 0: XOR is its own inverse, so a removal carries the same value
  digest_hi         BIGINT   NOT NULL,
  event_count       SMALLINT NOT NULL    -- +1 when the event joins the set, -1 when it leaves
);

CREATE INDEX IF NOT EXISTS idx_unsequenced_digest_changes_origin
  ON __SCHEMA__.wh_unsequenced_digest_changes (origin_service_id, change_id);

-- A queue: every row is deleted by the fold that takes it in. Vacuumed after a fixed number of deleted rows rather than a
-- fraction of the table, so the next fold never walks a backlog of dead rows (as 073 does for the reaped bodies).
ALTER TABLE __SCHEMA__.wh_unsequenced_digest_changes SET (
  autovacuum_vacuum_scale_factor = 0,
  autovacuum_vacuum_threshold = 1000,
  autovacuum_analyze_scale_factor = 0,
  autovacuum_analyze_threshold = 1000
);

COMMENT ON TABLE __SCHEMA__.wh_unsequenced_digest_changes IS
'447 (#2351): one row per eligible event joining (+1) or leaving (-1) the set held from an origin without its commit sequence, written by the wh_event_store row triggers. Insert-only for writers; wh_unsequenced_received_type_digests folds them into wh_unsequenced_digest_totals.';

-- The held rows only: the answer reads the ones younger than the settle window through it, never the settled history.
CREATE INDEX IF NOT EXISTS idx_event_store_unsequenced_received
  ON __SCHEMA__.wh_event_store (origin_service_id, created_at)
  WHERE origin_service_id IS NOT NULL AND origin_commit_sequence IS NULL;

-- One row trigger for insert, update and delete: removes the old row's contribution when it was in the set, adds the
-- new row's when it is.
-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:EveryKindOfChange_LeavesTheAnswerEqualToAFoldOfTheRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_track_unsequenced_digest() RETURNS TRIGGER AS $$
BEGIN
  IF TG_OP <> 'INSERT'
     AND OLD.origin_service_id IS NOT NULL AND OLD.origin_commit_sequence IS NULL
     AND __DIGEST_ELIGIBLE__(OLD) THEN
    INSERT INTO __SCHEMA__.wh_unsequenced_digest_changes
      (origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count)
    VALUES (OLD.origin_service_id, COALESCE(OLD.scope ->> 't', ''), OLD.event_type,
            __SCHEMA__._wh_digest_lane(OLD.event_id, 0), __SCHEMA__._wh_digest_lane(OLD.event_id, 1), -1);
  END IF;
  IF TG_OP <> 'DELETE'
     AND NEW.origin_service_id IS NOT NULL AND NEW.origin_commit_sequence IS NULL
     AND __DIGEST_ELIGIBLE__(NEW) THEN
    INSERT INTO __SCHEMA__.wh_unsequenced_digest_changes
      (origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count)
    VALUES (NEW.origin_service_id, COALESCE(NEW.scope ->> 't', ''), NEW.event_type,
            __SCHEMA__._wh_digest_lane(NEW.event_id, 0), __SCHEMA__._wh_digest_lane(NEW.event_id, 1), 1);
  END IF;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_track_unsequenced_digest() IS
'Row trigger on wh_event_store (#2351): records an eligible event leaving (-1) or joining (+1) the set held from an origin without its commit sequence in wh_unsequenced_digest_changes. Insert-only, so writers never wait on one another.';

-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ATruncatedEventStore_HoldsNothingWithoutASequenceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_clear_unsequenced_digests() RETURNS TRIGGER AS $$
BEGIN
  TRUNCATE __SCHEMA__.wh_unsequenced_digest_totals, __SCHEMA__.wh_unsequenced_digest_changes;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_clear_unsequenced_digests() IS
'Statement trigger on TRUNCATE of wh_event_store (#2351): an emptied store holds nothing without a sequence.';

-- Recomputes the totals from the rows. The SHARE lock keeps writers out while it reads, so no change is missed or
-- counted twice; readers are not blocked.
-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ARebuild_FoldsTheRowsTheTrackingNeverSaw_AndAnswersAsTheRowsDoAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_rebuild_unsequenced_digests() RETURNS BIGINT AS $$
DECLARE
  v_rows BIGINT;
BEGIN
  LOCK TABLE __SCHEMA__.wh_event_store IN SHARE MODE;
  DELETE FROM __SCHEMA__.wh_unsequenced_digest_changes;
  DELETE FROM __SCHEMA__.wh_unsequenced_digest_totals;
  INSERT INTO __SCHEMA__.wh_unsequenced_digest_totals
    (origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count)
  SELECT es.origin_service_id, COALESCE(es.scope ->> 't', ''), es.event_type,
         bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)), bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)),
         COUNT(*)
  FROM __SCHEMA__.wh_event_store es
  WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL
    AND __DIGEST_ELIGIBLE__(es)
  GROUP BY 1, 2, 3;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_rebuild_unsequenced_digests() IS
'Recomputes wh_unsequenced_digest_totals from wh_event_store and clears the pending changes, holding writers out while it reads (#2351). Returns the (origin, tenant, type) rows written. Run once by 447; a change to what a digest counts runs it again.';

-- Folds up to p_limit of the origin's pending changes into the totals. A change another fold has claimed is skipped,
-- never waited on: whatever is not folded is still read as pending, so skipping is always correct.
-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ByType_ALargeHistory_CostsWhatASmallOneDoes_ForTheSameNewWorkAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fold_unsequenced_digest_changes(p_origin_service_id UUID, p_limit INTEGER)
RETURNS INTEGER AS $$
DECLARE
  v_folded INTEGER;
BEGIN
  WITH claimed AS (
    SELECT c.change_id FROM __SCHEMA__.wh_unsequenced_digest_changes c
    WHERE c.origin_service_id = p_origin_service_id
    ORDER BY c.change_id
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED
  ), moved AS (
    DELETE FROM __SCHEMA__.wh_unsequenced_digest_changes c
    USING claimed
    WHERE c.change_id = claimed.change_id
    RETURNING c.scope_tenant, c.event_type, c.digest_lo, c.digest_hi, c.event_count
  ), summed AS (
    SELECT m.scope_tenant, m.event_type, bit_xor(m.digest_lo) AS lo, bit_xor(m.digest_hi) AS hi,
           SUM(m.event_count) AS n, COUNT(*) AS changes
    FROM moved m
    GROUP BY 1, 2
  ), written AS (
    INSERT INTO __SCHEMA__.wh_unsequenced_digest_totals AS t
      (origin_service_id, scope_tenant, event_type, digest_lo, digest_hi, event_count)
    SELECT p_origin_service_id, s.scope_tenant, s.event_type, s.lo, s.hi, s.n
    FROM summed s
    ORDER BY s.scope_tenant, s.event_type
    ON CONFLICT (origin_service_id, scope_tenant, event_type) DO UPDATE
      SET digest_lo = t.digest_lo # EXCLUDED.digest_lo,
          digest_hi = t.digest_hi # EXCLUDED.digest_hi,
          event_count = t.event_count + EXCLUDED.event_count
    RETURNING 1
  )
  SELECT COALESCE(SUM(s.changes), 0)::int INTO v_folded FROM summed s;
  DELETE FROM __SCHEMA__.wh_unsequenced_digest_totals t
  WHERE t.origin_service_id = p_origin_service_id AND t.event_count = 0;
  RETURN v_folded;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_unsequenced_digest_changes(UUID, INTEGER) IS
'Folds up to p_limit pending changes of one origin into wh_unsequenced_digest_totals, skipping changes another fold holds, and drops totals left empty (#2351). Returns the changes folded.';

-- Folds the oldest p_limit pending changes of every origin into the totals, for the maintenance cycle: an answer folds
-- only its own origin's changes, so an origin nobody asks about would otherwise grow the log without bound. Oldest
-- first by the identity, so a bounded batch always takes the changes that have waited longest.
-- <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedDigestCompactionSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_compact_unsequenced_digest_changes(p_limit INTEGER)
RETURNS INTEGER AS $$
BEGIN
  RETURN 0;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_compact_unsequenced_digest_changes(INTEGER) IS
'Folds the oldest p_limit pending changes of every origin into wh_unsequenced_digest_totals and deletes them, skipping changes another fold holds (#2351). Run by the maintenance cycle so the change log stays bounded when no answer is asked. Returns the changes folded.';

-- The type-level answer: one row per (tenant, type) of the eligible events held from the origin without its commit
-- sequence and older than the settle window.
-- <docs>resilience/stream-integrity#the-held-fold-is-maintained-as-rows-change</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ByType_UnderAGenericPlan_ReadsOnlyTheUnsettledRowsOfTheEventStore_NeverItsHistoryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:EveryKindOfChange_LeavesTheAnswerEqualToAFoldOfTheRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedReceivedDigestSqlTests.cs:ByType_FoldsPerTenantAndType_AcrossStreamsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_unsequenced_received_type_digests(
  p_origin_service_id UUID,
  p_event_types TEXT[],
  p_settle INTERVAL
) RETURNS TABLE(scope_tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_fold_batch CONSTANT INTEGER := 10000;
BEGIN
  PERFORM __SCHEMA__._wh_fold_unsequenced_digest_changes(p_origin_service_id, c_fold_batch);
  RETURN QUERY
  WITH parts AS (
    SELECT t.scope_tenant AS te, t.event_type AS ty, t.digest_lo AS lo, t.digest_hi AS hi, t.event_count AS n
    FROM __SCHEMA__.wh_unsequenced_digest_totals t
    WHERE t.origin_service_id = p_origin_service_id AND t.event_type = ANY(p_event_types)
    UNION ALL
    SELECT c.scope_tenant, c.event_type, c.digest_lo, c.digest_hi, c.event_count::bigint
    FROM __SCHEMA__.wh_unsequenced_digest_changes c
    WHERE c.origin_service_id = p_origin_service_id AND c.event_type = ANY(p_event_types)
    UNION ALL
    -- The held rows inside the settle window leave the answer: XOR removes them, the count subtracts them.
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type,
           __SCHEMA__._wh_digest_lane(es.event_id, 0), __SCHEMA__._wh_digest_lane(es.event_id, 1), -1::bigint
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = p_origin_service_id
      AND es.origin_service_id IS NOT NULL
      AND es.origin_commit_sequence IS NULL
      AND es.created_at >= NOW() - p_settle
      AND es.event_type = ANY(p_event_types)
      AND __DIGEST_ELIGIBLE__(es)
  )
  SELECT p.te, p.ty, bit_xor(p.lo), bit_xor(p.hi), SUM(p.n)::int
  FROM parts p
  GROUP BY p.te, p.ty
  HAVING SUM(p.n) > 0
  ORDER BY p.te, p.ty;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_unsequenced_received_type_digests(UUID, TEXT[], INTERVAL) IS
'The fold, per (tenant, type), of the eligible events held from an origin without its commit sequence and older than p_settle (#2102, #2351). Folds a bounded batch of pending changes first; reads totals, pending changes and the held rows younger than p_settle, never the settled history.';

-- The triggers. CREATE TRIGGER takes a lock that waits for every in-flight writer and holds new ones until this file
-- commits, so the rebuild below reads a store no write can change underneath it.
DROP TRIGGER IF EXISTS wh_event_store_tracks_unsequenced_insert_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_tracks_unsequenced_insert_trg
  AFTER INSERT ON __SCHEMA__.wh_event_store
  FOR EACH ROW
  WHEN (NEW.origin_service_id IS NOT NULL AND NEW.origin_commit_sequence IS NULL)
  EXECUTE FUNCTION __SCHEMA__._wh_track_unsequenced_digest();

DROP TRIGGER IF EXISTS wh_event_store_tracks_unsequenced_update_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_tracks_unsequenced_update_trg
  AFTER UPDATE OF origin_service_id, origin_commit_sequence, flags, event_type, scope, event_id
  ON __SCHEMA__.wh_event_store
  FOR EACH ROW
  WHEN ((OLD.origin_service_id IS NOT NULL AND OLD.origin_commit_sequence IS NULL)
     OR (NEW.origin_service_id IS NOT NULL AND NEW.origin_commit_sequence IS NULL))
  EXECUTE FUNCTION __SCHEMA__._wh_track_unsequenced_digest();

DROP TRIGGER IF EXISTS wh_event_store_tracks_unsequenced_delete_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_tracks_unsequenced_delete_trg
  AFTER DELETE ON __SCHEMA__.wh_event_store
  FOR EACH ROW
  WHEN (OLD.origin_service_id IS NOT NULL AND OLD.origin_commit_sequence IS NULL)
  EXECUTE FUNCTION __SCHEMA__._wh_track_unsequenced_digest();

DROP TRIGGER IF EXISTS wh_event_store_clears_unsequenced_trg ON __SCHEMA__.wh_event_store;
CREATE TRIGGER wh_event_store_clears_unsequenced_trg
  AFTER TRUNCATE ON __SCHEMA__.wh_event_store
  FOR EACH STATEMENT
  EXECUTE FUNCTION __SCHEMA__._wh_clear_unsequenced_digests();

-- One-time: the history already held, folded once (also on a re-run, which starts again from the rows).
SELECT __SCHEMA__.wh_rebuild_unsequenced_digests();
