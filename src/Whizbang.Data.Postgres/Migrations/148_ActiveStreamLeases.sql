-- Migration: 148_ActiveStreamLeases
-- Date: 2026-09-09
-- Description: Acquiring a stream's rows leases the stream (#731). wh_active_streams.lease_expiry had never
--   been set by any acquisition function: the pinning INSERT of claim_orphaned_inbox (145), claim_orphaned_outbox
--   (115) and claim_orphaned_perspective_events (140) listed every column but lease_expiry, and the owner's
--   renewal UPDATE refreshed last_activity_at only. Every stream-level guard those functions carry (others_live:
--   never take a live sibling's stream; mine_owned: prefer and renew the streams this instance holds; refreshed:
--   renew rather than re-pin) compared lease_expiry against p_now and saw NULL, so all three were inert and a
--   stream could be interleaved across two instances the moment its owner had no row leased in it.
--   The three functions are reproduced verbatim from their last words with one delta each: the pinning INSERT
--   carries lease_expiry = p_lease_expiry, its ON CONFLICT branch sets the lease to follow the assignment, and
--   the renewal UPDATE renews the lease with the rows. renew_leases (029) is reproduced with one delta too: a
--   row renewal extends the stream lease of every stream whose assigned instance holds the row, so a long
--   handler keeps the whole stream and not only the row it is on. Signatures are unchanged.
-- Dependencies: 145_BoundedAcquisitionRewrite, 115_TagBoundCoalescing, 140_LockFreeDoorbellProbes, 029_ProcessWorkBatch, 007_CreateActiveStreamsTable
-- Objects: claim_orphaned_inbox, claim_orphaned_outbox, claim_orphaned_perspective_events, renew_leases

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_inbox: last word 145_BoundedAcquisitionRewrite.sql, plus the stream lease.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION, not just re-emission. Without a limit here this statement leases every
  -- eligible row in one shot and charges an attempt to each, so an instance restarting onto a large
  -- backlog claims the whole thing instantly. The rows it cannot dispatch inside the lease expire
  -- un-dispatched, get re-claimed here, spend another attempt, and eventually dead-letter as
  -- MaxAttemptsExceeded having never reached a receptor - the error branch below is this statement
  -- stamping its own casualties.
  --
  -- The caller's claim limit previously reached claim_orphaned_perspective_events but not this
  -- function, so both the adaptive claim window and the outstanding budget were throttling a valve
  -- downstream of the flood: p_max_streams bounds only the RE-EMISSION of work already held
  -- (claim_work's eligible_inbox filters on instance_id = p_instance_id). That is why narrowing the
  -- window changed the rate of lease saturation without ever converging.
  --
  -- LIMIT NULL is unlimited in Postgres, so the default preserves the old behavior for any caller
  -- that has not been taught to pass a bound.
  WITH params AS (
    -- BIGINT: the unbounded default is INT max and max_rows + 1 below must not overflow.
    SELECT COALESCE(p_max_rows, 2147483647)::BIGINT AS max_rows
  ),
  -- 145: ownership is a per-STREAM property. The two sets below are built ONCE per call from the small
  -- ledger tables and tested by hash membership. 138 evaluated the same predicate as correlated
  -- EXISTS probes on every pending inbox row, which is one of the two reasons the acquisition scan
  -- grew with the backlog instead of the batch (#714). "Live" keeps 025's meaning: a heartbeat within
  -- p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity.
  others_live AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    JOIN __SCHEMA__.wh_service_instances si ON si.instance_id = ast.assigned_instance_id
    WHERE ast.assigned_instance_id <> p_instance_id
      AND ast.lease_expiry > p_now
      AND (
        si.last_heartbeat_at >= p_stale_cutoff
        OR EXISTS (
          SELECT 1 FROM pg_stat_activity sa
          WHERE sa.application_name = 'whizbang-' || si.instance_id::text
        )
      )
  ),
  mine_owned AS MATERIALIZED (
    SELECT DISTINCT ast.stream_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
  ),
  -- 145 COMMAND LANE (#721): a command (is_event = FALSE) is a request someone is waiting on. Commands
  -- are picked first, in per-stream arrival order, before any event fills the rest of the batch, so one
  -- interactive request never queues behind a bulk fan-in it did not cause. The set of pending commands
  -- is small by nature and served by idx_inbox_pending_commands, so ranking it is bounded by the number
  -- of pending commands, not by the backlog.
  pick_commands AS (
    SELECT i.message_id AS cand_message_id,
           0 AS lane,
           ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq,
           i.received_at AS cand_received_at
    FROM __SCHEMA__.wh_inbox i
    WHERE i.processed_at IS NULL
      AND i.is_event = FALSE
      AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
  ),
  commands_taken AS (
    SELECT LEAST((SELECT count(*) FROM pick_commands), (SELECT max_rows FROM params))::INTEGER AS n
  ),
  -- 145 EVENT SELECTION, breadth-first with an early stop instead of ranking the whole backlog.
  -- The eligible streams are counted with an early stop at max_rows + 1 so the mode decision costs a
  -- bounded index walk. MANY streams (more than the batch can hold): walk arrival order and keep a row
  -- only when it is the first eligible row of its stream (one covering-index probe), stopping when the
  -- batch is full; the cost is proportional to rows scanned until the batch fills. FEW streams: take
  -- k = ceil(remaining / streams) rows per stream through LATERAL, which never ranks the set either.
  -- Both modes produce the 138 order, (stream_seq, received_at, message_id), so the drain sees the same
  -- breadth-first interleave it always has (#568), only cheaper.
  eligible_event_streams AS MATERIALIZED (
    SELECT DISTINCT i.stream_id
    FROM __SCHEMA__.wh_inbox i
    WHERE i.processed_at IS NULL
      AND i.is_event = TRUE
      AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
    LIMIT (SELECT max_rows + 1 FROM params)
  ),
  mode AS (
    SELECT (SELECT count(*) FROM eligible_event_streams) AS n_streams,
           GREATEST((SELECT max_rows FROM params) - (SELECT n FROM commands_taken), 0) AS remaining
  ),
  event_heads AS (
    SELECT i.message_id AS cand_message_id,
           1 AS lane,
           1::BIGINT AS stream_seq,
           i.received_at AS cand_received_at
    FROM __SCHEMA__.wh_inbox i
    WHERE (SELECT n_streams > remaining FROM mode)
      AND i.processed_at IS NULL
      AND i.is_event = TRUE
      AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
      AND (
        i.stream_id IN (SELECT stream_id FROM mine_owned)
        OR (
          (i.partition_number IS NULL
           OR (i.partition_number % p_active_instance_count) = p_instance_rank
           OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
          AND i.stream_id NOT IN (SELECT stream_id FROM others_live)
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_inbox j
        WHERE j.stream_id = i.stream_id
          AND j.processed_at IS NULL
          AND j.is_event = TRUE
          AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
          AND (j.instance_id IS NULL OR j.lease_expiry < p_now)
          AND (j.scheduled_for IS NULL OR j.scheduled_for <= p_now)
          AND (j.partition_number IS NULL
               OR (j.partition_number % p_active_instance_count) = p_instance_rank
               OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = j.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      )
    ORDER BY i.received_at, i.message_id
    LIMIT (SELECT remaining FROM mode)
  ),
  kk AS (
    SELECT GREATEST(1, CEIL(remaining::NUMERIC / GREATEST(n_streams, 1)))::INTEGER AS k FROM mode
  ),
  event_few AS (
    SELECT h.message_id AS cand_message_id,
           1 AS lane,
           h.rn AS stream_seq,
           h.received_at AS cand_received_at
    FROM eligible_event_streams s
    CROSS JOIN kk
    CROSS JOIN LATERAL (
      SELECT i.message_id, i.received_at,
             ROW_NUMBER() OVER (ORDER BY i.received_at, i.message_id) AS rn
      FROM __SCHEMA__.wh_inbox i
      WHERE i.stream_id = s.stream_id
        AND i.processed_at IS NULL
        AND i.is_event = TRUE
        AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
        AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
        AND (i.partition_number IS NULL
             OR (i.partition_number % p_active_instance_count) = p_instance_rank
             OR (p_allow_steal AND NOT EXISTS (
                 -- 145 (#725): never steal from a stream another live instance is mid-drain on; taking its next
                 -- row would interleave one stream across two instances and break per-stream order.
                 SELECT 1 FROM __SCHEMA__.wh_inbox l
                 WHERE l.stream_id = i.stream_id AND l.processed_at IS NULL
                   AND l.instance_id IS NOT NULL AND l.instance_id <> p_instance_id AND l.lease_expiry > p_now)))
      ORDER BY i.received_at, i.message_id
      LIMIT kk.k
    ) h
    WHERE (SELECT n_streams <= remaining FROM mode)
  ),
  pick AS (
    SELECT cand_message_id, lane, stream_seq, cand_received_at FROM pick_commands
    UNION ALL
    SELECT cand_message_id, lane, stream_seq, cand_received_at FROM event_heads
    UNION ALL
    SELECT cand_message_id, lane, stream_seq, cand_received_at FROM event_few
  ),
  candidates AS (
    -- Lock under breadth-first order. SKIP LOCKED skips rows a concurrent claimer holds, and
    -- the volatile predicates re-check under the lock - a row leased between pick and here is
    -- filtered exactly as the old single-level shape would have skipped it.
    SELECT i.message_id AS cand_message_id,
           pick.lane AS cand_lane,
           pick.stream_seq AS cand_stream_seq,
           pick.cand_received_at
    FROM __SCHEMA__.wh_inbox i
    JOIN pick ON pick.cand_message_id = i.message_id
    WHERE (i.instance_id IS NULL OR i.lease_expiry < p_now)
      AND i.processed_at IS NULL
    ORDER BY pick.lane, pick.stream_seq, pick.cand_received_at, pick.cand_message_id
    LIMIT (SELECT max_rows FROM params)
    FOR UPDATE OF i SKIP LOCKED
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_inbox i
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: claim_orphaned is the SOLE source of attempt counting.
        -- Bumps unconditionally on every claim (fresh or re-claim) so attempts = 1 means
        -- "first attempt has started" (one-based). process_inbox_failures records error and
        -- releases the lease but does NOT bump - the next claim's bump captures attempt N+1.
        -- Single-source removes the double-counting that two bumps per failed cycle would cause.
        -- Without this, hung handlers (no exception thrown, lease eventually expires) looked
        -- identical to fresh messages in a consumer's production environment - an extended stuck-message backlog with
        -- attempts=0 across thousands of rows (production audit).
        attempts = i.attempts + 1,
        -- Attribute the attempt this claim is REPLACING. process_inbox_failures records
        -- error/failure_reason only when dispatch reported a failure; when the process is killed
        -- mid-dispatch (SIGKILL from a failed liveness probe, container replaced, handler hung past
        -- its lease) nothing reports anything - the lease just expires and the bump above spends
        -- another attempt in silence. The budget then runs out and the row dead-letters as
        -- "MaxAttemptsExceeded: attempts=N > max=M", which describes the counter and not the cause.
        --
        -- Observed in production as ~54k inbox rows averaging 11 attempts, every one with
        -- error IS NULL and failure_reason = 99 (Unknown) - no way to tell a crash-looping host
        -- from a genuinely failing handler. Stamp the abandonment so the row carries its own
        -- history: an expired lease still held by an instance (instance_id IS NOT NULL) means the
        -- previous attempt ended without ever reporting. Guarded on error IS NULL so a real
        -- recorded failure is never papered over - that error is the better diagnostic.
        failure_reason = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 6  -- MessageFailureReason.LeaseExpired
          ELSE i.failure_reason
        END,
        error = CASE
          WHEN i.instance_id IS NOT NULL AND i.lease_expiry < p_now AND i.error IS NULL
          THEN 'Attempt ' || i.attempts || ' ended without a reported outcome: lease held by instance '
               || i.instance_id::text || ' expired at ' || i.lease_expiry::text
               || ' (process terminated mid-dispatch, or the handler outran its lease). '
               || 'No dispatch failure was recorded for that attempt.'
          ELSE i.error
        END
    -- Ownership and partition routing were resolved in `candidates` above, under a row lock. Only
    -- the two cheap invariants are re-asserted here, as defense against a lease that lapsed between
    -- selection and write:
    --
    --   OWNER PATH - a stream's live owner always claims its messages, ignoring partition modulo,
    --   which preserves per-stream FIFO and prevents the rank-churn wedge seen in production: when
    --   active_instance_count changes, modulo routing for a partition can shift to a different rank
    --   than the stream's existing owner. Without that branch the modulo-matched instance is blocked
    --   by the ownership NOT EXISTS and the owner is blocked by the modulo filter - neither claims.
    --
    --   UNOWNED / ABANDONED PATH - no live owner, so partition-based load balancing decides the rank.
    --   NULL partition_number (no stream binding) stays claimable by any rank. "Live" means a
    --   heartbeat within p_stale_cutoff OR a registered LISTEN connection in pg_stat_activity; an
    --   instance killed by SIGKILL holds no meaningful ownership, and without the recency clause its
    --   dead lease would block cross-instance claims for the full lease duration (300 s default).
    --   See 011 (cleanup_stale_instances) for the eventual DELETE - this claim-time check is what
    --   makes recovery happen at the stale threshold rather than at lease expiry.
    FROM candidates c
    WHERE i.message_id = c.cand_message_id
      AND i.processed_at IS NULL
      AND (i.instance_id IS NULL OR i.lease_expiry < p_now)
    RETURNING i.message_id AS c_message_id, i.stream_id AS c_stream_id, i.partition_number AS c_partition_number,
              c.cand_lane AS c_lane, c.cand_stream_seq AS c_stream_seq, c.cand_received_at AS c_received_at
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into REFRESH (row-only
  -- UPDATE for already-owned-with-live-lease streams) + PIN (INSERT...ON CONFLICT for
  -- the rare ownership-transition case, with ORDER BY stream_id for consistent lock
  -- acquisition). Symmetric with the fix in claim_orphaned_outbox (mig 024); see that
  -- migration for the full rationale. Eliminates the 40P01 deadlock observed in
  -- production (Whizbang PR #227).
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND c.c_stream_id IS NOT NULL
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, COALESCE(c.c_partition_number, 0) AS partition_number
      FROM claimed c
      WHERE c.c_stream_id IS NOT NULL
        AND NOT EXISTS (
          SELECT 1 FROM refreshed r WHERE r.refreshed_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO UPDATE
      SET last_activity_at = EXCLUDED.last_activity_at,
          assigned_instance_id = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.assigned_instance_id
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.assigned_instance_id
            ELSE ast.assigned_instance_id
          END,
          -- 148 (#731): the stream lease follows the assignment. Whoever the CASE above leaves as owner
          -- holds the lease: a stream this instance takes (unowned, orphaned by a deregistered instance,
          -- or already its own) is leased to p_lease_expiry; a stream another registered instance keeps
          -- is not touched.
          lease_expiry = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.lease_expiry
            WHEN ast.assigned_instance_id = EXCLUDED.assigned_instance_id THEN EXCLUDED.lease_expiry
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.lease_expiry
            ELSE ast.lease_expiry
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  -- 145: the result carries the pick order (commands first, then breadth-first). UPDATE ... RETURNING
  -- alone promises no order, and a caller that consumes the rows in order relies on the lane.
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c
  ORDER BY c.c_lane, c.c_stream_seq, c.c_received_at, c.c_message_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_inbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, BOOLEAN) IS
  'Acquires unowned/abandoned pending inbox rows for an instance, bounded by p_max_rows (see 025 for the ownership '
  'rationale, 138 for the total order). 145: ownership is decided per stream from two hashed sets built once per '
  'call 148: pinning a stream leases it (lease_expiry = p_lease_expiry) and the owner''s renewal renews it, so the stream-level guards are live (#731).';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_outbox: last word 115_TagBoundCoalescing.sql, plus the stream lease.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION — see claim_orphaned_inbox (mig 025) for the full rationale. Unbounded, this
  -- statement leases every eligible row at once and charges an attempt to each, so the caller's
  -- claim limit governs only what comes back out, never how much gets taken. LIMIT NULL is
  -- unlimited in Postgres, so the default preserves the previous behavior for untaught callers.
  WITH candidates AS (
    -- Full predicate, under a row lock: selecting by age and filtering for ownership afterwards
    -- would let another instance's rows permanently fill this instance's window.
    SELECT o.message_id AS cand_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
      AND (
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = o.stream_id
            AND ast.assigned_instance_id = p_instance_id
            AND ast.lease_expiry > p_now
        )
        OR
        (
          (o.partition_number IS NULL
           OR (o.partition_number % p_active_instance_count) = p_instance_rank)
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = o.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND ast.lease_expiry > p_now
              AND EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_service_instances si
                WHERE si.instance_id = ast.assigned_instance_id
                  AND (
                    si.last_heartbeat_at >= p_stale_cutoff
                    OR EXISTS (
                      SELECT 1 FROM pg_stat_activity sa
                      WHERE sa.application_name = 'whizbang-' || si.instance_id::text
                    )
                  )
              )
          )
        )
      )
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_outbox earlier
        WHERE earlier.stream_id = o.stream_id
          AND earlier.created_at < o.created_at
          AND earlier.scheduled_for IS NOT NULL
          AND earlier.scheduled_for > p_now
          AND earlier.processed_at IS NULL
          -- #668 H3: a coalesce-pending row parks with scheduled_for = created + MaxDelay,
          -- which made it an 'earlier scheduled row' that blocked EVERY later row on the
          -- same stream from claiming for the whole window — re-armed by each new coalesce
          -- row, i.e. permanently under sustained ingest. Coalesce rows are not deferred
          -- deliveries; they are parked for folding and must not gate their stream.
          AND earlier.coalesce_group IS NULL
      )
    ORDER BY o.created_at
    LIMIT p_max_rows
    FOR UPDATE OF o SKIP LOCKED
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = o.attempts + 1
    FROM candidates c
    WHERE o.message_id = c.cand_message_id
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL  -- 115: coalesce-pending singles are never leased by the pump
    -- Ownership, partition routing and the stream-ordering check were all resolved in `candidates`
    -- above, under a row lock. Only the cheap invariants are re-asserted here, against a lease that
    -- lapsed between selection and write. The predicate deliberately lives in ONE place: two copies
    -- of a 40-line ownership rule is exactly the kind of pair that drifts.
    --
    --   OWNER PATH — a stream's live owner always claims its messages, partition modulo ignored,
    --   which prevents the rank-churn wedge described in migration 025.
    --   UNOWNED / ABANDONED PATH — partition-based load balancing for streams with no live owner;
    --   "live" means a fresh heartbeat OR a registered LISTEN connection in pg_stat_activity.
    --   STREAM ORDERING — an earlier message in the same stream awaiting a future retry blocks the
    --   later ones, so per-stream order survives a scheduled retry.
    RETURNING o.message_id AS c_message_id, o.stream_id AS c_stream_id, o.partition_number AS c_partition_number
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into two paths to
  -- eliminate the unique-index leaf-page deadlock observed in production under N pods ×
  -- 250 ms polling. See Whizbang PR #227 for the full diagnosis.
  --
  -- REFRESH path (steady-state, >99% of claims under load): if this instance already
  -- owns the stream with a live lease, the prior UPSERT was wasted work that only
  -- bumped last_activity_at on a row we already owned, yet still took the unique-index
  -- leaf-page lock on each INSERT...ON CONFLICT. A plain row UPDATE achieves the same
  -- semantic (refresh last_activity_at) without touching the unique-index INSERT path
  -- at all → no leaf-page contention, no deadlock possible on this code path.
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND c.c_stream_id IS NOT NULL
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  -- PIN path (rare): only fires for streams NOT covered by REFRESH — first-time pinning
  -- (producer-side strategy-flush left assigned_instance_id NULL), abandoned-owner
  -- reassignment, or orphan-claim transferring ownership across instances. ORDER BY
  -- stream_id forces concurrent pods to acquire the unique-index leaf-page locks in a
  -- consistent order, which prevents lock-cycle deadlocks on this remaining path as
  -- well (lock-ordering precludes cycle formation).
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, COALESCE(c.c_partition_number, 0) AS partition_number
      FROM claimed c
      WHERE c.c_stream_id IS NOT NULL
        AND NOT EXISTS (
          SELECT 1 FROM refreshed r WHERE r.refreshed_stream_id = c.c_stream_id
        )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO UPDATE
      SET last_activity_at = EXCLUDED.last_activity_at,
          assigned_instance_id = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.assigned_instance_id
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.assigned_instance_id
            ELSE ast.assigned_instance_id
          END,
          -- 148 (#731): the stream lease follows the assignment. Whoever the CASE above leaves as owner
          -- holds the lease: a stream this instance takes (unowned, orphaned by a deregistered instance,
          -- or already its own) is leased to p_lease_expiry; a stream another registered instance keeps
          -- is not touched.
          lease_expiry = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.lease_expiry
            WHEN ast.assigned_instance_id = EXCLUDED.assigned_instance_id THEN EXCLUDED.lease_expiry
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.lease_expiry
            ELSE ast.lease_expiry
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER) IS
  'Acquires unowned/abandoned pending outbox rows for an instance (see 024 for the ownership rationale, 115 for the '
  'coalesce-aware selection). 148: pinning a stream leases it (lease_expiry = p_lease_expiry) and the owner''s renewal '
  'renews it, so the stream-level guards are live (#731).';

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_perspective_events: last word 140_LockFreeDoorbellProbes.sql, plus the stream lease.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_perspective_events(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_max_streams INTEGER DEFAULT 500,
  p_instance_rank INTEGER DEFAULT 0,
  p_active_instance_count INTEGER DEFAULT 1
) RETURNS TABLE(
  event_work_id UUID,
  stream_id UUID,
  perspective_name VARCHAR(200)
) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  WITH claimable_events AS (
    -- Find all events eligible for claiming (orphaned or unleased)
    SELECT
      pe.event_work_id,
      pe.stream_id,
      pe.perspective_name,
      pe.event_id,
      pe.partition_number
    FROM __SCHEMA__.wh_perspective_events pe
    WHERE (pe.instance_id IS NULL OR pe.lease_expiry < p_now)
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND pe.processed_at IS NULL
      -- Phase H step 6 slice 2: stream ownership now combines wh_active_streams pinning
      -- (OWNER PATH — always wins) with partition-modulo load balancing for unowned streams
      -- (UNOWNED PATH — symmetric with claim_orphaned_outbox / _inbox).
      AND (
        -- OWNER PATH — wh_active_streams says this instance owns the stream. Always claim.
        EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_active_streams ast
          WHERE ast.stream_id = pe.stream_id
            AND ast.assigned_instance_id = p_instance_id
        )
        OR
        -- UNOWNED / ABANDONED PATH — partition-modulo selection for streams with no live owner.
        (
          (pe.partition_number % p_active_instance_count) = p_instance_rank
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_active_streams ast
            WHERE ast.stream_id = pe.stream_id
              AND ast.assigned_instance_id != p_instance_id
              AND (
                -- Existing check: a row in wh_service_instances counts as alive.
                -- This is removed by cleanup_stale_instances at the stale threshold.
                EXISTS (
                  SELECT 1 FROM __SCHEMA__.wh_service_instances si
                  WHERE si.instance_id = ast.assigned_instance_id
                )
                -- Slice 2b of zero-idle-polling — additive defensive predicate:
                -- a pod with a live Whizbang LISTEN connection in pg_stat_activity
                -- counts as alive even if its wh_service_instances row has been
                -- cleaned up (transient state during pod restart, race between
                -- cleanup_stale_instances and the pod opening its LISTEN
                -- connection on next boot). Strictly additive — only adds
                -- protection against premature orphan-claim, never loosens.
                OR EXISTS (
                  SELECT 1 FROM pg_stat_activity sa
                  WHERE sa.application_name = 'whizbang-' || ast.assigned_instance_id::text
                )
              )
          )
        )
      )
      -- Ensure ordering - no earlier uncompleted events in same perspective
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events earlier
        WHERE earlier.stream_id = pe.stream_id
          AND earlier.perspective_name = pe.perspective_name
          AND earlier.event_id < pe.event_id
          AND (
            (earlier.instance_id IS NOT NULL AND earlier.lease_expiry > p_now)
            OR (earlier.scheduled_for > p_now)
          )
      )
  ),
  -- Select up to p_max_streams distinct streams from claimable events
  selected_streams AS (
    SELECT DISTINCT ce.stream_id
    FROM claimable_events ce
    LIMIT p_max_streams
  ),
  -- 140: lock the selected streams' rows with SKIP LOCKED before leasing them (issue #699), the
  -- shape claim_orphaned_inbox and claim_orphaned_outbox already use. A row another session holds
  -- is a row being completed or leased; waiting for it stalled the whole claim tick behind one
  -- commit batch. The lock is scoped to the selected streams, never the full eligible backlog.
  locked AS (
    SELECT pe.event_work_id
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN claimable_events ce ON ce.event_work_id = pe.event_work_id
    INNER JOIN selected_streams ss ON ce.stream_id = ss.stream_id
    FOR UPDATE OF pe SKIP LOCKED
  ),
  -- Claim ALL events for selected streams (full-stream capture)
  claimed AS (
    UPDATE __SCHEMA__.wh_perspective_events pe
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = pe.attempts + 1
    FROM locked l
    WHERE pe.event_work_id = l.event_work_id
    RETURNING pe.event_work_id AS c_event_work_id, pe.stream_id AS c_stream_id, pe.perspective_name AS c_perspective_name, pe.partition_number AS c_partition_number
  ),
  -- 2026-06-02: split the wh_active_streams ledger maintenance into REFRESH (row-only
  -- UPDATE for already-owned-with-live-lease streams) + PIN (INSERT...ON CONFLICT for
  -- the rare ownership-transition case, with ORDER BY stream_id for consistent lock
  -- acquisition). Symmetric with the fix in claim_orphaned_outbox (mig 024); see that
  -- migration for the full rationale. Eliminates the 40P01 deadlock observed in
  -- production (Whizbang PR #227).
  refreshed AS (
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = p_now,
        lease_expiry = p_lease_expiry
    FROM claimed c
    WHERE ast.stream_id = c.c_stream_id
      AND ast.assigned_instance_id = p_instance_id
      AND ast.lease_expiry > p_now
    RETURNING ast.stream_id AS refreshed_stream_id
  ),
  pinned AS (
    INSERT INTO __SCHEMA__.wh_active_streams AS ast
      (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now, p_lease_expiry
    FROM (
      SELECT c.c_stream_id AS stream_id, c.c_partition_number AS partition_number
      FROM claimed c
      WHERE NOT EXISTS (
        SELECT 1 FROM refreshed r WHERE r.refreshed_stream_id = c.c_stream_id
      )
    ) sub
    ORDER BY sub.stream_id
    ON CONFLICT (stream_id) DO UPDATE
      SET last_activity_at = EXCLUDED.last_activity_at,
          assigned_instance_id = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.assigned_instance_id
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.assigned_instance_id
            ELSE ast.assigned_instance_id
          END,
          -- 148 (#731): the stream lease follows the assignment. Whoever the CASE above leaves as owner
          -- holds the lease: a stream this instance takes (unowned, orphaned by a deregistered instance,
          -- or already its own) is leased to p_lease_expiry; a stream another registered instance keeps
          -- is not touched.
          lease_expiry = CASE
            WHEN ast.assigned_instance_id IS NULL THEN EXCLUDED.lease_expiry
            WHEN ast.assigned_instance_id = EXCLUDED.assigned_instance_id THEN EXCLUDED.lease_expiry
            WHEN NOT EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            ) THEN EXCLUDED.lease_expiry
            ELSE ast.lease_expiry
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_event_work_id AS event_work_id, c.c_stream_id AS stream_id, c.c_perspective_name AS perspective_name FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_perspective_events(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
  'Acquires unowned/abandoned pending perspective events for an instance, by stream (see 027 for the ownership '
  'rationale, 140 for the per-stream monotonic gate). 148: pinning a stream leases it (lease_expiry = p_lease_expiry) '
  'and the owner''s renewal renews it, so the stream-level guards are live (#731).';

-- ---------------------------------------------------------------------------------------------
-- renew_leases: last word 029_ProcessWorkBatch.sql, plus the stream lease.
-- A row renewal extends the stream lease of every stream whose assigned instance is the row's lease
-- holder, so a long handler keeps the whole stream and not only the row it is on. Rows are updated
-- before streams, the same order the acquisition functions lock in, so a renewal and a claim on the
-- same stream never wait on each other in opposite orders. The stream update is left out of the
-- returned count: rows-affected stays the function's contract.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__.renew_leases(
  p_category TEXT,
  p_ids UUID[],
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS INTEGER AS $$
DECLARE
  v_new_expiry TIMESTAMPTZ := NOW() + (p_lease_seconds || ' seconds')::INTERVAL;
  v_updated INTEGER;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  CASE p_category
    WHEN 'outbox' THEN
      UPDATE __SCHEMA__.wh_outbox
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM __SCHEMA__.wh_outbox o
        WHERE o.message_id = ANY(p_ids)
          AND o.processed_at IS NULL
          AND o.stream_id = ast.stream_id
          AND o.instance_id = ast.assigned_instance_id;
    WHEN 'inbox' THEN
      UPDATE __SCHEMA__.wh_inbox
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM __SCHEMA__.wh_inbox i
        WHERE i.message_id = ANY(p_ids)
          AND i.processed_at IS NULL
          AND i.stream_id = ast.stream_id
          AND i.instance_id = ast.assigned_instance_id;
    WHEN 'perspective_event' THEN
      UPDATE __SCHEMA__.wh_perspective_events
        SET lease_expiry = v_new_expiry
        WHERE event_work_id = ANY(p_ids)
          AND processed_at IS NULL;
      GET DIAGNOSTICS v_updated = ROW_COUNT;
      UPDATE __SCHEMA__.wh_active_streams ast
        SET lease_expiry = v_new_expiry
        FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.event_work_id = ANY(p_ids)
          AND pe.processed_at IS NULL
          AND pe.stream_id = ast.stream_id
          AND pe.instance_id = ast.assigned_instance_id;
    ELSE
      RAISE EXCEPTION 'renew_leases: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;

  RETURN v_updated;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.renew_leases(TEXT, UUID[], INTEGER) IS
  'Batched lease extension per category. UPDATEs lease_expiry to NOW() + p_lease_seconds for the supplied ids in the '
  'chosen category table, only for rows that are not yet processed, and extends wh_active_streams.lease_expiry for '
  'every stream whose assigned instance holds one of those rows (148, #731). Returns rows-affected for the category '
  'table. Called by C# LeaseRenewalWorker when in-flight items approach lease/3 from expiry.';
