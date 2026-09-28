-- Migration: 171_OutboxStreamRuns.sql
-- Date: 2026-09-27
-- Description: An outbox claim leases a RUN of a stream's consecutive rows, not one row per stream.
--
--              Measured on a deployed service, an outbox backlog drained at about one row per stream
--              per claim cycle, and a cycle took about two seconds. A job produced tens of
--              thousands of events onto a few dozen streams of several hundred rows each; the
--              backlog took 7 to 14 minutes to drain while the database and the broker sat idle.
--              Drain rate tracked the number of streams that still had rows (a hundred streams gave
--              ~70 rows/s, twenty gave ~10, the tail of a few long streams ~5), never the backlog
--              size or the claim settings, so the drain time of a backlog was its longest stream's
--              row count times the cycle.
--
--              The mechanism, from the code:
--                * claim_orphaned_outbox (148) leases the oldest pending rows by arrival, bounded by
--                  a ROW count, and claim_work (170) hands it p_max_streams as that count: the claim
--                  loop's adaptive STREAM window, which starts at its floor of 25 and grows only on
--                  inbox evidence. The oldest 25 rows of an interleaved backlog are one row on each
--                  of 25 streams, so every claim leased about one row per stream (completions arrived
--                  in multiples of 25).
--                * the outbox re-offer returned held ROWS, p_max_streams of them, so a stream holding
--                  many rows crowded every other held stream out of the drain's work list.
--                * nothing told the claim loop a claim had filled its bound, so a full claim spaced
--                  out like a re-offer instead of claiming again.
--
--              What changes:
--                * claim_orphaned_outbox takes a run length. The oldest pending rows still choose
--                  WHICH streams move (the same bounded arrival walk, capped at p_max_heads), and each
--                  chosen stream then leases up to a run of its next consecutive rows, as far as the
--                  row bound allows: an even share of the bound per stream, never more than the run
--                  length, cut round-robin so every chosen stream keeps its head. A run is a PREFIX:
--                  it stops at the first row it may not take (another instance's live lease, a retry
--                  deferred into the future) and at the first row another session holds locked, so
--                  a stream is never leased with a gap. Ordering holds because one instance holds the
--                  stream lease for the whole run and the drain publishes a stream's rows in order.
--                * claim_work takes p_max_outbox_rows (the outbox row bound, independent of the
--                  stream window) and p_outbox_run_length. Both default to NULL, which is the previous
--                  behavior exactly: bound = p_max_streams, run = 1.
--                * the outbox re-offer returns one row per held stream (its most urgent held row), a
--                  batch of STREAMS, walked from at most a row bound of held rows.
--                * claim_work raises NOTICE 'whizbang.outbox_acquisition_full=true' when outbox
--                  acquisition leased its whole row bound, so the claim loop can claim again at once.
--                * wh_continue_outbox_streams (new) lets the drain continue a stream from the lease
--                  it already holds: it leases the stream's next run and returns the rows after the
--                  last one the drain published, without waiting for the next claim cycle.
--                * process_outbox_failures releases the rest of a failed row's run: rows of the same
--                  stream after it, still leased to the same holder, go back unowned with the attempt
--                  the claim charged refunded. The failed row is deferred into the future, and the
--                  acquisition's stream-ordering rule (an earlier deferred row blocks the later ones)
--                  holds the rest behind it, so the stream is retried in order. Before this, the rest
--                  stayed leased and the next drain published them ahead of the retry.
--
--              Cost. Every new access path is priced by the batch, never the backlog
--              (ai-docs/load-under-bulk-import.md):
--                * the run walk is one range scan per chosen stream on idx_outbox_stream_run below,
--                  keyed (stream_id, created_at, message_id), which is the walk's own order, so the
--                  LIMIT stops it inside the index. It reads at most the stream's share plus the rows
--                  this instance already holds in that stream (skipped by an index filter on INCLUDEd
--                  columns). Measured with the plan below: an Index Only Scan with Index Cond
--                  stream_id = $, no Sort, Limit above it.
--                * the continuation reads the same way, per stream the drain names.
--                * the failure release is one range scan per failed row on the same index.
--                * the re-offer walks at most max(stream window, outbox row bound) held rows, the
--                  same Sort + Limit shape 166 measured, and folds them to streams.
--              idx_outbox_stream_run REPLACES idx_outbox_stream_unpublished (160) rather than joining
--              it: same partial predicate, same leading key, scheduled_for still INCLUDEd, so the
--              store's emptiness probe (stream_id = $ ... LIMIT 1) is still one descent. The outbox
--              is the hottest write path during a bulk load and its index count does not grow.
--
-- Dependencies: 170 (last definition of claim_work, copied verbatim with the delta), 148 (last
--               definition of claim_orphaned_outbox), 156 (last definition of
--               process_outbox_failures), 151 (fetch_outbox_batch, whose projection the continuation
--               returns), 160 (idx_outbox_stream_unpublished, replaced here)
-- Objects: idx_outbox_stream_run, idx_outbox_stream_unpublished, claim_orphaned_outbox,
--          process_outbox_failures, wh_continue_outbox_streams, claim_work
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- The run index. Keyed in the walk's order so a run is an early-stopping range scan; partial on
-- the same predicate as idx_outbox_stream_unpublished so it serves that index's probe too.
-- ---------------------------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS idx_outbox_stream_run
  ON __SCHEMA__.wh_outbox (stream_id, created_at, message_id)
  INCLUDE (scheduled_for, instance_id, lease_expiry, coalesce_group)
  WHERE processed_at IS NULL AND published_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_outbox_stream_run IS
  'Pending unpublished outbox rows of a stream in arrival order (171). The claim leases a run of a '
  'stream''s consecutive rows and the drain continues a stream from its lease; both walk this index '
  'from the stream''s first entry and stop at the run, reading lease and schedule from INCLUDEd '
  'columns. Replaces idx_outbox_stream_unpublished (160): same predicate and leading key, so the '
  'store''s emptiness probe is still one descent, and the outbox keeps its index count.';

DROP INDEX IF EXISTS __SCHEMA__.idx_outbox_stream_unpublished;

-- ---------------------------------------------------------------------------------------------
-- claim_orphaned_outbox: last word 148_ActiveStreamLeases.sql, plus the run.
-- ---------------------------------------------------------------------------------------------
-- The arity changes (two trailing defaulted parameters), so every overload is cleared first.
SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_outbox');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimOrphanedOutbox_RunStopsAtTheFirstRowItMayNotTakeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ClaimOrphanedOutbox_FirstClaim_BumpsToOneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(
  p_instance_id UUID,
  p_instance_rank INTEGER,
  p_active_instance_count INTEGER,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_stale_cutoff TIMESTAMPTZ,
  p_max_rows INTEGER DEFAULT NULL,
  -- 171: how many consecutive rows one stream may lease in one call. 1 is the previous behavior.
  p_run_length INTEGER DEFAULT 1,
  -- 171: how many of the oldest rows choose the streams that move. NULL means p_max_rows, the
  -- previous behavior; claim_work passes its stream window.
  p_max_heads INTEGER DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID
) AS $$
#variable_conflict use_column
DECLARE
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  -- LEAST ignores a NULL, so a NULL head cap falls back to the row bound, and a NULL row bound (an
  -- untaught caller) stays unlimited exactly as before.
  v_heads INTEGER := LEAST(p_max_heads, p_max_rows);
BEGIN
  RETURN QUERY
  -- Bound ACQUISITION — see claim_orphaned_inbox (mig 025) for the full rationale. Unbounded, this
  -- statement leases every eligible row at once and charges an attempt to each, so the caller's
  -- claim limit governs only what comes back out, never how much gets taken. LIMIT NULL is
  -- unlimited in Postgres, so the default preserves the previous behavior for untaught callers.
  WITH candidates AS (
    -- The HEADS: the oldest claimable rows, which choose the streams that move this call. Full
    -- predicate, under a row lock: selecting by age and filtering for ownership afterwards would let
    -- another instance's rows permanently fill this instance's window. (148, verbatim, plus the two
    -- columns the run needs.)
    SELECT o.message_id AS cand_message_id, o.stream_id AS cand_stream_id, o.created_at AS cand_created_at
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
                      WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || si.instance_id::text
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
    LIMIT v_heads
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- 171: the streams whose runs extend past their heads. A NULL or empty stream id is a singleton
  -- (the drain looks it up by message id), so it has no run to extend. With a run of 1 there is
  -- nothing to extend either, and the walk below never executes.
  run_streams AS (
    SELECT DISTINCT c.cand_stream_id AS run_stream_id
    FROM candidates c
    WHERE v_run > 1
      AND c.cand_stream_id IS NOT NULL
      AND c.cand_stream_id <> __EMPTY_UUID__::uuid
  ),
  -- Each stream's share of the row bound: the run length, or an even split of the bound when the
  -- chosen streams cannot all have a full run. Walking only the share is what keeps the walk priced
  -- by the batch: streams x share is about the row bound, never streams x run length.
  run_share AS (
    SELECT CASE
             WHEN p_max_rows IS NULL THEN v_run
             ELSE LEAST(v_run, GREATEST(1, CEIL(p_max_rows::NUMERIC / GREATEST(count(*), 1))::INTEGER))
           END AS share
    FROM run_streams
  ),
  run_walk AS (
    SELECT w.w_message_id, w.w_stream_id, w.w_created_at, w.w_takeable, w.w_pos
    FROM run_streams rs
    CROSS JOIN LATERAL (
      -- The stream's pending rows from its first entry, in its own order, skipping the ones this
      -- instance already holds under a live lease (they are its own run already, in flight). One
      -- range scan on idx_outbox_stream_run: Index Cond stream_id = rs.run_stream_id, the ORDER BY
      -- is the index order, the lease test is an index filter on INCLUDEd columns, and the LIMIT
      -- stops the scan at the share.
      SELECT o.message_id AS w_message_id,
             o.stream_id AS w_stream_id,
             o.created_at AS w_created_at,
             -- COALESCEd because bool_and below skips a NULL, which would read as "not blocking".
             COALESCE((o.instance_id IS NULL OR o.lease_expiry < p_now)
               AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now), FALSE) AS w_takeable,
             ROW_NUMBER() OVER (ORDER BY o.created_at, o.message_id) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = rs.run_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND (o.instance_id IS DISTINCT FROM p_instance_id
             OR o.lease_expiry IS NULL
             OR o.lease_expiry <= p_now)
      ORDER BY o.created_at, o.message_id
      LIMIT (SELECT share FROM run_share)
    ) w
  ),
  -- A run is a prefix: it ends at the first row this instance may not take -- a row another
  -- instance holds under a live lease, or a retry deferred into the future. Taking a row beyond it
  -- would publish past a row that has not been published.
  run_open AS (
    SELECT rw.*, bool_and(rw.w_takeable) OVER (PARTITION BY rw.w_stream_id ORDER BY rw.w_pos) AS w_open
    FROM run_walk rw
  ),
  run_locked AS (
    SELECT o.message_id AS locked_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id IN (SELECT ro.w_message_id FROM run_open ro WHERE ro.w_open)
    FOR UPDATE OF o SKIP LOCKED
  ),
  -- ...and at the first row another session holds locked, for the same reason: a lock skipped in
  -- the middle of a run would leave a gap in it.
  run_first_gap AS (
    SELECT ro.w_stream_id AS gap_stream_id, MIN(ro.w_pos) AS gap_pos
    FROM run_open ro
    WHERE ro.w_open
      AND NOT EXISTS (SELECT 1 FROM run_locked rl WHERE rl.locked_message_id = ro.w_message_id)
    GROUP BY ro.w_stream_id
  ),
  run_rows AS (
    SELECT ro.w_message_id, ro.w_pos, ro.w_created_at
    FROM run_open ro
    LEFT JOIN run_first_gap g ON g.gap_stream_id = ro.w_stream_id
    WHERE ro.w_open
      AND (g.gap_pos IS NULL OR ro.w_pos < g.gap_pos)
  ),
  -- The heads always, then run rows round-robin by position (every stream's 2nd row before any
  -- stream's 3rd), cut at the row bound. Cutting by position keeps each stream's rows a prefix.
  chosen AS (
    SELECT u.chosen_message_id
    FROM (
      SELECT a.chosen_message_id, MIN(a.chosen_pos) AS chosen_pos, MIN(a.chosen_created_at) AS chosen_created_at
      FROM (
        SELECT c.cand_message_id AS chosen_message_id, 0::BIGINT AS chosen_pos, c.cand_created_at AS chosen_created_at
        FROM candidates c
        UNION ALL
        SELECT r.w_message_id, r.w_pos, r.w_created_at
        FROM run_rows r
      ) a
      GROUP BY a.chosen_message_id
    ) u
    ORDER BY u.chosen_pos, u.chosen_created_at, u.chosen_message_id
    LIMIT p_max_rows
  ),
  claimed AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = p_lease_expiry,
        -- Phase H step 8 slice D: see claim_orphaned_inbox (mig 025). Single-source
        -- attempt counting; first claim → 1, every re-claim bumps; failures don't bump.
        attempts = o.attempts + 1
    FROM chosen c
    WHERE o.message_id = c.chosen_message_id
      AND (o.instance_id IS NULL OR o.lease_expiry < p_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL  -- 115: coalesce-pending singles are never leased by the pump
    -- Ownership, partition routing and the stream-ordering check were all resolved in `candidates`
    -- above, under a row lock, and a run shares its head's stream and so its ownership. Only the
    -- cheap invariants are re-asserted here, against a lease that lapsed between selection and write.
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

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox(UUID, INTEGER, INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER, TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
  'Acquires unowned/abandoned pending outbox rows for an instance (see 024 for the ownership rationale, 115 for the '
  'coalesce-aware selection, 148 for the stream lease). 171: the oldest claimable rows (at most p_max_heads) choose '
  'the streams that move, and each leases up to p_run_length of its next consecutive rows as a prefix, within '
  'p_max_rows in total; a run of 1 is the previous behavior.';

-- ---------------------------------------------------------------------------------------------
-- process_outbox_failures: last word 156_OutboxInboxFailureElementNames.sql, plus the release of
-- the failed row's run. Signature unchanged.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ProcessOutboxFailures_MidRun_ReleasesTheRestOfTheRunToBeRetriedInOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxInboxFailureReasonSqlTests.cs:OutboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAttemptsIncrementSqlTests.cs:ProcessOutboxFailures_DoesNotDoubleCountAttemptsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_outbox_failures(
  p_failures JSONB,
  p_now TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
  v_failure RECORD;
  v_schedule_id TEXT;
  v_delivery INTEGER;
  -- 171: where the failed row sat, so the rest of its run can be released behind it.
  v_holder UUID;
  v_stream UUID;
  v_created TIMESTAMPTZ;
BEGIN
  IF jsonb_array_length(p_failures) = 0 THEN RETURN; END IF;

  FOR v_failure IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      (elem->>'CompletedStatus')::INTEGER as status_flags,
      elem->>'Error' as error_message,
      COALESCE(elem->>'Reason', elem->>'FailureReason')::INTEGER as failure_reason
    FROM jsonb_array_elements(p_failures) as elem
  LOOP
    SELECT o.metadata->>'scheduleId', COALESCE((o.metadata->>'deliveryGuarantee')::INTEGER, 0),
           o.instance_id, o.stream_id, o.created_at
    INTO v_schedule_id, v_delivery, v_holder, v_stream, v_created
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = v_failure.msg_id;

    IF v_schedule_id IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_schedule_runs
        (schedule_id, occurrence_id, fired_at, status, error_message)
      VALUES (v_schedule_id::UUID, v_failure.msg_id, p_now, 1, v_failure.error_message);
    END IF;

    IF v_schedule_id IS NOT NULL AND v_delivery = 1 THEN
      -- AT-MOST-ONCE (delivery_guarantee = 1): never redeliver. Parked terminally; the failure is
      -- durably recorded in wh_schedule_runs above, which is what makes this safe.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),
          scheduled_for = 'infinity'::TIMESTAMPTZ,             -- terminal: no retry, ever
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;
    ELSE
      -- Default (at-least-once, and every non-schedule message): retry with backoff. Attempts are
      -- counted by claim_orphaned_outbox alone, so the backoff reads o.attempts as it stands.
      UPDATE __SCHEMA__.wh_outbox o
      SET status = o.status | v_failure.status_flags | 32768,  -- Set Failed bit (32768)
          error = v_failure.error_message,
          failure_reason = COALESCE(v_failure.failure_reason, 0),  -- Default to Unknown (0)
          -- Exponential backoff: 30s * 2^attempts, capped at 5 minutes
          scheduled_for = p_now + (INTERVAL '30 seconds' * LEAST(POWER(2, LEAST(o.attempts, 10)), 10)),
          instance_id = NULL,
          lease_expiry = NULL
      WHERE o.message_id = v_failure.msg_id;

      -- 171: release the rest of the run. The rows of the same stream after the failed one that its
      -- holder still leases were claimed with it and must not publish ahead of its retry. Before
      -- this they stayed leased, the next drain of the stream fetched them, and they went out
      -- before the row that failed. Released, they are unowned and the acquisition's ordering rule
      -- (an earlier row deferred into the future blocks the later ones) holds them behind the
      -- retry. The attempt their claim charged is refunded: none of them was attempted. One range
      -- scan on idx_outbox_stream_run from the failed row's position; the holder test is an index
      -- filter on an INCLUDEd column.
      IF v_holder IS NOT NULL AND v_stream IS NOT NULL AND v_stream <> __EMPTY_UUID__::uuid THEN
        UPDATE __SCHEMA__.wh_outbox o
        SET instance_id = NULL,
            lease_expiry = NULL,
            attempts = GREATEST(o.attempts - 1, 0)
        WHERE o.stream_id = v_stream
          AND (o.created_at, o.message_id) > (v_created, v_failure.msg_id)
          AND o.instance_id = v_holder
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
          AND o.coalesce_group IS NULL;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.process_outbox_failures(JSONB, TIMESTAMPTZ) IS
  'Processes outbox message failures: sets the Failed flag, records the error and reason, and '
  'schedules the retry with exponential backoff (capped at 5 minutes), releasing the lease for '
  'reclaim. A failing schedule occurrence also lands a wh_schedule_runs Failed row, and an '
  'at-most-once schedule (delivery_guarantee=1) is parked terminally instead of retried. Reads the '
  'element the runtime writes (MessageId, Reason) as well as the older name (FailureReason). 171: a '
  'retried row releases the rest of its run (later rows of its stream its holder still leases, attempt '
  'refunded), so the stream is retried in order.';

-- ---------------------------------------------------------------------------------------------
-- wh_continue_outbox_streams (new): continue a stream from the lease the drain already holds.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_continue_outbox_streams');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_LeasesTheNextRunAndReturnsOnlyRowsAfterTheCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ContinueOutboxStreams_AStreamThisInstanceDoesNotOwn_IsNotContinuedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_continue_outbox_streams(
  p_instance_id UUID,
  p_stream_ids UUID[],
  -- The last message the drain PUBLISHED on each stream, position for position with p_stream_ids.
  -- Rows this instance holds at or before it are published and waiting for their completion to
  -- land; they are neither leased again nor returned.
  p_after_message_ids UUID[],
  p_run_length INTEGER DEFAULT 100,
  p_max_bytes BIGINT DEFAULT NULL
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  event_data TEXT,
  metadata JSONB,
  scope JSONB,
  status INTEGER,
  attempts INTEGER,
  partition_number INTEGER,
  is_event BOOLEAN,
  commit_sequence BIGINT,
  origin_service_id UUID,
  origin_commit_sequence BIGINT,
  error TEXT,
  priority INTEGER
) AS $$
#variable_conflict use_column
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_run INTEGER := GREATEST(COALESCE(p_run_length, 1), 1);
  v_run_ids UUID[];
BEGIN
  IF p_stream_ids IS NULL OR array_length(p_stream_ids, 1) IS NULL THEN
    RETURN;
  END IF;

  -- Lease each stream's next run. Only a stream whose lease this instance holds is continued: the
  -- claim took that lease with the stream's earlier rows, and continuing under it is what lets the
  -- drain move on without waiting for the next claim cycle while keeping the stream on one instance.
  -- The rows join the stream's lease rather than starting a new one.
  WITH asked AS (
    SELECT a.a_stream_id, a.a_after_id
    FROM unnest(p_stream_ids, p_after_message_ids) AS a(a_stream_id, a_after_id)
    WHERE a.a_stream_id IS NOT NULL
      AND a.a_stream_id <> __EMPTY_UUID__::uuid
  ),
  owned AS (
    SELECT DISTINCT ON (ak.a_stream_id) ak.a_stream_id AS o_stream_id, ak.a_after_id AS o_after_id,
           ast.lease_expiry AS o_lease_expiry
    FROM asked ak
    JOIN __SCHEMA__.wh_active_streams ast
      ON ast.stream_id = ak.a_stream_id
     AND ast.assigned_instance_id = p_instance_id
     AND ast.lease_expiry > v_now
    ORDER BY ak.a_stream_id
  ),
  run_walk AS (
    SELECT ow.o_stream_id, ow.o_lease_expiry, w.w_message_id, w.w_mine, w.w_takeable, w.w_pos
    FROM owned ow
    CROSS JOIN LATERAL (
      -- The stream's pending rows in its own order, skipping the ones already published and waiting
      -- for their completion (held here, at or before the cursor). Rows this instance holds after
      -- the cursor are unpublished work of the run already and count toward it. Same range scan as
      -- the claim's run walk: idx_outbox_stream_run, Index Cond stream_id = $, LIMIT the run.
      SELECT o.message_id AS w_message_id,
             COALESCE(o.instance_id = p_instance_id AND o.lease_expiry > v_now, FALSE) AS w_mine,
             -- COALESCEd because bool_and below skips a NULL, which would read as "not blocking".
             COALESCE((o.instance_id IS NULL OR o.lease_expiry < v_now)
               AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now), FALSE) AS w_takeable,
             ROW_NUMBER() OVER (ORDER BY o.created_at, o.message_id) AS w_pos
      FROM __SCHEMA__.wh_outbox o
      WHERE o.stream_id = ow.o_stream_id
        AND o.processed_at IS NULL
        AND o.published_at IS NULL
        AND o.coalesce_group IS NULL
        AND NOT COALESCE(o.instance_id = p_instance_id
                         AND o.lease_expiry > v_now
                         AND o.message_id <= ow.o_after_id, FALSE)
      ORDER BY o.created_at, o.message_id
      LIMIT v_run
    ) w
  ),
  -- A prefix, as in the claim: it ends at the first row neither held here nor takeable.
  run_open AS (
    SELECT rw.*, bool_and(rw.w_mine OR rw.w_takeable) OVER (PARTITION BY rw.o_stream_id ORDER BY rw.w_pos) AS w_open
    FROM run_walk rw
  ),
  run_locked AS (
    SELECT o.message_id AS locked_message_id
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id IN (SELECT ro.w_message_id FROM run_open ro WHERE ro.w_open AND NOT ro.w_mine)
    FOR UPDATE OF o SKIP LOCKED
  ),
  run_first_gap AS (
    SELECT ro.o_stream_id AS gap_stream_id, MIN(ro.w_pos) AS gap_pos
    FROM run_open ro
    WHERE ro.w_open
      AND NOT ro.w_mine
      AND NOT EXISTS (SELECT 1 FROM run_locked rl WHERE rl.locked_message_id = ro.w_message_id)
    GROUP BY ro.o_stream_id
  ),
  run_rows AS (
    SELECT ro.w_message_id, ro.w_mine, ro.o_lease_expiry
    FROM run_open ro
    LEFT JOIN run_first_gap g ON g.gap_stream_id = ro.o_stream_id
    WHERE ro.w_open
      AND (g.gap_pos IS NULL OR ro.w_pos < g.gap_pos)
  ),
  leased AS (
    UPDATE __SCHEMA__.wh_outbox o
    SET instance_id = p_instance_id,
        lease_expiry = r.o_lease_expiry,
        -- Single-source attempt counting, as in claim_orphaned_outbox: a lease charges one.
        attempts = o.attempts + 1
    FROM run_rows r
    WHERE o.message_id = r.w_message_id
      AND NOT r.w_mine
      AND (o.instance_id IS NULL OR o.lease_expiry < v_now)
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
      AND o.processed_at IS NULL
      AND o.coalesce_group IS NULL
    RETURNING o.message_id AS leased_message_id
  ),
  renewed AS (
    -- The stream's activity follows its work, as the claim's refresh does. Nothing reads this CTE;
    -- a data-modifying CTE runs exactly once whether or not the statement reads it.
    UPDATE __SCHEMA__.wh_active_streams ast
    SET last_activity_at = v_now
    FROM owned ow
    WHERE ast.stream_id = ow.o_stream_id
      AND ast.assigned_instance_id = p_instance_id
    RETURNING ast.stream_id
  )
  SELECT array_agg(x.run_id)
  INTO v_run_ids
  FROM (
    SELECT r.w_message_id AS run_id FROM run_rows r WHERE r.w_mine
    UNION
    SELECT l.leased_message_id FROM leased l
  ) x;

  IF v_run_ids IS NULL THEN
    RETURN;
  END IF;

  -- The run's rows, as fetch_outbox_batch (151) returns them: the same columns, the same event
  -- store join, the same order, and the same byte budget that trims a slice's tail and never its
  -- head. Addressed by primary key, so this reads exactly the run.
  RETURN QUERY
  WITH ranked AS (
    SELECT
      o.*,
      es.commit_sequence AS es_commit_sequence,
      es.origin_service_id AS es_origin_service_id,
      es.origin_commit_sequence AS es_origin_commit_sequence,
      ROW_NUMBER() OVER (
        PARTITION BY o.stream_id
        ORDER BY es.commit_sequence ASC NULLS LAST, o.message_id
      ) AS rank_in_stream
    FROM __SCHEMA__.wh_outbox o
    LEFT JOIN __SCHEMA__.wh_event_store es
      ON o.is_event AND es.event_id = o.message_id
    WHERE o.message_id = ANY(v_run_ids)
      AND o.instance_id = p_instance_id
      AND o.lease_expiry > v_now
      AND o.processed_at IS NULL
      AND o.published_at IS NULL
  ),
  budgeted AS (
    SELECT
      r.*,
      SUM(COALESCE(octet_length(r.event_data::TEXT), 0)
          + COALESCE(octet_length(r.metadata::TEXT), 0))
        OVER (PARTITION BY r.stream_id
              ORDER BY r.es_commit_sequence ASC NULLS LAST, r.message_id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
    FROM ranked r
  )
  SELECT
    b.message_id,
    b.stream_id,
    b.destination::VARCHAR(200),
    b.message_type::VARCHAR(500),
    b.envelope_type::VARCHAR(500),
    b.event_data::TEXT,
    b.metadata,
    b.scope,
    b.status,
    b.attempts,
    b.partition_number,
    b.is_event,
    b.es_commit_sequence,
    b.es_origin_service_id,
    b.es_origin_commit_sequence,
    b.error,
    b.priority
  FROM budgeted b
  WHERE p_max_bytes IS NULL
     OR b.rank_in_stream = 1
     OR b.running_bytes <= p_max_bytes
  ORDER BY b.stream_id, b.es_commit_sequence ASC NULLS LAST, b.message_id;
END;
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;

COMMENT ON FUNCTION __SCHEMA__.wh_continue_outbox_streams(UUID, UUID[], UUID[], INTEGER, BIGINT) IS
  'Continues outbox streams from the lease this instance already holds (171): for each stream it owns, leases '
  'up to p_run_length of the stream''s next consecutive rows as a prefix (stopping at a row it may not take), '
  'and returns the rows it holds after the last one the drain published, in fetch_outbox_batch''s shape and '
  'order. Lets the drain move a long stream on without waiting for the next claim cycle.';

-- ---------------------------------------------------------------------------------------------
-- claim_work: last word 170_HeldIdleRowsAreReOffered.sql, plus the outbox row bound, the run, the
-- per-stream re-offer and the acquisition-full notice.
-- ---------------------------------------------------------------------------------------------
-- claim_work is defined at more than one parameter count across the corpus; every definition site
-- clears the old overloads first (OverloadGuardsCoverEveryDefinitionSiteTests). 171 adds two
-- trailing defaulted parameters, so the arity changes.
SELECT __SCHEMA__.drop_all_overloads('claim_work');

-- <docs>fundamentals/work-coordinator/claim-loop</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkSqlTests.cs:ClaimWork_OutboxHasUnprocessedWork_ReturnsThatWorkAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimOrphanedAcquisitionBoundSqlTests.cs:ClaimWork_DoesNotAcquireMoreThanItsCallerAskedForAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BucketAwareClaimSqlTests.cs:ClaimOrphanedInbox_BackgroundStreams_KeepAFloorOfTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IdleBandHeldRowReOfferTests.cs:WhileBusy_AnIdleBandCommandTheClaimLeases_IsReOfferedToTheDrainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IdleBandHeldRowReOfferTests.cs:AnIdleBandCommandPastMaxAttempts_ReclaimedAfterItsLeaseLapses_ReachesTheDrainEveryCycleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OneLongStream_DrainsInRunsNotRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_ReOffersOneRowPerHeldStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunSqlTests.cs:ClaimWork_OutboxAcquisitionFillsItsBound_RaisesTheFullNoticeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300,
  p_fresh_share DOUBLE PRECISION DEFAULT 0.5,
  p_max_rows INTEGER DEFAULT NULL,
  p_allow_steal BOOLEAN DEFAULT FALSE,
  p_max_perspective_streams INTEGER DEFAULT NULL,
  -- 167: the idle band. The CALLER owns settledness -- it already tracks the dwell for housekeeping
  -- -- and passes it in. Computing it here would mean counting the backlog on every poll, which is
  -- the cost the claim is measured against.
  p_idle_settled BOOLEAN DEFAULT FALSE,
  p_idle_trickle_after INTERVAL DEFAULT INTERVAL '30 minutes',
  p_idle_trickle_slice INTEGER DEFAULT 10,
  p_idle_force_after INTERVAL DEFAULT INTERVAL '4 hours',
  -- 171: the outbox's own row bound, independent of the stream window. NULL is the previous
  -- behavior: acquisition bounded by p_max_streams rows.
  p_max_outbox_rows INTEGER DEFAULT NULL,
  -- 171: how many consecutive rows one outbox stream may lease per claim. NULL (or 1) is the
  -- previous behavior: one row per chosen head.
  p_outbox_run_length INTEGER DEFAULT NULL
) RETURNS TABLE(
  source VARCHAR(20),           -- __CATEGORY_OUTBOX__ | __CATEGORY_INBOX__ | 'receptor' | __CATEGORY_PERSPECTIVE__
  work_id UUID,
  work_stream_id UUID,
  partition_number INTEGER,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  message_data TEXT,
  metadata JSONB,
  status INTEGER,
  attempts INTEGER,
  is_newly_stored BOOLEAN,
  is_orphaned BOOLEAN,
  perspective_name VARCHAR(200),
  priority INTEGER,             -- 150: the inbox row's effective priority (NULL for other sources)
  received_at TIMESTAMPTZ       -- 150: the inbox row's arrival (NULL for other sources)
) AS $$
DECLARE
  c_source_outbox CONSTANT VARCHAR(20) := __CATEGORY_OUTBOX__;
  c_source_inbox CONSTANT VARCHAR(20) := __CATEGORY_INBOX__;
  v_has_any_work BOOLEAN;
  -- 167: the idle band's admission, decided once per poll in the outer block. 170: it bounds inbox
  -- ACQUISITION and the perspective lane loop; the inbox re-offer no longer reads it.
  v_idle_admitted BOOLEAN := FALSE;  -- may the idle band be claimed at all on this poll
  v_idle_capped BOOLEAN := FALSE;    -- admitted by trickle, so bounded to a slice
  -- What to hand acquisition: 0 withheld, NULL full width, N a trickle slice.
  v_idle_acquire_rows INTEGER := 0;
  v_idle_oldest INTERVAL;            -- age of the oldest idle row this instance can see
BEGIN
  -- 157: this function runs under plan_cache_mode = force_custom_plan (see its closing clause), so
  -- the plans below and in the acquisition functions it calls are made for the tables as they are
  -- at each poll, never kept from a poll that found them empty.
  -- Empty-call short-circuit: cheap indexed EXISTS lookups on partial indexes.
  -- Each LIMIT 1 against an existing partial index is sub-millisecond when buffer-cached.
  -- Note: wh_receptor_processing uses completed_at (not processed_at) for the "is done" semantic.
  -- 115: coalesce-pending rows are not claim-pump work, they wait for the coalesce worker.


  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL AND coalesce_group IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox_state WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_receptor_processing WHERE completed_at IS NULL LIMIT 1);

  IF NOT v_has_any_work THEN
    RETURN;  -- empty result set; orphan-claim sub-functions never invoked
  END IF;

  -- Non-empty path: claim outbox work and return it.
  -- Inbox / receptor / perspective claim + return land in subsequent TDD cycles.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    v_count INTEGER;
    -- v0.661: track per-category RETURN QUERY rowcount so the drain-mode hint
    -- can be derived from ROW_COUNT instead of four fresh COUNT(*) queries.
    v_outbox_rows INTEGER := 0;
    v_inbox_rows INTEGER := 0;
    v_receptor_rows INTEGER := 0;
    v_perspective_rows INTEGER := 0;
    -- 171: the outbox acquisition's row bound and what it actually leased, for the full notice.
    v_outbox_bound INTEGER := COALESCE(p_max_outbox_rows, p_max_streams);
    v_outbox_acquired INTEGER := 0;
    -- 158: the lane walks that re-offer held inbox and perspective streams.
    v_bucket INTEGER;
    v_is_event BOOLEAN;
    v_start UUID;
    v_remaining INTEGER;
    v_rows INTEGER;
  BEGIN
    -- 167: whether the idle band may be claimed at all on this poll.
    --
    -- Idle work is work nobody waits for, so it is withheld while the service is busy. A bucket that
    -- is withheld can starve, and its first occupant is auditing, so withholding is bounded by TIME
    -- rather than by the service happening to go quiet:
    --
    --   settled            -> drain at full width, which is the point of the band
    --   older than force   -> drain at full width anyway, however busy. The floor.
    --   older than trickle -> take a slice, so progress is never zero
    --   otherwise          -> withheld
    --
    -- One probe against the held-lane index answers all three, and only when the instance holds idle
    -- work at all: no idle rows, no cost.
    -- CLAIMABLE idle work, not merely work this instance already holds. An earlier draft asked only
    -- about held rows, which cannot admit anything: idle rows arrive unowned, so the band could
    -- never be entered, nothing was ever leased, and the probe went on reporting nothing to do. The
    -- band must be admitted on what this instance COULD take.
    --
    -- Both tables, because the band's first occupant is auditing and audit lands in perspective
    -- events; a gate derived from the inbox alone would withhold a perspective backlog for ever.
    --
    -- MIN of the timestamp rather than MAX of the age: the aggregate has to be over an indexed
    -- COLUMN for the planner to answer it by walking to the first qualifying entry and stopping.
    -- MAX(v_now - received_at) is an expression, and costs a pass over the whole band instead.
    -- LEAST ignores NULLs in Postgres, so a band empty on one side still reports the other, and
    -- empty on both leaves this NULL, which is the "nothing to admit" case below.
    SELECT v_now - LEAST(
      (SELECT MIN(i.received_at)
       FROM __SCHEMA__.wh_inbox_state i
       WHERE i.processed_at IS NULL
         AND i.is_event = TRUE
         AND i.priority > 399
         AND (i.instance_id = p_instance_id OR i.instance_id IS NULL OR i.lease_expiry < v_now)
         AND (i.scheduled_for IS NULL OR i.scheduled_for <= v_now)),
      (SELECT MIN(pe.created_at)
       FROM __SCHEMA__.wh_perspective_events pe
       WHERE pe.processed_at IS NULL
         AND pe.priority > 399
         AND (pe.instance_id = p_instance_id OR pe.instance_id IS NULL OR pe.lease_expiry < v_now)
         AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= v_now))
    ) INTO v_idle_oldest;

    IF v_idle_oldest IS NULL THEN
      v_idle_admitted := FALSE;
    ELSIF p_idle_settled OR v_idle_oldest >= p_idle_force_after THEN
      v_idle_admitted := TRUE;
    ELSIF v_idle_oldest >= p_idle_trickle_after THEN
      v_idle_admitted := TRUE;
      v_idle_capped := TRUE;
    END IF;

    -- Inbox acquisition and the perspective re-offer are both derived from the one decision above.
    IF NOT v_idle_admitted THEN
      v_idle_acquire_rows := 0;
    ELSIF v_idle_capped THEN
      v_idle_acquire_rows := GREATEST(p_idle_trickle_slice, 0);
    ELSE
      v_idle_acquire_rows := NULL;
    END IF;
    -- Self-heal this instance's own registration before ranking against it. When a pod's heartbeat
    -- lapses past the stale cutoff (a GC pause, thread-pool starvation, a database failover), the
    -- stale-instance cleanup reaps its wh_service_instances row. Before this repair, every claim
    -- from that point on failed on the missing row, and because the failure aborted the claim, no
    -- work on the claim path was left to put the row back -- the instance stayed locked out until
    -- it was restarted. Repairing here closes that loop, using the identity the caller already
    -- passes in, so the restored row carries the real service name / host / process id rather than
    -- a placeholder.
    --
    -- Guarded by an indexed primary-key pre-check: claim_work is polled continuously, so an
    -- unconditional UPSERT would write a new row version per poll per instance and bloat the
    -- table. On the healthy path this performs no write at all. Only last_heartbeat_at is
    -- refreshed on conflict -- metadata stays owned by record_heartbeat.
    IF NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_service_instances
      WHERE instance_id = p_instance_id
        AND last_heartbeat_at >= v_stale_cutoff
    ) THEN
      INSERT INTO __SCHEMA__.wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES
        (p_instance_id, p_service_name, p_host_name, p_process_id, v_now, v_now)
      ON CONFLICT (instance_id) DO UPDATE SET
        last_heartbeat_at = EXCLUDED.last_heartbeat_at;
    END IF;

    SELECT instance_rank, active_instance_count INTO v_rank, v_count
    FROM __SCHEMA__.calculate_instance_rank(p_instance_id, v_stale_cutoff);

    -- v0.683, per-inner-function guards. The existing v_has_any_work short-circuit
    -- (top of the function) only fires when ALL four queues are empty. Under steady
    -- import load, that's rare, but the typical pattern is "one queue has work,
    -- the others don't." Without per-function guards, claim_work paid the full
    -- claim_orphaned_*/emit_chain scan cost on every call regardless. Each guard
    -- uses an existing partial index (idx_{outbox,inbox}_unprocessed_claiming WHERE
    -- processed_at IS NULL, etc.) so the EXISTS probe is sub-millisecond. Behavior
    -- is preserved: if a guard returns false, the corresponding inner function had
    -- no rows to claim anyway, so skipping its scan is a pure win.

    -- Claim orphaned / unowned outbox work, only if any outbox row is unprocessed
    -- AND either unowned or has an expired lease (the orphan predicate matched by
    -- claim_orphaned_outbox's WHERE clause).
    -- 158: two index probes, not one scan. A single predicate over "unowned or expired" has to examine
    -- every pending row to prove there is none, which on a busy instance is its whole holdings on
    -- every poll. Unowned rows are found under instance_id IS NULL through the outstanding-by-instance
    -- index (123); expired leases are the head of the lease-expiry index (158), so an instance whose
    -- leases are all live proves it at the first entry.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE processed_at IS NULL
        AND coalesce_group IS NULL  -- 115: pending singles are never orphan-claimable
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND coalesce_group IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- Bounded for the same reason as the inbox call below: without a limit this acquires the whole
      -- eligible backlog in one statement, and the caller's claim limit never reaches the flood.
      -- 171: bounded by the OUTBOX row bound, not the stream window. The window is a stream count
      -- that starts at its floor and grows only on inbox evidence; as a row cap it leased one row on
      -- each of the oldest streams per claim, so a backlog on a few long streams drained one row per
      -- stream per cycle. The window still chooses how many streams move (the heads); each of them
      -- then leases a run of its next rows. Counted, so a full acquisition can say so below.
      SELECT count(*)::INTEGER INTO v_outbox_acquired
      FROM __SCHEMA__.claim_orphaned_outbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        v_outbox_bound, GREATEST(COALESCE(p_outbox_run_length, 1), 1), p_max_streams
      );
    END IF;

    -- Claim orphaned / unowned inbox work, same predicate shape.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- p_max_streams bounds ACQUISITION here, not just the re-emission below. Omitting it let this
      -- call lease the entire eligible backlog in one statement, charging an attempt to every row ,
      -- while the caller's claim window and outstanding budget bounded only what came back out of
      -- eligible_inbox. Both throttles sat downstream of the flood, so neither could ever have held.
      -- 145: acquisition has its own ROW bound. p_max_streams is a stream count; handing it to the
      -- acquisition as a row cap turned fat streams into one row per cycle (#714). The caller passes
      -- the rows it can afford; a caller that does not is bounded by the stream count as before.
      PERFORM __SCHEMA__.claim_orphaned_inbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        COALESCE(p_max_rows, p_max_streams), p_allow_steal,
        -- 167: the admission decided once per poll, carrying its size. 170: this is where the band
        -- is withheld; whatever is acquired is re-offered below in full, so a leased idle row can
        -- never be held unoffered.
        v_idle_acquire_rows
      );
    END IF;

    -- Claim orphaned perspective events, same predicate shape on
    -- wh_perspective_events.
    -- 158: two index probes; see the outbox guard above.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE processed_at IS NULL
        AND instance_id IS NULL
      LIMIT 1
    ) OR EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE lease_expiry < v_now
        AND processed_at IS NULL
        AND instance_id IS NOT NULL
      LIMIT 1
    ) THEN
      -- 145 (#719): perspective ACQUISITION has its own bound. The caller passes 0 while its drain channel is
      -- above its cap, so a perspective backlog is drained before more is leased; re-emission of held work
      -- below stays on p_max_streams so the drain keeps moving.
      -- 160: the perspective acquisition gets a ROW bound as well as its stream bound. It used to
      -- take a batch of streams and lease every pending event of each, so one poll leased whatever
      -- a consumer's streams happened to hold and cost accordingly. The caller's own budget is used
      -- when it passes one; otherwise a multiple of the batch, because handing a stream count
      -- straight to an acquisition as a row cap is 145's #714 mistake in reverse.
      PERFORM __SCHEMA__.claim_orphaned_perspective_events(
        p_instance_id, v_lease_expiry, v_now, COALESCE(p_max_perspective_streams, p_max_streams), v_rank, v_count,
        COALESCE(p_max_rows, GREATEST(COALESCE(p_max_perspective_streams, p_max_streams), 1) * 8)
      );
    END IF;

    -- Claim orphaned receptor work, wh_receptor_processing uses completed_at
    -- (not processed_at) for the "is done" semantic.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_receptor_processing
      WHERE completed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_receptor_work(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now
      );
    END IF;

    -- Back-fill wh_event_store + wh_perspective_events for inbox events claimed above.
    -- Replaces legacy process_work_batch Phase 4.5B + 4.6 self-healing, ensures that by the
    -- time an inbox event row reaches InboxDispatchWorker, its event_store row exists and
    -- perspective_events have been created so PerspectiveWorker can pick them up.
    --
    -- v0.683 guard: only call when this instance owns at least one unprocessed
    -- inbox event row with a stream_id. emit_chain's own internal NOT EXISTS
    -- check against wh_event_store filters out already-emitted rows; we don't
    -- repeat that check here because a production measurement
    -- showed the wrapping NOT EXISTS predicate at 42 ms mean (~4-5% of total
    -- DB time) under heavy inbox load, overwhelming the savings from skipping
    -- emit_chain. The simpler EXISTS uses idx_inbox_instance_lease and is
    -- sub-millisecond. The handler-delay backlog scenario where every event_id
    -- is already in wh_event_store is rare and is more appropriately addressed
    -- on the handler side (composite events) than in the work-pump.
    -- 158: only rows the chain has never read (idx_inbox_chain_pending). A holder in steady state
    -- has none, and the poll skips the chain without touching a held row.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox_state i
      WHERE i.instance_id = p_instance_id
        AND i.processed_at IS NULL
        AND i.is_event = true
        AND i.stream_id IS NOT NULL
        AND i.chain_emitted_at IS NULL
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__._emit_event_store_chain_for_inbox(p_instance_id, v_lease_expiry, v_now, p_partition_count);
    END IF;

    -- Return outbox work owned by this instance, oldest first, a batch of it.
    -- 158: a walk of idx_outbox_held_arrival that stops at the batch. The previous shape ranked every
    -- held row per stream with a window function it never read and sorted them all before taking the
    -- batch, so the poll priced itself by the holdings: a busy instance holds thousands of leased rows
    -- and polls several times a second. Same rows in the same order, ties broken by message id.
    RETURN QUERY
    -- Per-stream-drain projection (Phase H step 5b): claim_work returns stream_ids only for
    -- outbox. The OutboxDrainWorker consumes WorkBatch.OutboxStreamIds and pulls full payloads
    -- on demand via fetch_outbox_batch. Body columns are NULL, keeps the bytes-on-the-wire
    -- proportional to the active stream set, not the leased-row count × payload size.
    --
    -- 171: one row per held STREAM, a batch of streams. The drain reads stream ids, and a claim now
    -- leases runs, so returning held rows let one stream's run fill the batch and hide every other
    -- stream this instance holds from the drain. The held rows are walked exactly as before (the
    -- same predicate and order, a Sort + Limit over this instance's leased rows, 166), bounded by
    -- the larger of the stream window and the outbox row bound, and folded to each stream's most
    -- urgent row. A row with no routable stream id is its own stream, as the drain keys it.
    SELECT
      c_source_outbox               AS source,
      h.message_id                  AS work_id,
      h.stream_id                   AS work_stream_id,
      h.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      h.status,
      h.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      h.priority                    AS priority,   -- 166: what this row was scheduled on
      NULL::TIMESTAMPTZ             AS received_at
    FROM (
      SELECT DISTINCT ON (held.stream_key) held.*
      FROM (
        SELECT o.message_id, o.stream_id, o.partition_number, o.status, o.attempts, o.priority, o.created_at,
               CASE WHEN o.stream_id IS NULL OR o.stream_id = __EMPTY_UUID__::uuid
                    THEN o.message_id ELSE o.stream_id END AS stream_key
        FROM __SCHEMA__.wh_outbox o
        WHERE o.instance_id = p_instance_id
          AND o.processed_at IS NULL
          AND o.coalesce_group IS NULL  -- 115: pending singles; the index predicate
          AND o.lease_expiry > v_now
          AND o.published_at IS NULL  -- skip debug-mode forensic rows (production never sets this, row is deleted)
          AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
        -- 166: priority first, arrival as the tiebreak. Every other queue in the path honors the
        -- number the producer declared -- the inbox through its band lanes, the read-model claim by
        -- ordering on it -- and this one selected by arrival alone, so a reply someone was waiting for
        -- published behind whatever bulk work happened to be queued first.
        --
        -- On the NUMBER, not the band, because that is what the priority contract says: scheduling
        -- works on the bucket, and inside a bucket the number orders work.
        --
        -- This costs no index HERE. The branch is already narrowed to one instance's leased rows by
        -- idx_outbox_outstanding_by_instance, and it already sorts, because that index carries
        -- lease_expiry rather than created_at. Measured on a deployed database the plan is identical
        -- either way (Sort, cost 1.12..1.13), so priority joins an existing sort key. The ACQUISITION
        -- side is the one that needed idx_outbox_pending_priority_arrival (166); that restraint
        -- matters because the outbox is the hottest write path during a bulk load.
        ORDER BY o.priority, o.created_at, o.message_id
        LIMIT GREATEST(p_max_streams, v_outbox_bound, 0)
      ) held
      ORDER BY held.stream_key, held.priority, held.created_at, held.message_id
    ) h
    ORDER BY h.priority, h.created_at, h.message_id
    LIMIT p_max_streams;

    -- v0.661: track this category's RETURN QUERY rowcount so the drain-mode
    -- hint at function end can be derived from ROW_COUNT instead of a fresh
    -- COUNT(*) scan. See drain-mode hint block below.
    GET DIAGNOSTICS v_outbox_rows = ROW_COUNT;

    -- Return inbox work owned by this instance: one row per lane a held stream appears in, a batch
    -- of them.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_inbox_held_lanes one index-only probe per stream, lane by lane in the
    -- order the batch is ordered: most urgent bucket first (150), commands before events inside a
    -- bucket (145). Inside a lane the fresh and retried classes are walked side by side (126), each
    -- able to take the whole batch, because the share must not hold a slot empty when the other
    -- class is: what apportions them is the interleave below, not the walks. A lane's walk starts at
    -- a stream id drawn per poll and wraps once, so a lane larger than the batch does not enumerate
    -- the same streams on every poll; a lane no larger than the batch is enumerated whole. One step
    -- lands on one stream, so a lane costs exactly the streams it enumerates.
    --
    -- One row stands for a held stream in a lane: the oldest row the instance holds of it there,
    -- carrying that row's number, arrival and attempts. A stream whose rows span lanes is returned
    -- once per lane, exactly as before, and the batch hooks fold those rows to the stream's most
    -- urgent number and oldest arrival (ClaimedInboxStreamFolder), unchanged. What is no longer
    -- returned is a stream's further rows inside one lane: the drain consumes stream ids and pulls a
    -- stream's rows on demand (fetch_inbox_batch), so they carried nothing a caller read, and ranking
    -- them is what cost the poll. A stream met in both classes of a lane stands as its retried row,
    -- which is its head -- an attempt is charged at the head and everything older is processed, so a
    -- stream with a retried row has a retried head, and stream FIFO means the fresh rows behind it
    -- cannot dispatch anyway (126). The previous shape ranked every held row with three window
    -- functions on every poll and fetched every held row's heap page to do it, and a busy instance
    -- holds thousands of rows and polls several times a second.
    --
    -- GREATEST drops a NULL, so a NULL batch leaves nothing to fill and the re-offer returns nothing.
    -- Nothing passes NULL (the parameter defaults to 1000 and the coordinators pass an integer), and
    -- returning nothing is the safe reading of "no batch size": the previous LIMIT NULL meant no
    -- bound at all, which is the failure this migration exists to stop.
    v_remaining := GREATEST(p_max_streams, 0);
    <<inbox_lanes>>
    FOR v_bucket IN 0..3 LOOP
      -- 170: the idle band (bucket 3) is re-offered like every other bucket. These walks visit only
      -- rows this instance already holds under a live lease, so there is nothing left to withhold:
      -- the lease is taken and the attempt charged. 167 skipped the bucket unless the band was
      -- admitted, and acquisition takes idle commands and lapsed idle rows regardless, so a held row
      -- could sit here unoffered, lapse, be re-acquired and charged again, for ever -- never reaching
      -- the dispatcher that enforces the attempt ceiling. Admission bounds ACQUISITION
      -- (claim_orphaned_inbox's p_idle_max_rows); it never hides what is already held.
      FOREACH v_is_event IN ARRAY ARRAY[false, true] LOOP
        EXIT inbox_lanes WHEN v_remaining <= 0;
        v_start := gen_random_uuid();
        RETURN QUERY
        WITH RECURSIVE lanes AS (
          -- Two walks side by side, one per class, seeded at the drawn point. A seed is a bound and
          -- not a candidate.
          SELECT c.fresh_lane, v_start AS stream_id, false AS wrapped, true AS is_seed, 0 AS steps,
                 NULL::INTEGER AS priority, NULL::UUID AS message_id, NULL::TIMESTAMPTZ AS received_at,
                 NULL::INTEGER AS attempts, NULL::INTEGER AS partition_number, NULL::INTEGER AS status
          FROM (VALUES (true), (false)) AS c(fresh_lane)
          UNION ALL
          SELECT h.fresh_lane, n.stream_id, n.wrapped, false, h.steps + 1,
                 n.priority, n.message_id, n.received_at, n.attempts, n.partition_number, n.status
          FROM lanes h
          CROSS JOIN LATERAL (
            -- The lane's next stream after the current one, up to the drawn point once wrapped. The
            -- index is keyed (holder, bucket, kind, class, stream, arrival, id) and covers every
            -- column below, so this is one index-only probe: it lands on the stream's oldest row in
            -- the lane and the next stream is one probe away. The upper bound is written as a CASE
            -- rather than an OR so it stays an index condition; as a filter the walk would read past
            -- the drawn point instead of stopping at it.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, h.wrapped AS wrapped
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND i.stream_id > h.stream_id
               AND i.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            UNION ALL
            -- ...or, past the lane's last stream, its first one: the walk wraps, once.
            (SELECT i.stream_id, i.priority, i.message_id, i.received_at, i.attempts,
                    i.partition_number, i.status, true
             FROM __SCHEMA__.wh_inbox_state i
             WHERE i.instance_id = p_instance_id
               AND i.processed_at IS NULL
               AND (CASE WHEN i.priority <= 99 THEN 0 WHEN i.priority <= 199 THEN 1 WHEN i.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
               AND i.is_event = v_is_event
               AND (i.attempts = 0) = h.fresh_lane
               AND i.lease_expiry > v_now
               AND NOT h.wrapped
               AND i.stream_id <= v_start
             ORDER BY i.stream_id, i.received_at, i.message_id
             LIMIT 1)
            LIMIT 1
          ) n
          -- One step, one stream: each class may take the whole batch, and the interleave apportions.
          WHERE h.steps < v_remaining
        ),
        inbox_candidates AS (
          -- A stream met in both classes stands as its retried row: that row is its head (126).
          SELECT DISTINCT ON (l.stream_id)
                 l.stream_id, l.priority, l.message_id, l.received_at, l.attempts,
                 l.partition_number, l.status
          FROM lanes l
          WHERE NOT l.is_seed
          ORDER BY l.stream_id, l.fresh_lane
        ),
        ranked_inbox AS (
          SELECT c.stream_id, c.priority, c.message_id, c.received_at, c.attempts,
                 c.partition_number, c.status,
                 (c.attempts = 0) AS is_fresh,
                 ROW_NUMBER() OVER (PARTITION BY (c.attempts = 0) ORDER BY c.received_at, c.message_id) AS class_rank
          FROM inbox_candidates c
        ),
        ordered_inbox AS (
          SELECT ri.stream_id, ri.priority, ri.message_id, ri.received_at, ri.attempts,
                 ri.partition_number, ri.status
          FROM ranked_inbox ri
          ORDER BY
            -- 126: fresh-head streams receive p_fresh_share of the batch, retried-head streams the
            -- remainder, each class in arrival order; an empty class hands its share to the other,
            -- because the key only competes candidates that exist.
            CASE WHEN ri.is_fresh
                 THEN (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(LEAST(p_fresh_share, 1.0), 0.000001)
                 ELSE (ri.class_rank - 1)::DOUBLE PRECISION / GREATEST(1.0 - LEAST(p_fresh_share, 1.0), 0.000001)
            END,
            ri.received_at,
            ri.message_id
          LIMIT v_remaining
        )
        -- Per-stream-drain projection (Phase H step 5d): inbox follows outbox into stream-ids-only.
        -- InboxDrainWorker reads stream_ids off IInboxDrainChannel and pulls payloads on demand
        -- via fetch_inbox_batch. Body columns are NULL -- keeps claim_work's bytes-on-the-wire
        -- proportional to active stream count.
        SELECT
          c_source_inbox                AS source,
          oi.message_id                 AS work_id,
          oi.stream_id                  AS work_stream_id,
          oi.partition_number,
          NULL::VARCHAR(200)            AS destination,
          NULL::VARCHAR(500)            AS message_type,
          NULL::VARCHAR(500)            AS envelope_type,
          NULL::TEXT                    AS message_data,
          NULL::JSONB                   AS metadata,
          oi.status,
          oi.attempts,
          false                         AS is_newly_stored,
          false                         AS is_orphaned,
          NULL::VARCHAR(200)            AS perspective_name,
          oi.priority                   AS priority,
          oi.received_at                AS received_at
        FROM ordered_inbox oi;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_remaining := v_remaining - v_rows;
      END LOOP;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_inbox_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- Return receptor work owned by this instance.
    -- Receptor work uses `id` as the work_id (not message_id) and `completed_at` as the "done" marker.
    -- Most fields are NULL, receptors carry their state in dedicated columns the worker reads
    -- directly via the work_id; the row here just signals "this receptor needs attention".
    RETURN QUERY
    SELECT
      'receptor'::VARCHAR(20)       AS source,
      rp.id                         AS work_id,
      rp.stream_id                  AS work_stream_id,
      rp.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      rp.status::INTEGER,
      rp.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name,
      NULL::INTEGER                 AS priority,
      NULL::TIMESTAMPTZ             AS received_at
    FROM __SCHEMA__.wh_receptor_processing rp
    WHERE rp.instance_id = p_instance_id
      AND rp.lease_expiry > v_now
      AND rp.completed_at IS NULL
    LIMIT p_max_streams;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_receptor_rows = ROW_COUNT;

    -- Return perspective work as one row per distinct stream owned by this instance, most urgent
    -- bucket first, oldest event first within a bucket, a batch of streams.
    -- 158: the poll is priced by the batch, never by the holdings. The streams an instance holds are
    -- enumerated through idx_perspective_held_lanes one index-only probe per stream, bucket by bucket,
    -- most urgent first; the first entry of a stream in its bucket is its oldest held event there,
    -- which is what orders the streams within the bucket. The walk starts at a stream id drawn per
    -- poll and wraps once, so a bucket larger than the batch does not enumerate the same streams on
    -- every poll, and one no larger than the batch is enumerated whole. One step lands on one stream,
    -- so a bucket costs exactly the streams it enumerates. The previous shape aggregated every held
    -- event per poll to put streams with a hundred or fewer pending events ahead of larger ones; that
    -- tier guarded a batch of rows against one large stream, and the drain has been per stream with an
    -- unbounded channel since Phase H, so a large stream no longer displaces small ones.
    v_remaining := GREATEST(p_max_streams, 0);
    FOR v_bucket IN 0..3 LOOP
      -- 167: the idle band is bucket 3 and is skipped unless time or quiet admitted it.
      CONTINUE WHEN v_bucket = 3 AND NOT v_idle_admitted;
      IF v_bucket = 3 AND v_idle_capped THEN
        -- admitted by trickle, not by quiet: a slice, never a burst.
        v_remaining := LEAST(v_remaining, GREATEST(p_idle_trickle_slice, 0));
      END IF;
      EXIT WHEN v_remaining <= 0;
      v_start := gen_random_uuid();
      RETURN QUERY
      WITH RECURSIVE lane AS (
        -- Seeded at the drawn point; the seed is not a candidate.
        SELECT v_start AS stream_id, NULL::UUID AS event_id, false AS wrapped, true AS is_seed, 0 AS steps
        UNION ALL
        SELECT n.stream_id, n.event_id, n.wrapped, false, h.steps + 1
        FROM lane h
        CROSS JOIN LATERAL (
          -- The bucket's next stream after the current one, up to the drawn point once wrapped...
          (SELECT pe.stream_id, pe.event_id, h.wrapped AS wrapped
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND pe.stream_id > h.stream_id
             AND pe.stream_id <= CASE WHEN h.wrapped THEN v_start ELSE __MAX_UUID__::UUID END
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          UNION ALL
          -- ...or, past the bucket's last stream, its first one: the walk wraps.
          (SELECT pe.stream_id, pe.event_id, true
           FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id
             AND pe.processed_at IS NULL
             AND (CASE WHEN pe.priority <= 99 THEN 0 WHEN pe.priority <= 199 THEN 1 WHEN pe.priority <= 399 THEN 2 ELSE 3 END) = v_bucket
             AND pe.lease_expiry > v_now
             AND NOT h.wrapped
             AND pe.stream_id <= v_start
           ORDER BY pe.stream_id, pe.event_id
           LIMIT 1)
          LIMIT 1
        ) n
        WHERE h.steps < v_remaining
      )
      SELECT
        'perspective_stream'::VARCHAR(20) AS source,
        NULL::UUID                        AS work_id,
        l.stream_id                       AS work_stream_id,
        NULL::INTEGER                     AS partition_number,
        NULL::VARCHAR(200)                AS destination,
        NULL::VARCHAR(500)                AS message_type,
        NULL::VARCHAR(500)                AS envelope_type,
        NULL::TEXT                        AS message_data,
        NULL::JSONB                       AS metadata,
        0::INTEGER                        AS status,
        0::INTEGER                        AS attempts,
        false                             AS is_newly_stored,
        false                             AS is_orphaned,
        NULL::VARCHAR(200)                AS perspective_name,
        NULL::INTEGER                     AS priority,
        NULL::TIMESTAMPTZ                 AS received_at
      FROM lane l
      WHERE NOT l.is_seed
      ORDER BY l.event_id, l.stream_id
      LIMIT v_remaining;

      GET DIAGNOSTICS v_rows = ROW_COUNT;
      v_remaining := v_remaining - v_rows;
    END LOOP;

    -- v0.661: the category's row count feeds the drain-mode hint at the end.
    v_perspective_rows := GREATEST(p_max_streams, 0) - v_remaining;

    -- 130 doorbell debounce: finding work stamps this instance's watermark, the signal
    -- producers use to suppress redundant notifies while this drainer is awake. Rides
    -- inside the claim (zero extra round trips) and skips the empty case so the
    -- empty-call short-circuit's ~1 ms idle floor is untouched.
    -- 140: the stamp never waits (issue #699). The store's debounce touches the same rows inside
    -- its own transaction; under a burst the two met in opposite orders with the inbox row locks
    -- and deadlocked. A watermark row another session holds is skipped this tick: the stamp is a
    -- freshness hint and the next tick re-stamps it. Rows that do not exist yet are created; rows
    -- that exist and are free are stamped in place.
    WITH kinds AS (
      SELECT k.kind
      FROM (VALUES (c_source_outbox), (c_source_inbox), (__CATEGORY_PERSPECTIVE__)) AS k(kind)
    WHERE (k.kind = c_source_outbox AND v_outbox_rows > 0)
       OR (k.kind = c_source_inbox AND (v_inbox_rows > 0 OR v_receptor_rows > 0))
       -- 133: the perspective watermark must reflect DRAINABLE progress, not merely a
       -- claimed Stored-but-fence-held row. claim_work returns a perspective_stream row for
       -- any leased unprocessed perspective_event, but a row whose underlying event is still
       -- unstamped (commit_sequence IS NULL, held by the per-database ordering fence) cannot
       -- be surfaced by the fetch gate, the drainer spins with no progress. Arming the
       -- watermark for it lets the doorbell debounce (130/131) suppress the fence-clearing
       -- stamp's make-up ring, stranding visibility on the adaptive poll cap (issue #677).
       -- The EXISTS runs at most once: the k.kind guard short-circuits it away for the
       -- outbox/inbox VALUES rows.
       -- 158: the question is asked of a batch of the rows leased longest, not of every held row.
       -- Over all of them it was priced by the holdings whenever the fence held (#677 is exactly
       -- that state), joining every leased row to the event store per poll. A held row whose event is
       -- stamped is found among the oldest leases if it is found at all; when none of them is, the
       -- watermark stays unarmed and the make-up ring goes through, which is the safe direction.
       OR (k.kind = __CATEGORY_PERSPECTIVE__ AND v_perspective_rows > 0 AND EXISTS (
             SELECT 1
             FROM (
               SELECT pe.event_id
               FROM __SCHEMA__.wh_perspective_events pe
               WHERE pe.instance_id = p_instance_id
                 AND pe.lease_expiry > v_now
                 AND pe.processed_at IS NULL
               ORDER BY pe.lease_expiry
               LIMIT GREATEST(p_max_streams, 1)
             ) held
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = held.event_id
             WHERE es.commit_sequence IS NOT NULL))
    ),
    lockable AS (
      SELECT ns.instance_id, ns.payload_kind
      FROM __SCHEMA__.wh_notify_state ns
      JOIN kinds ON kinds.kind = ns.payload_kind
      WHERE ns.instance_id = p_instance_id
      FOR UPDATE OF ns SKIP LOCKED
    ),
    stamped AS (
      UPDATE __SCHEMA__.wh_notify_state ns
      SET last_work_at = NOW()
      FROM lockable
      WHERE ns.instance_id = lockable.instance_id AND ns.payload_kind = lockable.payload_kind
      RETURNING ns.payload_kind
    )
    INSERT INTO __SCHEMA__.wh_notify_state (instance_id, payload_kind, last_work_at)
    SELECT p_instance_id, kinds.kind, NOW()
    FROM kinds
    WHERE NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_notify_state ns2
      WHERE ns2.instance_id = p_instance_id AND ns2.payload_kind = kinds.kind)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    -- Drain-mode hint: if any of the four return categories filled its LIMIT
    -- (rows == p_max_streams), there's likely more eligible work for this
    -- instance, RAISE NOTICE so the C# claim worker skips its wait and
    -- re-polls immediately. Survives pgbouncer (protocol message, not a
    -- session-state thing).
    --
    -- v0.661 forensic (gate.hold_duration_ms histogram during a consumer's
    -- draft-job import): the prior implementation ran four separate
    -- COUNT(*) queries here (one per category, plus a COUNT(DISTINCT
    -- stream_id) on wh_perspective_events). Under import load with millions
    -- of leased rows per instance, those counts dominated claim_work hold
    -- time, ClaimWorkAsync at p99 5031 ms / avg 128 ms. We don't need
    -- exact counts; we only need to know whether ANY category filled its
    -- LIMIT. ROW_COUNT after each RETURN QUERY gives us that for free.
    IF v_outbox_rows = p_max_streams
       OR v_inbox_rows = p_max_streams
       OR v_receptor_rows = p_max_streams
       OR v_perspective_rows = p_max_streams THEN
      RAISE NOTICE 'whizbang.has_more=true';
    END IF;

    -- 171: the outbox acquisition leased its whole row bound, so the backlog it was taken from is
    -- at least that large again. Distinct from has_more above, which also fires when this instance
    -- merely HOLDS a full batch (a re-offer), and a claim loop that re-claimed on a re-offer would
    -- spin. This one is only ever raised by NEW work, so the claim loop may claim again at once;
    -- what bounds that loop is the row bound itself, which the caller sizes from what it holds.
    IF v_outbox_bound > 0 AND v_outbox_acquired >= v_outbox_bound THEN
      RAISE NOTICE 'whizbang.outbox_acquisition_full=true';
    END IF;
  END;

  RETURN;
END;
-- 157: custom plans for the poll and everything it calls. The queue tables are empty between loads,
-- and a session that polled while they were empty kept generic plans made for empty tables; once
-- the tables filled those plans scanned them whole, nested, on every poll, until the next analyze
-- invalidated them. Measured: a poll that takes well under a second with fresh plans did not finish
-- inside the command timeout with the empty-table plans. Planning per call costs milliseconds.
$$ LANGUAGE plpgsql SET plan_cache_mode = force_custom_plan;
