-- Migration: 115_TagBoundCoalescing.sql
-- Date: 2026-08-18
-- Description: Tag-bound coalescing storage (proposal: tag-bound policies & message coalescing).
--              wh_outbox gains coalesce_group TEXT NULL, stamped at mint for messages whose
--              type carries a tag with an enabled coalesce binding (always together with the
--              ScheduledFor max-delay floor). Hot-path isolation is BY INDEX MEMBERSHIP:
--                * idx_outbox_unprocessed_claiming is re-created with AND coalesce_group IS NULL
--                  in its predicate, so pending singles never enter the index the claim path
--                  scans (zero per-row filtering for normal traffic);
--                * idx_outbox_coalesce_pending (coalesce_group, created_at) serves the coalesce
--                  worker's group scan — only pending singles ever live in it;
--                * claim_work (verbatim from 029 + surgical predicates) and
--                  claim_orphaned_outbox (verbatim from 024 + surgical predicate) add
--                  AND coalesce_group IS NULL so the planner matches the narrowed index and the
--                  pump neither returns nor leases pending singles — even matured ones: the
--                  deadline degrade is an explicit release (coalesce_group = NULL,
--                  scheduled_for = NULL), never an implicit query union;
--                * store_outbox_messages (verbatim from 114 + surgical column) persists the
--                  CoalesceGroup payload key.
-- Dependencies: 024 (claim_orphaned_outbox base), 029 (claim_work base), 031 (the index this
--              file re-creates), 114 (store_outbox_messages base).

-- ============================================================================
-- Column: the group rides the row — filtering must be index-served, so this is
-- a real column, not metadata.
-- ============================================================================

ALTER TABLE __SCHEMA__.wh_outbox ADD COLUMN IF NOT EXISTS coalesce_group TEXT NULL;

COMMENT ON COLUMN __SCHEMA__.wh_outbox.coalesce_group IS
'Tag-bound coalescing (115): the coalesce group a pending single belongs to, stamped at mint together with the scheduled_for max-delay floor. NULL = normal immediately-claimable row. Non-null rows are excluded from the eligible-scan index by predicate; the coalesce worker folds them into composites (marking them processed) or releases them (group + floor cleared) at the deadline.';

-- ============================================================================
-- Indexes. The eligible-scan index (031) is re-created with the coalesce
-- exclusion IN ITS PREDICATE — membership exclusion, measured 6.0 ms -> 0.23 ms
-- for a 100-row claim scan over 20k pending singles (copy-table EXPLAIN ANALYZE).
-- DROP + CREATE because a partial index predicate cannot be altered in place.
-- ============================================================================

DROP INDEX IF EXISTS __SCHEMA__.idx_outbox_unprocessed_claiming;
CREATE INDEX IF NOT EXISTS idx_outbox_unprocessed_claiming
ON __SCHEMA__.wh_outbox (partition_number, instance_id, lease_expiry)
WHERE processed_at IS NULL AND coalesce_group IS NULL;

-- The coalesce worker's own tiny index: group stats + oldest-first fetch. Only
-- coalesce-pending rows ever live in it.
CREATE INDEX IF NOT EXISTS idx_outbox_coalesce_pending
ON __SCHEMA__.wh_outbox (coalesce_group, created_at)
WHERE coalesce_group IS NOT NULL AND processed_at IS NULL;

-- ============================================================================
-- store_outbox_messages — VERBATIM from 114 (the previous last word) plus the
-- coalesce_group column through the parse/insert. This file is now the last
-- word on store_outbox_messages; the redefinition closure re-runs it after any
-- re-run of 020/021/029/046/062/114.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('store_outbox_messages');

CREATE OR REPLACE FUNCTION __SCHEMA__.store_outbox_messages(
  p_messages JSONB,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_newly_created BOOLEAN
) AS $$
#variable_conflict use_column
DECLARE
  v_msg RECORD;
  v_partition INTEGER;
  v_was_new BOOLEAN;
  v_inserted_event_ids UUID[] := ARRAY[]::UUID[];
  -- 114 edge-notify state: streams probed once per call (first encounter, BEFORE this
  -- call inserts for them), the probed-empty subsets per category, and the notify sets.
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_outbox_streams UUID[] := ARRAY[]::UUID[];
  v_empty_persp_streams UUID[] := ARRAY[]::UUID[];
  v_notify_outbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_persp_streams UUID[] := ARRAY[]::UUID[];
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>'MessageId')::UUID as msg_id,
      elem->>'Destination' as destination,
      elem->>'MessageType' as message_type,
      elem->>'EnvelopeType' as envelope_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>'StreamId')::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- EventFlags (062): persisted so migration 061's collective routing can see (flags & 1). Robust to
      -- numeric (default System.Text.Json enum) or [Flags] string serialization of EventFlags.
      CASE
        WHEN elem->>'Flags' IS NULL OR elem->>'Flags' = '' THEN 0
        WHEN elem->>'Flags' ~ '^[0-9]+$' THEN (elem->>'Flags')::INTEGER
        WHEN elem->>'Flags' ILIKE '%Collective%' THEN 1
        ELSE 0
      END as flags,
      NULLIF(elem->>'ScheduledFor', '')::TIMESTAMPTZ as scheduled_for,
      -- 115 tag-bound coalescing: the group a pending single belongs to (NULL for normal rows).
      NULLIF(elem->>'CoalesceGroup', '') as coalesce_group
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>'StreamId')::UUID NULLS FIRST, (elem->>'MessageId')::UUID
  LOOP
    IF v_msg.stream_id IS NOT NULL THEN
      v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
    ELSE
      v_partition := NULL;
    END IF;

    -- 114: emptiness probe, once per distinct stream per call, BEFORE this call's first
    -- insert for that stream (the loop is stream-ordered, so first encounter precedes all
    -- of the stream's inserts). FOR SHARE serializes against an in-flight completion of
    -- the last pending row — see the header's MVCC lost-wakeup guard.
    IF v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
      v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

      PERFORM 1 FROM __SCHEMA__.wh_outbox o
        WHERE o.stream_id = v_msg.stream_id
          AND o.processed_at IS NULL
          AND o.published_at IS NULL
          AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
        LIMIT 1
        FOR SHARE OF o;
      IF NOT FOUND THEN
        v_empty_outbox_streams := array_append(v_empty_outbox_streams, v_msg.stream_id);
      END IF;

      PERFORM 1 FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = v_msg.stream_id
          AND pe.processed_at IS NULL
        LIMIT 1
        FOR SHARE OF pe;
      IF NOT FOUND THEN
        v_empty_persp_streams := array_append(v_empty_persp_streams, v_msg.stream_id);
      END IF;
    END IF;

    INSERT INTO __SCHEMA__.wh_outbox (
      message_id,
      destination,
      message_type,
      envelope_type,
      event_data,
      metadata,
      scope,
      stream_id,
      partition_number,
      is_event,
      flags,
      status,
      attempts,
      created_at,
      instance_id,
      lease_expiry,
      scheduled_for,
      coalesce_group
    ) VALUES (
      v_msg.msg_id,
      v_msg.destination,
      v_msg.message_type,
      v_msg.envelope_type,
      COALESCE(v_msg.envelope_data, '{}'::jsonb),
      COALESCE(v_msg.metadata, '{}'::jsonb),
      COALESCE(v_msg.scope, 'null'::jsonb),
      v_msg.stream_id,
      v_partition,
      COALESCE(v_msg.is_event, false),
      COALESCE(v_msg.flags, 0),
      1,  -- Stored flag
      0,  -- Initial attempts
      p_now,
      p_instance_id,  -- Immediate lease
      p_lease_expiry,
      v_msg.scheduled_for,
      v_msg.coalesce_group
    )
    ON CONFLICT ON CONSTRAINT wh_outbox_pkey DO NOTHING;

    GET DIAGNOSTICS v_was_new = ROW_COUNT;

    IF v_was_new AND COALESCE(v_msg.is_event, false) AND v_msg.stream_id IS NOT NULL THEN
      v_inserted_event_ids := array_append(v_inserted_event_ids, v_msg.msg_id);
    END IF;

    -- 114: a genuinely-new, drainable-now row on a probed-empty stream is the
    -- empty→non-empty edge. Future-scheduled rows are not drainable now, so they
    -- do not ring (they surface via the scheduled-retry NOTIFY / poll when due).
    IF v_was_new AND v_msg.stream_id IS NOT NULL
       AND (v_msg.scheduled_for IS NULL OR v_msg.scheduled_for <= p_now)
       AND v_msg.stream_id = ANY(v_empty_outbox_streams)
       AND NOT (v_msg.stream_id = ANY(v_notify_outbox_streams)) THEN
      v_notify_outbox_streams := array_append(v_notify_outbox_streams, v_msg.stream_id);
    END IF;

    -- Pinning is ownership/routing, unchanged by 114 (the cold-notify tracking that
    -- used to ride this block is gone — the notify decision now keys on queue state).
    IF v_was_new AND v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
      INSERT INTO __SCHEMA__.wh_active_streams
        (stream_id, partition_number, assigned_instance_id, last_activity_at)
      VALUES
        (v_msg.stream_id, COALESCE(v_partition, 0), p_instance_id, p_now)
      ON CONFLICT (stream_id) DO NOTHING;
    END IF;

    RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, v_was_new AS was_newly_created;
  END LOOP;

  IF cardinality(v_inserted_event_ids) > 0 THEN
    PERFORM __SCHEMA__._emit_event_store_chain(
      v_inserted_event_ids,
      p_instance_id,
      p_lease_expiry,
      p_now,
      p_partition_count
    );

    -- 114: the perspective edge is decided AFTER the emit chain, so it rings only for
    -- streams whose perspective queue was empty before this call AND for which this
    -- call actually created work items (our own inserts are visible in-transaction).
    -- Association-less event types create no work and therefore never ring here.
    IF cardinality(v_empty_persp_streams) > 0 THEN
      SELECT COALESCE(array_agg(DISTINCT pe.stream_id), ARRAY[]::UUID[])
        INTO v_notify_persp_streams
        FROM __SCHEMA__.wh_perspective_events pe
        WHERE pe.stream_id = ANY(v_empty_persp_streams)
          AND pe.processed_at IS NULL;
    END IF;
  END IF;

  IF cardinality(v_notify_outbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('outbox', v_notify_outbox_streams);
  END IF;
  IF cardinality(v_notify_persp_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('perspective', v_notify_persp_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.store_outbox_messages IS
'Stores new outbox messages (114: edge-notify — the doorbell rings when a store creates a stream''s first pending row, judged by the drain-fetch eligibility predicate with a FOR SHARE probe; piled-up rows stay silent, drained streams re-arm the edge). Optionally with immediate lease (p_instance_id + p_lease_expiry non-null) or without (NULL params — claim_orphaned_outbox picks them up). Calls _emit_event_store_chain for newly-inserted events; the perspective doorbell rings only when the chain created work for a previously-empty perspective queue. Returns (message_id, stream_id, was_newly_created) per row. 115: persists the CoalesceGroup payload key to wh_outbox.coalesce_group (tag-bound coalescing).';

-- ============================================================================
-- claim_work — VERBATIM from 029 (its only prior definition) plus the three
-- surgical coalesce_group predicates (empty-call probe, orphan-claim guard,
-- eligible scan).
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
'Phase A focused claim function replacing the claim portion of process_work_batch. Returns work for the calling instance with an empty-call short-circuit that skips orphan-claim scans when all queues are empty (drops idle floor from ~17 ms to ≤ 1 ms). Real claim logic lands in subsequent TDD cycles; old process_work_batch remains the active production path until Phase C migrates IWorkCoordinator callers. 115: coalesce-pending outbox rows (coalesce_group IS NOT NULL) are excluded from the empty-call probe, the orphan-claim guard, and the eligible scan — they surface only via the coalesce worker''s fold or an explicit release.';

-- ============================================================================
-- claim_orphaned_outbox — VERBATIM from 024 (its only prior definition) plus
-- the surgical coalesce_group predicate, so pending singles are never leased.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('claim_orphaned_outbox');

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
    SET last_activity_at = p_now
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
      (stream_id, partition_number, assigned_instance_id, last_activity_at)
    SELECT DISTINCT ON (sub.stream_id) sub.stream_id, sub.partition_number, p_instance_id, p_now
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
          END
    RETURNING ast.stream_id AS pinned_stream_id
  )
  SELECT c.c_message_id AS message_id, c.c_stream_id AS stream_id FROM claimed c;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_orphaned_outbox IS
'Claims orphaned outbox messages with expired or null leases. Owner-preferring: a stream''s live owner always claims its messages (FIFO per-stream, immune to rank churn from scale events). Partition-modulo load balancing applies only to streams with no live owner. An abandoned (non-heartbeating) instance''s lease does NOT block cross-instance claims, giving SIGKILL-tolerant recovery bounded by the stale threshold rather than the 300 s active-streams lease. Returns claimed message IDs for Orphaned flag in orchestrator response. 115: coalesce-pending rows (coalesce_group IS NOT NULL) are never claimed or leased — the coalesce worker owns them until fold or release. p_max_rows bounds ACQUISITION (oldest-first, FOR UPDATE SKIP LOCKED); NULL means unlimited.';
