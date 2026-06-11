-- Migration: 029_ProcessWorkBatch.sql
-- Date: 2025-12-28
-- Description: Hosts the focused work-pump SQL functions that replaced the legacy
--              process_work_batch orchestrator: claim_work, _emit_event_store_chain,
--              _emit_event_store_chain_for_inbox, commit_handler_result/batch,
--              complete_outbox_published, record_heartbeat, complete_perspective,
--              report_failures, renew_leases, flush_completions, resolve_sync_inquiries.
--              The legacy orchestrator is dropped at the top so existing dev databases
--              lose it on the next run.
-- Dependencies: 009-028 (foundation, completion, failure, storage, cleanup, claiming functions, and error tracking)

DROP FUNCTION IF EXISTS __SCHEMA__.process_work_batch CASCADE;

-- ============================================================================
-- claim_work — focused replacement for the claim portion of process_work_batch.
-- Phase A of work-pump decomposition. Empty-call short-circuit: when all queues
-- are empty, return immediately without invoking any claim_orphaned_* function,
-- cutting the structural ~17 ms idle floor of process_work_batch to ≤ 1 ms.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('claim_work');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300
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
  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL LIMIT 1)
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
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_outbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff
      );
    END IF;

    -- Claim orphaned / unowned inbox work — same predicate shape.
    IF EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_inbox
      WHERE processed_at IS NULL
        AND (instance_id IS NULL OR lease_expiry < v_now)
      LIMIT 1
    ) THEN
      PERFORM __SCHEMA__.claim_orphaned_inbox(
        p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff
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
    -- repeat that check here because the slot-3 2026-06-11 PM measurement
    -- showed the wrapping NOT EXISTS predicate at 42 ms mean (~4-5% of slot-3
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
    ordered_inbox AS (
      SELECT ei.*, ROW_NUMBER() OVER (ORDER BY ei.received_at) AS row_num
      FROM eligible_inbox ei
      ORDER BY ei.received_at
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
    -- v0.661 forensic (gate.hold_duration_ms histogram during a JDX
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
'Phase A focused claim function replacing the claim portion of process_work_batch. Returns work for the calling instance with an empty-call short-circuit that skips orphan-claim scans when all queues are empty (drops idle floor from ~17 ms to ≤ 1 ms). Real claim logic lands in subsequent TDD cycles; old process_work_batch remains the active production path until Phase C migrates IWorkCoordinator callers.';


-- ============================================================================
-- commit_handler_result — atomic transactional bundle for inbox handler completion.
-- Combines: inbox completion + emitted new outbox/inbox messages, in one transaction.
-- This is the only true transactional unit in the work-pump decomposition; everything
-- else in the new function family is independently committable.
-- Phase A of work-pump decomposition.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('emit_event_store_chain');

-- ============================================================================
-- _emit_event_store_chain — extracts Phase 4.5A + 4.6 logic from the legacy
-- process_work_batch so commit_handler_result can call it after storing the
-- handler's emitted outbox messages. Takes the message_ids of those newly-
-- stored outbox rows; copies events with stream_id IS NOT NULL into
-- wh_event_store with sequential versioning; auto-creates wh_perspective_events
-- rows for events that match wh_message_associations.
--
-- Idempotent via ON CONFLICT (event_id) DO NOTHING — re-running with the same
-- message_ids is safe.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('_emit_event_store_chain');

CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain(
  p_outbox_message_ids UUID[],
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  c_field_message_id CONSTANT TEXT := 'MessageId';
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := 'perspective';
BEGIN
  IF p_outbox_message_ids IS NULL OR cardinality(p_outbox_message_ids) = 0 THEN
    RETURN 0;
  END IF;

  -- Per-stream advisory locks. Without these, two concurrent transactions can both
  -- read MAX(version)=N from wh_event_store and both attempt INSERT at version=N+1,
  -- violating idx_event_store_stream UNIQUE(stream_id, version) (PG error 23505). The
  -- legacy process_work_batch ran serially through one ProcessWorkBatchAsync call so
  -- the race didn't exist; the new path lets parallel handlers / strategy flushes
  -- target the same stream concurrently.
  --
  -- Lock order is hashtext(stream_id::text), sorted ascending — ensures deadlock-free
  -- nesting between any pair of transactions touching overlapping stream sets.
  -- pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    SELECT DISTINCT o.stream_id AS sid
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
    ORDER BY o.stream_id
  ) AS streams_to_lock;

  -- Phase 4.5A-equivalent: store outbox events into wh_event_store with sequential versioning.
  -- Phase H step 10 slice 1: ORDER BY o.message_id (UUIDv7 = chronological at the source)
  -- so version assignment matches canonical event_id ordering. Without this, two events stored
  -- "out of wall-clock order" (e.g., one took longer to land) would receive versions that
  -- disagree with their UUIDv7 ordering — perspective cursors advance by version, then later
  -- see an "earlier" event_id and trip the cursor-inversion detector + full replay.
  -- Phase H step 10 slice 3: version is computed via a correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive — if a future refactor weakens or bypasses the lock, the
  -- per-row MAX read still picks up any concurrent commits.
  WITH outbox_events AS (
    SELECT
      o.message_id,
      o.stream_id,
      o.message_type,
      o.event_data,
      o.metadata,
      o.scope,
      o.created_at,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.message_id) AS row_num
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      event_data, metadata, scope, version, created_at
    )
    SELECT
      oe.message_id,
      oe.stream_id,
      oe.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(oe.message_type), ',', 1),
      __SCHEMA__.normalize_event_type(oe.message_type),
      COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload'),
      jsonb_build_object(
        c_field_message_id, COALESCE(oe.event_data::jsonb -> 'id', oe.event_data::jsonb -> c_field_message_id, oe.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb)
      ),
      oe.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = oe.stream_id), 0) + oe.row_num,
      p_now
    FROM outbox_events oe
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream
    -- (stream_id, version) UNIQUE conflict gracefully. Without this, a (stream_id, version)
    -- conflict would bubble up as PG 23505 and fail the whole claim_work tick. With the
    -- advisory lock from slice 2 in place, we shouldn't hit (stream_id, version) conflicts in
    -- practice — but this is the third defensive layer per "all 3 options" guidance. Rows that
    -- conflict are silently skipped; the next claim_work cycle re-attempts them with a fresh
    -- MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id
  )
  SELECT array_agg(event_id) INTO v_stored_event_ids FROM stored_events;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Phase 4.6-equivalent: auto-create perspective events for matching event types.
  -- Phase H step 6 slice 2: populate partition_number via compute_partition(stream_id, p_partition_count)
  -- so claim_orphaned_perspective_events can apply partition-modulo load balancing symmetric
  -- with the outbox / inbox claim paths.
  -- Slice 26.14: route the lease through wh_active_streams. When a live owner is pinned for
  -- the stream, lease to that owner regardless of which instance ran the commit — eliminates
  -- the cross-instance saga race (different instances commit to the same stream, each
  -- selfishly leasing to themselves, drainer races) that produced the residual ~1000 cursor
  -- inversions in JDX run 14. When no live owner is pinned yet (new stream OR stale owner),
  -- fall back to the commit instance so sync paths (UI waiting on a perspective checkpoint)
  -- don't pay claim_orphaned-polling-interval latency on first-event-per-stream.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path — "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry
  FROM __SCHEMA__.wh_event_store es
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. PG buffers NOTIFY until COMMIT and
  -- dedups (channel, payload) within the transaction, so a tx storing 10k events
  -- delivers exactly one wh_committed notification to each LISTEN-er. The stamper
  -- is the only listener; on wake it runs stamp_pending_commit_sequences within ~1ms
  -- instead of waiting for its polling tick.
  PERFORM pg_notify('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain IS
'Helper extracted from process_work_batch Phase 4.5A + 4.6 — copies newly-stored outbox events into wh_event_store with sequential versioning and creates wh_perspective_events rows for matching perspective associations. Called by commit_handler_result after store_outbox_messages so handler-emitted events flow through to perspectives. Slice 26.4: emits NOTIFY wh_committed (deduped per tx) so the commit-order stamper wakes within ~1ms.';

-- ============================================================================
-- _emit_event_store_chain_for_inbox — Phase 4.5B + 4.6 equivalent for inbox-arrival
-- events. The legacy process_work_batch ran this every claim cycle to back-fill the
-- event_store + perspective_events from inbox rows that arrived via TransportConsumerWorker
-- (which inserts into wh_inbox directly and lets self-healing create the event_store row).
-- claim_work calls this after claim_orphaned_inbox so the new path preserves the same
-- guarantee: by the time an inbox row is returned to InboxDispatchWorker, its event_store
-- row exists and perspective_events have been created.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('_emit_event_store_chain_for_inbox');

CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  c_field_message_id CONSTANT TEXT := 'MessageId';
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := 'perspective';
BEGIN
  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Production reproduction observed on JDX BFF
  -- 2026-05-03 during job creation. Lock order is hashtext(stream_id::text), sorted ASC —
  -- ensures deadlock-free nesting between any pair of transactions touching overlapping stream
  -- sets. pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    SELECT DISTINCT i.stream_id AS sid
    FROM __SCHEMA__.wh_inbox i
    WHERE i.instance_id = p_instance_id
      AND i.lease_expiry > p_now
      AND i.processed_at IS NULL
      AND i.is_event = true
      AND i.stream_id IS NOT NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id
      )
    ORDER BY i.stream_id
  ) AS streams_to_lock;

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   • are events (is_event = true)
  --   • have a stream_id
  --   • aren't yet in wh_event_store (idempotent — ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- Phase H step 10 slice 1: ORDER BY i.message_id (UUIDv7 = chronological at the source) so
  -- version assignment matches canonical event_id order. See _emit_event_store_chain above
  -- for the rationale — same fix applies to the inbox backfill path.
  WITH inbox_events AS (
    SELECT
      i.message_id,
      i.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.received_at,
      ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.message_id) AS row_num
    FROM __SCHEMA__.wh_inbox i
    WHERE i.instance_id = p_instance_id
      AND i.lease_expiry > p_now
      AND i.processed_at IS NULL
      AND i.is_event = true
      AND i.stream_id IS NOT NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = i.message_id
      )
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive — see _emit_event_store_chain above for the rationale.
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      event_data, metadata, scope, version, created_at
    )
    SELECT
      ie.message_id,
      ie.stream_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1),
      __SCHEMA__.normalize_event_type(ie.message_type),
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload'),
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ),
      ie.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ie.stream_id), 0) + ie.row_num,
      p_now
    FROM inbox_events ie
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream
    -- (stream_id, version) UNIQUE conflict gracefully. Without this, a (stream_id, version)
    -- conflict would bubble up as PG 23505 and fail the whole claim_work tick. With the
    -- advisory lock from slice 2 in place, we shouldn't hit (stream_id, version) conflicts in
    -- practice — but this is the third defensive layer per "all 3 options" guidance. Rows that
    -- conflict are silently skipped; the next claim_work cycle re-attempts them with a fresh
    -- MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id
  )
  SELECT array_agg(event_id) INTO v_stored_event_ids FROM stored_events;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Auto-create perspective_events for the newly-stored events.
  -- Phase H step 6 slice 2: populate partition_number for symmetric load balancing.
  -- Slice 26.14: route the lease through wh_active_streams (live owner wins; fall back to
  -- commit instance when no live owner). Mirror of the outbox-side change in
  -- _emit_event_store_chain.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path — "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry
  FROM __SCHEMA__.wh_event_store es
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. See _emit_event_store_chain above for
  -- the rationale and dedup semantics. Inbox backfill is generally a smaller fan-in
  -- than outbox-emit, but the NOTIFY is just as cheap and keeps the stamper hot path
  -- responsive whether events arrive locally or via transport.
  PERFORM pg_notify('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox IS
'Phase 4.5B + 4.6 equivalent — back-fills wh_event_store + wh_perspective_events from inbox rows that arrived via TransportConsumerWorker direct-INSERT. Called by claim_work after claim_orphaned_inbox so the new path preserves the legacy self-healing guarantee.';

SELECT __SCHEMA__.drop_all_overloads('commit_handler_result');

CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_result(
  p_request JSONB
) RETURNS VOID AS $$
DECLARE
  v_instance_id UUID := (p_request ->> 'instance_id')::UUID;
  v_now TIMESTAMPTZ := NOW();
  v_lease_expiry TIMESTAMPTZ := v_now + INTERVAL '300 seconds';
  v_partition_count INTEGER := COALESCE((p_request ->> 'partition_count')::INTEGER, 10000);
  -- Production: completions DELETE rows. Debug: retain with processed_at stamped.
  -- Caller (C# OutboxDrainWorker / InboxDrainWorker / completion flushers) reads from
  -- IWorkCoordinatorStrategy.DebugMode and sets this on the request.
  v_debug_mode BOOLEAN := COALESCE((p_request ->> 'debug_mode')::BOOLEAN, FALSE);
  v_inbox_completion JSONB := p_request -> 'inbox_completion';
  v_new_outbox JSONB := COALESCE(p_request -> 'new_outbox_messages', '[]'::JSONB);
  v_new_inbox JSONB := COALESCE(p_request -> 'new_inbox_messages', '[]'::JSONB);
  v_outbox_inserted INTEGER := 0;
  v_inbox_inserted INTEGER := 0;
  -- Slice 27: instance-routed NOTIFY. Collect stream IDs from the new outbox/inbox
  -- payloads so we can route NOTIFYs only to the owning instance(s) of those streams
  -- instead of broadcasting on a global wh_work channel.
  v_outbox_stream_ids UUID[];
  v_inbox_stream_ids UUID[];
BEGIN
  -- 1. Mark inbox completion (if present). process_inbox_completions takes a JSONB array;
  --    wrap the single completion in an array.
  IF v_inbox_completion IS NOT NULL AND v_inbox_completion::TEXT != 'null' THEN
    PERFORM __SCHEMA__.process_inbox_completions(
      jsonb_build_array(v_inbox_completion),
      v_now,
      v_debug_mode  -- production: DELETE; debug: stamp processed_at and retain
    );
  END IF;

  -- 2. Store new outbox messages emitted by the handler.
  IF jsonb_array_length(v_new_outbox) > 0 THEN
    PERFORM __SCHEMA__.store_outbox_messages(
      v_new_outbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
    v_outbox_inserted := jsonb_array_length(v_new_outbox);

    -- Collect stream_ids for slice 27's routed NOTIFY.
    SELECT array_agg(DISTINCT (elem ->> 'StreamId')::UUID)
    INTO v_outbox_stream_ids
    FROM jsonb_array_elements(v_new_outbox) AS elem
    WHERE elem ->> 'StreamId' IS NOT NULL;

    -- 2.5. Event-store auto-create chain. For events with stream_id, copy into
    -- wh_event_store and auto-create wh_perspective_events rows for matching
    -- perspectives. Without this step, handler-emitted events sit in wh_outbox
    -- but never reach perspective consumers.
    DECLARE
      v_emitted_ids UUID[];
    BEGIN
      SELECT array_agg((elem ->> 'MessageId')::UUID)
      INTO v_emitted_ids
      FROM jsonb_array_elements(v_new_outbox) AS elem
      WHERE (elem ->> 'IsEvent')::BOOLEAN = true
        AND elem ->> 'StreamId' IS NOT NULL;

      IF v_emitted_ids IS NOT NULL AND cardinality(v_emitted_ids) > 0 THEN
        PERFORM __SCHEMA__._emit_event_store_chain(
          v_emitted_ids, v_instance_id, v_lease_expiry, v_now, v_partition_count
        );
      END IF;
    END;
  END IF;

  -- 3. Store new inbox messages emitted by the handler (rare path).
  IF jsonb_array_length(v_new_inbox) > 0 THEN
    PERFORM __SCHEMA__.store_inbox_messages(
      v_new_inbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
    v_inbox_inserted := jsonb_array_length(v_new_inbox);

    SELECT array_agg(DISTINCT (elem ->> 'StreamId')::UUID)
    INTO v_inbox_stream_ids
    FROM jsonb_array_elements(v_new_inbox) AS elem
    WHERE elem ->> 'StreamId' IS NOT NULL;
  END IF;

  -- 4. NOTIFY signal types via slice 27's instance-routed helper. Listeners on each
  --    instance subscribe to wh_work_i_<their_instance_id> only — non-owners never
  --    wake. pg_notify still deduplicates (channel, payload) within a transaction at
  --    COMMIT, so 10 000 inserts targeting the same owner collapse to one delivered
  --    notification per (owner, category) pair. Streams without a known owner in
  --    wh_active_streams emit zero NOTIFYs; polling backstop catches that work.
  IF v_outbox_inserted > 0 AND v_outbox_stream_ids IS NOT NULL THEN
    PERFORM __SCHEMA__.notify_instance_owners('outbox', v_outbox_stream_ids);
    -- Event-store auto-create chain may have queued perspective events for the same
    -- streams. Route the perspective wake to the same owners.
    PERFORM __SCHEMA__.notify_instance_owners('perspective', v_outbox_stream_ids);
  END IF;
  IF v_inbox_inserted > 0 AND v_inbox_stream_ids IS NOT NULL THEN
    PERFORM __SCHEMA__.notify_instance_owners('inbox', v_inbox_stream_ids);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.commit_handler_result IS
'Atomic transactional bundle for inbox handler completion. Marks the inbox completion AND stores the new outbox/inbox messages the handler emitted in one transaction; if any step fails the whole bundle rolls back. Handler-emitted events with a stream_id flow through _emit_event_store_chain which copies them into wh_event_store and creates matching wh_perspective_events rows so perspective consumers see them. Emits pg_notify(''wh_work'', category) per category that received new rows, dedup at COMMIT means burst inserts collapse to one delivered notification. Phase A of work-pump decomposition.';


-- ============================================================================
-- commit_handler_batch_bulk — Tier 1 (Option D) optimistic bulk commit. Runs
-- every handler's commit_handler_result in a single transaction with NO
-- savepoint isolation. Failure semantics: all-or-nothing — any error raises
-- out of the whole call and the caller (commit_handler_batch orchestrator)
-- catches it and falls back to the per-handler savepoint loop.
--
-- Why a separate function: keeping the bulk attempt in its own function gives
-- the orchestrator a clean BEGIN..EXCEPTION boundary to catch failures. Inline
-- code in the orchestrator would catch its own RAISE/EXCEPTION too aggressively
-- and complicate the fallback semantics.
--
-- Throughput multiplier on the happy path: zero savepoint overhead (no
-- per-iteration BEGIN..EXCEPTION subtransaction), one outer fsync, one logical
-- statement boundary. Estimated ~50-65% drop on CommitHandlerBatchAsync hold
-- relative to the savepoint-per-handler loop alone.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('commit_handler_batch_bulk');

CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_batch_bulk(
  p_results JSONB
) RETURNS VOID AS $$
DECLARE
  r RECORD;
BEGIN
  IF jsonb_array_length(p_results) = 0 THEN
    RETURN;
  END IF;

  FOR r IN
    SELECT elem
    FROM jsonb_array_elements(p_results) AS elem
  LOOP
    -- NO BEGIN..EXCEPTION wrapper: any error from commit_handler_result raises
    -- straight out of the function, aborting the whole batch atomically. The
    -- orchestrator's EXCEPTION handler is the safety net.
    PERFORM __SCHEMA__.commit_handler_result(r.elem);
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.commit_handler_batch_bulk IS
'Tier 1 (Option D) optimistic bulk commit. Runs each handler-result through commit_handler_result in a single transaction with no savepoint isolation — fastest happy path, all-or-nothing failure semantics. Any error raises out of the whole call so the commit_handler_batch orchestrator can fall back to the savepoint-per-handler loop. Caller should only invoke this directly when isolated per-handler error reporting is not needed.';


-- ============================================================================
-- commit_handler_batch — Two-tier orchestrator (Option D).
--
-- Tier 1 (optimistic): try commit_handler_batch_bulk in a subtransaction. On
-- success, emit success rows for every handler and return — no savepoint
-- overhead in the common case.
--
-- Tier 2 (fallback): if the bulk attempt raises, the subtransaction rolls back
-- cleanly, and we run the original per-handler SAVEPOINT loop. Failing handlers
-- get individual error reports; siblings commit normally. Rare path (handler
-- errors are exceptional), so the bulk-then-loop cost is fine.
--
-- PL/pgSQL BEGIN..EXCEPTION creates implicit subtransactions (savepoints), so
-- both tiers preserve transactional isolation against the outer transaction.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('commit_handler_batch');

CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_batch(
  p_results JSONB
) RETURNS TABLE(
  handler_id UUID,
  success BOOLEAN,
  error_message TEXT
) AS $$
DECLARE
  r RECORD;
  v_handler_id UUID;
BEGIN
  IF jsonb_array_length(p_results) = 0 THEN
    RETURN;
  END IF;

  -- Tier 1: optimistic bulk attempt. Subtransaction scope means a raise inside
  -- commit_handler_batch_bulk rolls back cleanly without aborting the outer
  -- transaction; control falls through to the savepoint loop.
  BEGIN
    PERFORM __SCHEMA__.commit_handler_batch_bulk(p_results);
    RETURN QUERY
    SELECT (elem ->> 'handler_id')::UUID AS handler_id,
           TRUE                          AS success,
           NULL::TEXT                    AS error_message
    FROM jsonb_array_elements(p_results) AS elem;
    RETURN;
  EXCEPTION WHEN OTHERS THEN
    -- Bulk attempt failed; fall through to Tier 2 (savepoint loop). The
    -- subtransaction has already rolled back any partial writes from the bulk
    -- attempt, so the loop starts from a clean state.
    NULL;
  END;

  -- Tier 2: per-handler SAVEPOINT loop (rare path). Identical to the
  -- pre-Option-D body — preserves the per-handler success/failure isolation
  -- contract for the C# flusher.
  FOR r IN
    SELECT elem
    FROM jsonb_array_elements(p_results) AS elem
  LOOP
    v_handler_id := (r.elem ->> 'handler_id')::UUID;

    BEGIN
      -- Implicit SAVEPOINT scope: any exception inside this BEGIN..EXCEPTION block
      -- rolls back ONLY this iteration's writes, then control jumps to the EXCEPTION
      -- branch and the loop continues with the next handler.
      PERFORM __SCHEMA__.commit_handler_result(r.elem);
      RETURN QUERY SELECT v_handler_id AS handler_id, TRUE AS success, NULL::TEXT AS error_message;
    EXCEPTION WHEN OTHERS THEN
      RETURN QUERY SELECT v_handler_id AS handler_id, FALSE AS success, SQLERRM::TEXT AS error_message;
    END;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.commit_handler_batch IS
'Two-tier (Option D) batched commit orchestrator. Tier 1: optimistic bulk attempt via commit_handler_batch_bulk — no savepoint overhead on the happy path. Tier 2: on any error, falls back to per-handler SAVEPOINT loop preserving isolated per-handler success/failure reporting. Returns per-handler (handler_id, success, error_message) for the C# flusher. Single fsync at outer commit on the happy path; bulk-then-loop on the rare failure path.';


-- ============================================================================
-- complete_outbox_published — fire-and-forget batched mark-as-processed for outbox
-- rows after the transport publish succeeds. Coalesced by the C# flush worker.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('complete_outbox_published');

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_outbox_published(
  p_ids UUID[],
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS INTEGER AS $$
DECLARE
  v_affected INTEGER;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  IF p_debug_mode THEN
    -- Debug mode: retain the row for forensics; stamp published_at + processed_at
    -- and set the Published bit. eligible_outbox filters published_at IS NULL so
    -- claim_work treats the row as if deleted on subsequent polls.
    UPDATE __SCHEMA__.wh_outbox
    SET processed_at = NOW(),
        published_at = NOW(),
        status = status | 4,         -- Published flag
        instance_id = NULL,
        lease_expiry = NULL
    WHERE message_id = ANY(p_ids)
      AND processed_at IS NULL;
  ELSE
    -- Production mode: row exits the table on success — structurally immune to
    -- claim_work re-issuing it on the next poll cycle.
    DELETE FROM __SCHEMA__.wh_outbox
    WHERE message_id = ANY(p_ids)
      AND processed_at IS NULL;
  END IF;

  GET DIAGNOSTICS v_affected = ROW_COUNT;
  RETURN v_affected;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_outbox_published IS
'Completes outbox rows after successful transport publish. Production (p_debug_mode=FALSE, default): DELETEs the row — structurally immune to claim_work re-issuing it. Debug mode (p_debug_mode=TRUE): retains the row, stamps published_at + processed_at + Published bit; eligible_outbox filters published_at IS NULL so claim_work skips it. Coalesced + batched by C# OutboxCompletionFlushWorker. Idempotent — unknown ids silently no-op. Returns rows-affected (deletions in prod, updates in debug).';


-- ============================================================================
-- record_heartbeat — decoupled heartbeat function. Separated from claim_work so
-- the heartbeat timer can fire on its own cadence (5 s default) independent of
-- polling. Sub-millisecond UPSERT.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

CREATE OR REPLACE FUNCTION __SCHEMA__.record_heartbeat(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_metadata JSONB DEFAULT '{}'::JSONB
) RETURNS VOID AS $$
DECLARE
  v_stale_cutoff TIMESTAMPTZ := NOW() - INTERVAL '30 seconds';
BEGIN
  INSERT INTO __SCHEMA__.wh_service_instances
    (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
  VALUES
    (p_instance_id, p_service_name, p_host_name, p_process_id, NOW(), NOW(), p_metadata)
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = NOW(),
    metadata = EXCLUDED.metadata;

  -- Opportunistic stale-peer cleanup (Phase H step 6 slice 1). When a peer has gone
  -- silent past the stale cutoff, delete its row and release its leases so live
  -- instances can claim on the next claim_work tick. Cheap pre-check on the indexed
  -- last_heartbeat_at means the heavyweight DELETE+lease-null block only fires when
  -- there's actually a stale peer — most heartbeats no-op past the EXISTS check.
  -- Backstop: MaintenanceWorker also runs cleanup_stale_instances every IntervalMinutes.
  IF EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances
    WHERE last_heartbeat_at < v_stale_cutoff
      AND instance_id != p_instance_id
    LIMIT 1
  ) THEN
    PERFORM __SCHEMA__.cleanup_stale_instances(v_stale_cutoff);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.record_heartbeat IS
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by C# HeartbeatWorker on its own timer (5 s default), independent of polling cadence. Opportunistically cleans up stale peers when detected (cheap pre-check guard). Sub-millisecond cost on the no-stale-peer path.';


-- ============================================================================
-- complete_perspective — batched perspective completion. Combines event-row
-- deletion (status flag = 1 means "completed/processed") with cursor advancement
-- in a single call. Coalesced flush from C# PerspectiveCompletionFlushWorker.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('complete_perspective');

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective(
  p_cursors JSONB,                          -- [{StreamId, PerspectiveName}] for cursor advancement
  p_event_work_ids UUID[],                  -- event_work_id rows to mark complete
  p_debug_mode BOOLEAN DEFAULT FALSE        -- production: DELETE rows; debug: retain with processed_at stamped
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_completions JSONB;
BEGIN
  -- Build the JSONB array of completions from the work-id list. StatusFlags = 1 = Completed.
  IF p_event_work_ids IS NOT NULL AND array_length(p_event_work_ids, 1) IS NOT NULL THEN
    SELECT jsonb_agg(jsonb_build_object('EventWorkId', wid, 'StatusFlags', 1))
    INTO v_completions
    FROM unnest(p_event_work_ids) AS wid;

    PERFORM __SCHEMA__.process_perspective_event_completions(
      COALESCE(v_completions, '[]'::JSONB),
      v_now,
      p_debug_mode  -- production: DELETE; debug: stamp processed_at and retain
    );
  END IF;

  -- Advance cursors for the (StreamId, PerspectiveName) pairs in p_cursors.
  IF p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0 THEN
    PERFORM __SCHEMA__.update_perspective_cursors(p_cursors, p_debug_mode);
  END IF;

  -- Slice 27: routed NOTIFY for downstream wakeups (e.g., perspective-sync awaiters
  -- watching for cursor advancement). Resolve stream IDs from the cursor payload and
  -- route to each stream's owning instance only. Polling backstop catches streams
  -- whose ownership isn't currently known in wh_active_streams.
  IF p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0 THEN
    DECLARE
      v_cursor_stream_ids UUID[];
    BEGIN
      SELECT array_agg(DISTINCT (elem ->> 'StreamId')::UUID)
      INTO v_cursor_stream_ids
      FROM jsonb_array_elements(p_cursors) AS elem
      WHERE elem ->> 'StreamId' IS NOT NULL;

      IF v_cursor_stream_ids IS NOT NULL THEN
        PERFORM __SCHEMA__.notify_instance_owners('perspective', v_cursor_stream_ids);
      END IF;
    END;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_perspective IS
'Batched perspective completion. Production (p_debug_mode=FALSE, default): DELETEs event-work rows and advances cursors. Debug (p_debug_mode=TRUE): retains rows with processed_at stamped (eligible_perspective_events filters processed_at IS NULL → row treated as deleted by claim_work). Single round-trip. Coalesced flush from C# PerspectiveCompletionFlushWorker. Emits pg_notify(''wh_work'', ''perspective'') so peers know cursors moved.';


-- ============================================================================
-- report_failures — category-aware batched failure reporter. Routes to the
-- correct underlying process_*_failures sub-function based on p_category.
-- Coalesced flush from C# FailureFlushWorker.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('report_failures');

CREATE OR REPLACE FUNCTION __SCHEMA__.report_failures(
  p_category TEXT,
  p_failures JSONB
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
BEGIN
  IF p_failures IS NULL OR jsonb_array_length(p_failures) = 0 THEN
    RETURN;
  END IF;

  CASE p_category
    WHEN 'outbox' THEN
      PERFORM __SCHEMA__.process_outbox_failures(p_failures, v_now);
    WHEN 'inbox' THEN
      PERFORM __SCHEMA__.process_inbox_failures(p_failures, v_now);
    WHEN 'perspective_event' THEN
      PERFORM __SCHEMA__.process_perspective_event_failures(p_failures, v_now);
    ELSE
      RAISE EXCEPTION 'report_failures: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.report_failures IS
'Category-aware batched failure reporter. Dispatches to process_outbox_failures / process_inbox_failures / process_perspective_event_failures based on p_category. Coalesced flush from C# FailureFlushWorker. Raises on unknown category.';


-- ============================================================================
-- renew_leases — per-category batched lease extension. Called by C# LeaseRenewalWorker
-- when in-flight items are within lease/3 of expiry. Coalesced flush.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('renew_leases');

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
    WHEN 'inbox' THEN
      UPDATE __SCHEMA__.wh_inbox
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
    WHEN 'perspective_event' THEN
      UPDATE __SCHEMA__.wh_perspective_events
        SET lease_expiry = v_new_expiry
        WHERE event_work_id = ANY(p_ids)
          AND processed_at IS NULL;
    ELSE
      RAISE EXCEPTION 'renew_leases: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;

  GET DIAGNOSTICS v_updated = ROW_COUNT;
  RETURN v_updated;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.renew_leases IS
'Batched lease extension per category. UPDATEs lease_expiry to NOW() + p_lease_seconds for the supplied ids in the chosen category table, only for rows that are not yet processed. Returns rows-affected. Called by C# LeaseRenewalWorker when in-flight items approach lease/3 from expiry.';


-- ============================================================================
-- flush_completions — composite single-round-trip flusher. Combines the per-category
-- completion functions into one call when the C# flusher has multiple categories
-- buffered. Single fsync at outer commit covers all sub-operations.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('flush_completions');

CREATE OR REPLACE FUNCTION __SCHEMA__.flush_completions(
  p_outbox_ids UUID[],
  p_perspective_cursors JSONB,
  p_perspective_event_work_ids UUID[],
  p_failures JSONB           -- [{Category: 'outbox'|'inbox'|'perspective_event', Items: [...]}]
) RETURNS VOID AS $$
DECLARE
  v_failure_group RECORD;
BEGIN
  IF p_outbox_ids IS NOT NULL AND array_length(p_outbox_ids, 1) IS NOT NULL THEN
    PERFORM __SCHEMA__.complete_outbox_published(p_outbox_ids);
  END IF;

  IF (p_perspective_cursors IS NOT NULL AND jsonb_array_length(p_perspective_cursors) > 0)
     OR (p_perspective_event_work_ids IS NOT NULL AND array_length(p_perspective_event_work_ids, 1) IS NOT NULL) THEN
    PERFORM __SCHEMA__.complete_perspective(
      COALESCE(p_perspective_cursors, '[]'::JSONB),
      COALESCE(p_perspective_event_work_ids, ARRAY[]::UUID[])
    );
  END IF;

  IF p_failures IS NOT NULL AND jsonb_array_length(p_failures) > 0 THEN
    FOR v_failure_group IN
      SELECT
        elem->>'Category' AS category,
        elem->'Items' AS items
      FROM jsonb_array_elements(p_failures) AS elem
    LOOP
      PERFORM __SCHEMA__.report_failures(v_failure_group.category, v_failure_group.items);
    END LOOP;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.flush_completions IS
'Composite single-round-trip flusher. Called by C# flush worker when it has multiple completion categories buffered. Combines complete_outbox_published + complete_perspective + report_failures (per-category) into one call. Single fsync at outer commit covers all sub-operations — the latency-and-throughput optimization for high-volume flush ticks.';


-- ============================================================================
-- resolve_sync_inquiries — read-only PerspectiveSyncAwaiter path. Reports pending
-- vs processed event counts per (stream, perspective) pair so the awaiter can
-- wait for cursor advancement. No writes; safe to call without a transaction.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('resolve_sync_inquiries');

CREATE OR REPLACE FUNCTION __SCHEMA__.resolve_sync_inquiries(
  p_inquiries JSONB
) RETURNS TABLE(
  inquiry_id UUID,
  stream_id UUID,
  pending_count INTEGER,
  processed_count INTEGER,
  pending_event_ids UUID[],
  processed_event_ids UUID[]
) AS $$
BEGIN
  IF p_inquiries IS NULL OR jsonb_array_length(p_inquiries) = 0 THEN
    RETURN;
  END IF;

  RETURN QUERY
  SELECT
    (inquiry->>'InquiryId')::UUID,
    (inquiry->>'StreamId')::UUID,
    COUNT(es.event_id) FILTER (WHERE pe.processed_at IS NULL)::INTEGER AS pending_count,
    COUNT(es.event_id) FILTER (WHERE pe.processed_at IS NOT NULL)::INTEGER AS processed_count,
    CASE
      WHEN (inquiry->>'IncludePendingEventIds')::BOOLEAN = true
      THEN ARRAY_AGG(es.event_id) FILTER (WHERE pe.processed_at IS NULL)
      ELSE NULL
    END AS pending_event_ids,
    CASE
      WHEN (inquiry->>'IncludeProcessedEventIds')::BOOLEAN = true
      THEN ARRAY_AGG(es.event_id) FILTER (WHERE pe.processed_at IS NOT NULL)
      ELSE NULL
    END AS processed_event_ids
  FROM jsonb_array_elements(p_inquiries) AS inquiry
  LEFT JOIN __SCHEMA__.wh_event_store es
    ON es.stream_id = (inquiry->>'StreamId')::UUID
    AND (
      (inquiry->'EventIds') IS NULL
      OR jsonb_array_length(inquiry->'EventIds') = 0
      OR es.event_id = ANY(ARRAY(SELECT (jsonb_array_elements_text(inquiry->'EventIds'))::UUID))
    )
    AND (
      (inquiry->'EventTypeFilter') IS NULL
      OR jsonb_array_length(inquiry->'EventTypeFilter') = 0
      OR es.event_type = ANY(ARRAY(SELECT jsonb_array_elements_text(inquiry->'EventTypeFilter')))
    )
  LEFT JOIN __SCHEMA__.wh_perspective_events pe
    ON pe.event_id = es.event_id
    AND pe.perspective_name = inquiry->>'PerspectiveName'
  WHERE
    CASE
      WHEN (inquiry->>'DiscoverPendingFromOutbox')::BOOLEAN = true
        THEN es.event_id IS NOT NULL
      ELSE true
    END
  GROUP BY
    inquiry->>'InquiryId',
    inquiry->>'StreamId',
    inquiry->>'IncludePendingEventIds',
    inquiry->>'IncludeProcessedEventIds';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.resolve_sync_inquiries IS
'PerspectiveSyncAwaiter read-only path. For each inquiry, returns pending vs processed event counts (and optionally the event ID lists) by joining wh_event_store to wh_perspective_events. Filters by stream, optional event-id list, optional event-type-filter, optional perspective. No writes.';
