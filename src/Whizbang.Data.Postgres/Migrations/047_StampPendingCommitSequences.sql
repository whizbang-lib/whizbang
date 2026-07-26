-- Migration: 047_StampPendingCommitSequences.sql
-- Date: 2026-05-23
-- Description: Slice 26 step 2 — the stamper SQL function. Allocates commit_sequence
--              values from wh_commit_seq for rows whose inserting transaction is
--              provably committed AND no older concurrent transaction could still
--              commit (the pg_snapshot_xmin barrier). Stamping order = xmin order,
--              which is monotonic with commit-completion order on the subset of
--              rows the stamper can see.
-- Dependencies: 046 (wh_commit_seq sequence, wh_event_store.commit_sequence column).

SELECT __SCHEMA__.drop_all_overloads('stamp_pending_commit_sequences');

CREATE OR REPLACE FUNCTION __SCHEMA__.stamp_pending_commit_sequences(
  p_batch_size INTEGER DEFAULT 1000
) RETURNS INTEGER AS $$
DECLARE
  v_stamped_count INTEGER;
BEGIN
  -- The barrier: pg_snapshot_xmin(pg_current_snapshot()) is the lowest xmin among
  -- currently-active transactions. Any row whose inserting xmin is strictly less than
  -- that is provably done — committed (visible to us, since MVCC says so) or aborted
  -- (invisible). For our stamping purposes, only visible rows match the predicate, so
  -- "xmin < pg_snapshot_xmin AND row visible" = "committed and stable".
  --
  -- Critically: pg_snapshot_xmin CANNOT advance past T2's xmin while T1 (with lower
  -- xmin) is still in flight. So if T1 holds a tx open with xmin=X1, neither T1's
  -- inserts NOR T2's inserts (with xmin > X1) become stampable until T1 resolves.
  -- This is what closes the commit-order race seen in a production run: T2's row is
  -- deferred until T1 is also resolved, then both get stamped in xmin order.
  --
  -- FOR UPDATE SKIP LOCKED: concurrent stamper callers partition the work — one
  -- locks rows the other skips. Each row gets stamped exactly once. Singleton-stamper
  -- pattern at the C# layer (pg_try_advisory_lock) ensures only one stamper actively
  -- works at a time, but this SQL is safe under concurrent invocation regardless.
  --
  -- ORDER BY xmin: within a batch, lower xmin gets lower commit_sequence. xmin is a
  -- valid total ordering on the provably-committed set (both rows are past the
  -- barrier, so their xmin relation is stable). This is the property downstream
  -- consumers rely on for monotonic cursor advancement.
  --
  -- Note on type compatibility: xmin is a system column of type `xid` (32-bit).
  -- pg_snapshot_xmin returns `xid8` (64-bit, epoch-aware). Cast xmin through text
  -- to xid8 for the comparison. This is safe within a single wraparound epoch;
  -- autovacuum freezing prevents wrap-around issues in practice.
  WITH eligible AS (
    SELECT event_id, xmin::text::xid8 AS xmin8
    FROM __SCHEMA__.wh_event_store
    WHERE commit_sequence IS NULL
      AND xmin::text::xid8 < pg_snapshot_xmin(pg_current_snapshot())
    ORDER BY xmin::text::xid8
    LIMIT p_batch_size
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_event_store es
  SET commit_sequence = nextval('__SCHEMA__.wh_commit_seq')
  FROM eligible e
  WHERE es.event_id = e.event_id;

  GET DIAGNOSTICS v_stamped_count = ROW_COUNT;
  RETURN v_stamped_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.stamp_pending_commit_sequences IS
'Slice 26 — stamps commit_sequence on event_store rows whose inserting tx is provably committed AND past the pg_snapshot_xmin barrier (no older concurrent tx could still commit). Allocates from wh_commit_seq. Order = xmin order = monotonic with commit-completion order on the visible set. FOR UPDATE SKIP LOCKED makes the function safe under concurrent invocation; the singleton stamper pattern at the C# layer (pg_try_advisory_lock) keeps allocation contention low. Returns count of rows stamped.';
