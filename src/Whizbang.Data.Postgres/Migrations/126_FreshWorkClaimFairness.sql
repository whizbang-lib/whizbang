-- Migration: 126_FreshWorkClaimFairness
-- Date: 2026-09-03
-- Description: claim_work's inbox ordering becomes a weighted-fair merge between fresh-head
--   streams (head row attempts = 0) and retry-head streams. Strict oldest-first ordering
--   guaranteed that a large retry backlog starved every new arrival: in production a 28,000-row
--   control-plane backlog put a user's brand-new single-row stream hours out. p_fresh_share
--   (default 0.5) reserves the fresh class's share of every batch; the merge is work-conserving,
--   so an empty class hands its slots to the other. Within each class, FIFO by received_at.
--   Stream-FIFO within a stream is untouched — classification orders streams, never rows.
-- Dependencies: 115_TagBoundCoalescing (prior claim_work definition, copied verbatim + delta)
-- Objects: claim_work

SELECT __SCHEMA__.drop_all_overloads('claim_work');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300,
  p_fresh_share DOUBLE PRECISION DEFAULT 0.5
) RETURNS TABLE(
  source VARCHAR(20),           -- 'outbox' | 'inbox' | 'receptor' | 'perspective'
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
  perspective_name VARCHAR(200)
) AS $$
DECLARE
  v_has_any_work BOOLEAN;
BEGIN
  -- Empty-call short-circuit: cheap indexed EXISTS lookups on partial indexes.
  -- Each LIMIT 1 against an existing partial index is sub-millisecond when buffer-cached.
  -- Note: wh_receptor_processing uses completed_at (not processed_at) for the "is done" semantic.
  -- 115: coalesce-pending rows are not claim-pump work — they wait for the coalesce worker.
  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL AND coalesce_group IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox WHERE processed_at IS NULL LIMIT 1)
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
  BEGIN
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

    -- v0.683 — per-inner-function guards. The existing v_has_any_work short-circuit
    -- (top of the function) only fires when ALL four queues are empty. Under steady
    -- import load, that's rare — but the typical pattern is "one queue has work,
    -- the others don't." Without per-function guards, claim_work paid the full
    -- claim_orphaned_*/emit_chain scan cost on every call regardless. Each guard
    -- uses an existing partial index (idx_{outbox,inbox}_unprocessed_claiming WHERE
    -- processed_at IS NULL, etc.) so the EXISTS probe is sub-millisecond. Behavior
    -- is preserved: if a guard returns false, the corresponding inner function had
    -- no rows to claim anyway, so skipping its scan is a pure win.

    -- Claim orphaned / unowned outbox work — only if any outbox row is unprocessed
    -- AND either unowned or has an expired lease (the orphan predicate matched by
    -- claim_orphaned_outbox's WHERE clause).
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_outbox
      WHERE processed_at IS NULL
        AND coalesce_group IS NULL  -- 115: pending singles are never orphan-claimable
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      -- Bounded for the same reason as the inbox call below: without a limit this acquires the whole
      -- eligible backlog in one statement, and the caller's claim limit never reaches the flood.
      PERFORM __SCHEMA__.claim_orphaned_outbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        p_max_streams
      );
    END IF;

    -- Claim orphaned / unowned inbox work — same predicate shape.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox
      WHERE processed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      -- p_max_streams bounds ACQUISITION here, not just the re-emission below. Omitting it let this
      -- call lease the entire eligible backlog in one statement — charging an attempt to every row —
      -- while the caller's claim window and outstanding budget bounded only what came back out of
      -- eligible_inbox. Both throttles sat downstream of the flood, so neither could ever have held.
      PERFORM __SCHEMA__.claim_orphaned_inbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff,
        p_max_streams
      );
    END IF;

    -- Claim orphaned perspective events — same predicate shape on
    -- wh_perspective_events.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events
      WHERE processed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_perspective_events(
        p_instance_id, v_lease_expiry, v_now, p_max_streams, v_rank, v_count
      );
    END IF;

    -- Claim orphaned receptor work — wh_receptor_processing uses completed_at
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
    -- Replaces legacy process_work_batch Phase 4.5B + 4.6 self-healing — ensures that by the
    -- time an inbox event row reaches InboxDispatchWorker, its event_store row exists and
    -- perspective_events have been created so PerspectiveWorker can pick them up.
    --
    -- v0.683 guard: only call when this instance owns at least one unprocessed
    -- inbox event row with a stream_id. emit_chain's own internal NOT EXISTS
    -- check against wh_event_store filters out already-emitted rows; we don't
    -- repeat that check here because a production measurement
    -- showed the wrapping NOT EXISTS predicate at 42 ms mean (~4-5% of total
    -- DB time) under heavy inbox load — overwhelming the savings from skipping
    -- emit_chain. The simpler EXISTS uses idx_inbox_instance_lease and is
    -- sub-millisecond. The handler-delay backlog scenario where every event_id
    -- is already in wh_event_store is rare and is more appropriately addressed
    -- on the handler side (composite events) than in the work-pump.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox i
      WHERE i.instance_id = p_instance_id
        AND i.processed_at IS NULL
        AND i.is_event = true
        AND i.stream_id IS NOT NULL
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__._emit_event_store_chain_for_inbox(p_instance_id, v_lease_expiry, v_now, p_partition_count);
    END IF;

    -- Return outbox work owned by this instance.
    -- Per-stream rank prevents one busy stream from starving others; global LIMIT bounds the batch.
    RETURN QUERY
    WITH eligible_outbox AS (
      SELECT
        o.*,
        ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.created_at) AS stream_rank
      FROM __SCHEMA__.wh_outbox o
      WHERE o.instance_id = p_instance_id
        AND o.lease_expiry > v_now
        AND o.processed_at IS NULL
        AND o.coalesce_group IS NULL  -- 115: matches the narrowed eligible-scan index predicate
        AND o.published_at IS NULL  -- skip debug-mode forensic rows (production never sets this — row is deleted)
        AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
    ),
    ordered_outbox AS (
      SELECT eo.*, ROW_NUMBER() OVER (ORDER BY eo.created_at) AS row_num
      FROM eligible_outbox eo
      ORDER BY eo.created_at
      LIMIT p_max_streams
    )
    -- Per-stream-drain projection (Phase H step 5b): claim_work returns stream_ids only for
    -- outbox. The OutboxDrainWorker consumes WorkBatch.OutboxStreamIds and pulls full payloads
    -- on demand via fetch_outbox_batch. Body columns are NULL — keeps the bytes-on-the-wire
    -- proportional to the active stream set, not the leased-row count × payload size.
    SELECT
      'outbox'::VARCHAR(20)         AS source,
      oo.message_id                 AS work_id,
      oo.stream_id                  AS work_stream_id,
      oo.partition_number,
      NULL::VARCHAR(200)            AS destination,
      NULL::VARCHAR(500)            AS message_type,
      NULL::VARCHAR(500)            AS envelope_type,
      NULL::TEXT                    AS message_data,
      NULL::JSONB                   AS metadata,
      oo.status,
      oo.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name
    FROM ordered_outbox oo;

    -- v0.661: track this category's RETURN QUERY rowcount so the drain-mode
    -- hint at function end can be derived from ROW_COUNT instead of a fresh
    -- COUNT(*) scan. See drain-mode hint block below.
    GET DIAGNOSTICS v_outbox_rows = ROW_COUNT;

    -- Return inbox work owned by this instance. Inbox uses handler_name (cast to destination)
    -- and received_at (cast to created_at). envelope_type is NULL for inbox.
    RETURN QUERY
    WITH eligible_inbox AS (
      SELECT
        i.*,
        ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at) AS stream_rank
      FROM __SCHEMA__.wh_inbox i
      WHERE i.instance_id = p_instance_id
        AND i.lease_expiry > v_now
        AND i.processed_at IS NULL
    ),
    -- 126: fresh-work fairness. Strict oldest-first starved real-time work: a 28k-row retry
    -- backlog means a brand-new single-row stream is guaranteed the last slot, hours out. A
    -- stream is classified by its HEAD row (stream-FIFO means rows behind a retried head cannot
    -- dispatch anyway), and the two classes merge by weighted fair queuing: fresh-head streams
    -- receive p_fresh_share of the batch, retry-head streams the remainder, each class FIFO
    -- within itself. Work-conserving by construction — an empty class hands its share to the
    -- other, because the merge key only competes rows that exist.
    inbox_stream_class AS (
      SELECT ei.stream_id AS class_stream_id,
             (ei.attempts = 0) AS is_fresh
      FROM eligible_inbox ei
      WHERE ei.stream_rank = 1
    ),
    classified_inbox AS (
      SELECT ei.*, isc.is_fresh
      FROM eligible_inbox ei
      JOIN inbox_stream_class isc ON isc.class_stream_id = ei.stream_id
    ),
    ranked_inbox AS (
      SELECT ci.*,
             ROW_NUMBER() OVER (PARTITION BY ci.is_fresh ORDER BY ci.received_at) AS class_rank
      FROM classified_inbox ci
    ),
    ordered_inbox AS (
      SELECT ri.*, ROW_NUMBER() OVER (
               ORDER BY
                 CASE WHEN ri.is_fresh
                      THEN (ri.class_rank - 1)::DOUBLE PRECISION
                           / GREATEST(LEAST(p_fresh_share, 1.0), 0.000001)
                      ELSE (ri.class_rank - 1)::DOUBLE PRECISION
                           / GREATEST(1.0 - LEAST(p_fresh_share, 1.0), 0.000001)
                 END,
                 ri.received_at
             ) AS row_num
      FROM ranked_inbox ri
      ORDER BY row_num
      LIMIT p_max_streams
    )
    -- Per-stream-drain projection (Phase H step 5d): inbox follows outbox into stream-ids-only.
    -- InboxDrainWorker reads stream_ids off IInboxDrainChannel and pulls payloads on demand
    -- via fetch_inbox_batch. Body columns are NULL — keeps claim_work's bytes-on-the-wire
    -- proportional to active stream count.
    SELECT
      'inbox'::VARCHAR(20)          AS source,
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
      NULL::VARCHAR(200)            AS perspective_name
    FROM ordered_inbox oi;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_inbox_rows = ROW_COUNT;

    -- Return receptor work owned by this instance.
    -- Receptor work uses `id` as the work_id (not message_id) and `completed_at` as the "done" marker.
    -- Most fields are NULL — receptors carry their state in dedicated columns the worker reads
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
      NULL::VARCHAR(200)            AS perspective_name
    FROM __SCHEMA__.wh_receptor_processing rp
    WHERE rp.instance_id = p_instance_id
      AND rp.lease_expiry > v_now
      AND rp.completed_at IS NULL
    LIMIT p_max_streams;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_receptor_rows = ROW_COUNT;

    -- Return perspective work as one row per distinct stream owned by this instance.
    -- Two-tier fairness ordering: small streams (≤ 100 pending events) come first, then
    -- large streams. Without this, a single large stream with thousands of pending events
    -- could starve many small streams behind it on every claim cycle. The 100-event tier
    -- threshold matches the typical perspective batch size.
    RETURN QUERY
    WITH stream_counts AS (
      SELECT
        pe.stream_id,
        COUNT(*) AS pending_count
      FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.instance_id = p_instance_id
        AND pe.lease_expiry > v_now
        AND pe.processed_at IS NULL
      GROUP BY pe.stream_id
    )
    SELECT
      'perspective_stream'::VARCHAR(20) AS source,
      NULL::UUID                        AS work_id,
      sc.stream_id                      AS work_stream_id,
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
      NULL::VARCHAR(200)                AS perspective_name
    FROM stream_counts sc
    ORDER BY
      CASE WHEN sc.pending_count <= 100 THEN 0 ELSE 1 END,  -- small streams first
      sc.pending_count                                       -- within tier, smallest-first
    LIMIT p_max_streams;

    -- v0.661: see outbox block above.
    GET DIAGNOSTICS v_perspective_rows = ROW_COUNT;

    -- Drain-mode hint: if any of the four return categories filled its LIMIT
    -- (rows == p_max_streams), there's likely more eligible work for this
    -- instance — RAISE NOTICE so the C# claim worker skips its wait and
    -- re-polls immediately. Survives pgbouncer (protocol message, not a
    -- session-state thing).
    --
    -- v0.661 forensic (gate.hold_duration_ms histogram during a consumer's
    -- draft-job import): the prior implementation ran four separate
    -- COUNT(*) queries here (one per category, plus a COUNT(DISTINCT
    -- stream_id) on wh_perspective_events). Under import load with millions
    -- of leased rows per instance, those counts dominated claim_work hold
    -- time — ClaimWorkAsync at p99 5031 ms / avg 128 ms. We don't need
    -- exact counts; we only need to know whether ANY category filled its
    -- LIMIT. ROW_COUNT after each RETURN QUERY gives us that for free.
    IF v_outbox_rows = p_max_streams
       OR v_inbox_rows = p_max_streams
       OR v_receptor_rows = p_max_streams
       OR v_perspective_rows = p_max_streams THEN
      RAISE NOTICE 'whizbang.has_more=true';
    END IF;
  END;

  RETURN;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_work IS
'Phase A focused claim function replacing the claim portion of process_work_batch. Returns work for the calling instance with an empty-call short-circuit that skips orphan-claim scans when all queues are empty (drops idle floor from ~17 ms to ≤ 1 ms). Real claim logic lands in subsequent TDD cycles; old process_work_batch remains the active production path until Phase C migrates IWorkCoordinator callers. 115: coalesce-pending outbox rows (coalesce_group IS NOT NULL) are excluded from the empty-call probe, the orphan-claim guard, and the eligible scan — they surface only via the coalesce worker''s fold or an explicit release. 126: inbox claim order is a weighted-fair merge of fresh-head and retry-head streams (p_fresh_share, default 0.5) so real-time work always gets batch slots while a retry backlog drains — strict oldest-first let a 28k-row backlog starve every new arrival.';
