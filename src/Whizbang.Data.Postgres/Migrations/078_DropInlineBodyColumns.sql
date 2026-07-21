-- Migration: 078_DropInlineBodyColumns.sql
-- Date: 2026-07-16
-- Description: Final cut of the full pointer/body split (E1 #13b4-3). 077 made every write go to
--              wh_event_body with the pointer's inline columns always NULL (and backfilled history);
--              this migration DROPS the vestigial wh_event_store.event_data / metadata columns so the
--              invariant is structural — nothing CAN write an inline body again. Every SQL function that
--              still referenced the columns is re-created first: the two emit fns lose the columns from
--              their INSERT lists, the two readers read the body table directly (a reaped ephemeral body
--              still surfaces as NULL through the LEFT JOIN), and reclassify collapses to a pure flags
--              stamp (there is no inline body to move anymore). The 077 backfill function is dropped —
--              its purpose is fulfilled and its body references the dropped columns.
-- Dependencies: 077 (full split writes + backfill), 072 (readers), 074 (reclassify)

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
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
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
  -- Migration 061: o.flags carried into wh_event_store.flags (was dropped here previously).
  WITH outbox_events AS (
    SELECT
      o.message_id,
      o.stream_id,
      o.message_type,
      o.event_data,
      o.metadata,
      o.scope,
      o.flags,
      o.created_at,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.message_id) AS row_num
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  -- Migration 072: materialise the extracted payload + built metadata ONCE, so the pointer INSERT
  -- and the ephemeral body offload read identical values without recomputing the JSON extraction.
  computed AS (
    SELECT
      oe.message_id,
      oe.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(oe.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(oe.message_type) AS event_type,
      COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload') AS body_data,
      jsonb_build_object(
        c_field_message_id, COALESCE(oe.event_data::jsonb -> 'id', oe.event_data::jsonb -> c_field_message_id, oe.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) AS body_meta,
      oe.scope,
      oe.row_num,
      oe.flags
    FROM outbox_events oe
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id), 0) + c.row_num,
      p_now,
      c.flags
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- Migration 077 (full split): offload EVERY body. Joined to stored_events so only events actually
  -- stored this call get a body row; ON CONFLICT keeps re-store idempotent.
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical) — keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
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
  -- inversions in a consumer run 14. When no live owner is pinned yet (new stream OR stale owner),
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

  -- Migration 061: collective-event routing. A collective event carries no perspective
  -- association — route it to the single fixed __collective__ sink so the perspective worker
  -- dispatches it via ICollectiveDispatcher exactly once (the dispatcher fans out to every
  -- matching model handler internally). One sink row per collective event, driven by the
  -- flag bit, independent of associations. Same partition / owner-lease / dedupe semantics as
  -- the association branch above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    c_collective_sink,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry
  FROM __SCHEMA__.wh_event_store es
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
    AND (es.flags & c_flag_collective) = c_flag_collective
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
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
'Helper extracted from process_work_batch Phase 4.5A + 4.6 — copies newly-stored outbox events into wh_event_store with sequential versioning and creates wh_perspective_events rows for matching perspective associations. Called by commit_handler_result after store_outbox_messages so handler-emitted events flow through to perspectives. Slice 26.4: emits NOTIFY wh_committed (deduped per tx) so the commit-order stamper wakes within ~1ms. Migration 061: carries flags into wh_event_store + routes collective events (flags & 1) to the __collective__ sink perspective.';

-- ============================================================================
-- _emit_event_store_chain_for_inbox — same unification for the transport-arrival path.
-- ============================================================================

-- ============================================================================
-- _emit_event_store_chain_for_inbox — same column removal for the transport-arrival path.
-- ============================================================================

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
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
BEGIN
  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Production reproduction observed on a consumer BFF
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
  -- Migration 061: i.flags carried into wh_event_store.flags (was dropped here previously).
  WITH inbox_events AS (
    SELECT
      i.message_id,
      i.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.flags,
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
  -- Migration 072: materialise the extracted payload + built metadata ONCE (see outbox fn above).
  computed AS (
    SELECT
      ie.message_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(ie.message_type) AS event_type,
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload') AS body_data,
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) AS body_meta,
      ie.scope,
      ie.row_num,
      ie.flags
    FROM inbox_events ie
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id), 0) + c.row_num,
      p_now,
      c.flags
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- Migration 077 (full split): offload EVERY body (see outbox fn above).
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical) — keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
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

  -- Migration 061: collective-event routing (inbox path). See _emit_event_store_chain above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    c_collective_sink,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry
  FROM __SCHEMA__.wh_event_store es
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
    AND (es.flags & c_flag_collective) = c_flag_collective
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
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
'Phase 4.5B + 4.6 equivalent — back-fills wh_event_store + wh_perspective_events from inbox rows that arrived via TransportConsumerWorker direct-INSERT. Called by claim_work after claim_orphaned_inbox so the new path preserves the legacy self-healing guarantee. Migration 061: carries flags into wh_event_store + routes collective events (flags & 1) to the __collective__ sink perspective.';

-- ============================================================================
-- Backfill: move pre-077 sourced inline bodies into wh_event_body.
-- ============================================================================
-- Idempotent + re-runnable (ops can invoke it again after restoring legacy data). Copies every
-- remaining inline body to wh_event_body, then NULLs the inline columns only for rows whose body

-- ============================================================================
-- Readers — the body table IS the body now (no inline fallback left to COALESCE).
-- Reproduced verbatim from 072 except the SELECT body columns.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.get_stream_events(
  p_instance_id UUID,
  p_stream_ids UUID[],
  p_now TIMESTAMPTZ DEFAULT NOW(),
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT,
  out_event_work_id UUID,
  out_perspective_name VARCHAR(200),
  out_commit_sequence BIGINT,
  out_attempts INTEGER
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  v_stamp_grace CONSTANT INTERVAL := INTERVAL '5 seconds';
  v_stamp_cutoff TIMESTAMPTZ;
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stamp_cutoff := p_now - v_stamp_grace;

  -- Atomic claim+fetch (slice 25) + grace-windowed unstamped gate (mig 058) + live-owner gate
  -- (mig 059). A row is claimable only if its stream is NOT owned by a DIFFERENT live instance —
  -- enforcing single-writer-per-stream so two pods never apply one stream concurrently.
  WITH eligible AS (
    SELECT pe.event_work_id, pe.instance_id, pe.attempts
    FROM __SCHEMA__.wh_perspective_events pe
    INNER JOIN __SCHEMA__.wh_event_store es
      ON es.stream_id = pe.stream_id
      AND es.event_id = pe.event_id
    WHERE pe.stream_id = ANY(p_stream_ids)
      AND pe.processed_at IS NULL
      AND (pe.scheduled_for IS NULL OR pe.scheduled_for <= p_now)
      AND (
        pe.instance_id IS NULL
        OR pe.lease_expiry < p_now
      )
      AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
      -- mig 059: single-writer gate. Do not claim a row whose stream is owned by a different
      -- LIVE instance (mirrors claim_orphaned's liveness: a wh_service_instances row exists, or a
      -- live LISTEN connection in pg_stat_activity). Caller-owned / unowned / dead-owner streams
      -- stay claimable.
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_active_streams ast
        WHERE ast.stream_id = pe.stream_id
          AND ast.assigned_instance_id IS NOT NULL
          AND ast.assigned_instance_id <> p_instance_id
          AND (
            EXISTS (
              SELECT 1 FROM __SCHEMA__.wh_service_instances si
              WHERE si.instance_id = ast.assigned_instance_id
            )
            OR EXISTS (
              SELECT 1 FROM pg_stat_activity sa
              WHERE sa.application_name = 'whizbang-' || ast.assigned_instance_id::text
            )
          )
      )
    ORDER BY pe.event_work_id
    FOR UPDATE OF pe SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_perspective_events pe
  SET instance_id = p_instance_id,
      lease_expiry = v_lease_expiry,
      attempts = pe.attempts + 1
  FROM eligible e
  WHERE pe.event_work_id = e.event_work_id;

  RETURN QUERY
  SELECT
    pe.stream_id,
    es.event_id,
    es.event_type::TEXT,
    eb.event_data::TEXT,
    eb.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name,
    es.commit_sequence,
    pe.attempts
  FROM __SCHEMA__.wh_perspective_events pe
  INNER JOIN __SCHEMA__.wh_event_store es
    ON pe.stream_id = es.stream_id
    AND pe.event_id = es.event_id
  -- Migration 072: COALESCE the offloaded ephemeral body back in. Sourced events have a non-NULL
  -- inline body so the join contributes nothing; ephemeral events read their body from wh_event_body.
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
  WHERE pe.instance_id = p_instance_id
    AND pe.lease_expiry > p_now
    AND pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
    AND (es.commit_sequence IS NOT NULL OR pe.created_at <= v_stamp_cutoff)
  ORDER BY pe.stream_id, es.commit_sequence ASC NULLS LAST, es.event_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_stream_events IS
'Mig 059 — atomic per-stream claim+fetch with the grace-windowed unstamped gate (058) AND a single-writer ownership gate: a row whose stream is owned by a different live instance is not claimable, so two pods never apply one stream concurrently (the cross-pod lost-update that stranded production saga 019ee73d). Caller-owned / unowned / dead-owner streams stay claimable for clean failover. Supersedes mig 058.';

SELECT __SCHEMA__.drop_all_overloads('fetch_events_by_ids');
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_events_by_ids(
  p_event_ids UUID[]
) RETURNS TABLE(
  out_stream_id UUID,
  out_event_id UUID,
  out_event_type TEXT,
  out_event_data TEXT,
  out_metadata TEXT,
  out_scope TEXT
) AS $$
BEGIN
  RETURN QUERY
  SELECT
    es.stream_id,
    es.event_id,
    es.event_type::TEXT,
    eb.event_data::TEXT,
    eb.metadata::TEXT,
    es.scope::TEXT
  FROM __SCHEMA__.wh_event_store es
  -- Migration 072: COALESCE the offloaded ephemeral body (see get_stream_events above).
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
  WHERE es.event_id = ANY(p_event_ids)
  ORDER BY es.event_id ASC;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.fetch_events_by_ids IS
'Returns event bodies from wh_event_store for the given event_id list, ordered by event_id ASC. The perspective drainer calls this AFTER the cooldown + cursor + inversion filters reduce the prefetched (event_work_id, event_id) tuples to only those needing apply. event_work_id is intentionally NOT returned — drainer pairs results back to its prefetch tuples by event_id in C#. Replaces the per-stream JOIN in get_stream_events for the precise-lookup case.';

-- ============================================================================
-- Reclassification — collapses to a pure flags stamp (no inline body to move).
-- Reproduced from 074 minus the offload + NULL-out steps.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.reclassify_events_ephemeral(p_event_types TEXT[])
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  streams_blocked BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  v_names TEXT[];
  v_events BIGINT := 0;
  v_streams BIGINT := 0;
  v_blocked BIGINT := 0;
BEGIN
  -- Normalize every name the logical type was ever stored under (current + former).
  SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_names
  FROM unnest(p_event_types) AS t;

  IF v_names IS NULL OR array_length(v_names, 1) IS NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT;
    RETURN;
  END IF;

  -- Count streams that would become MIXED: they hold the target type (under any of its names) AND a Sourced
  -- (flags & 8 = 0) event whose type is NOT one of those names. Reclassifying the target there would violate
  -- the homogeneous-stream invariant, so these are skipped by the offload/stamp below and reported here.
  SELECT COUNT(DISTINCT es.stream_id) INTO v_blocked
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_type = ANY(v_names)
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store b
      WHERE b.stream_id = es.stream_id
        AND NOT (b.event_type = ANY(v_names))
        AND (b.flags & c_flag_ephemeral) = 0
    );

  -- Full split (078): bodies live in wh_event_body from birth for BOTH classes, so reclassification is
  -- purely a flags stamp — there is no inline body to offload or null out anymore.
  WITH reclassified AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = es.flags | c_flag_ephemeral
    WHERE es.event_type = ANY(v_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(v_names))
          AND (b.flags & c_flag_ephemeral) = 0
      )
    RETURNING es.stream_id
  )
  SELECT COUNT(*), COUNT(DISTINCT stream_id) INTO v_events, v_streams FROM reclassified;

  RETURN QUERY SELECT v_events, v_streams, v_blocked;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reclassify_events_ephemeral IS
'E1 #13c1 — reclassify a formerly-Sourced event type to Ephemeral across its stored history. Takes the full name set of ONE logical type (current + former names): stamp EventFlags.Ephemeral (flags | 8) so the tier-1 reaper (perform_maintenance Task 8) cleans up the (already-offloaded, full split 077/078) bodies consumption-gated. Skips + counts streams that would become mixed (target type + a Sourced event of another type) to preserve the homogeneous-stream invariant. Idempotent; already-ephemeral rows are left untouched. Returns (events_reclassified, streams_reclassified, streams_blocked).';

-- ============================================================================
-- The cut: drop the backfill fn (references the columns; purpose fulfilled), then the columns.
-- ============================================================================

DROP FUNCTION IF EXISTS __SCHEMA__.wh_backfill_event_bodies();

ALTER TABLE __SCHEMA__.wh_event_store
  DROP COLUMN IF EXISTS event_data,
  DROP COLUMN IF EXISTS metadata;

COMMENT ON TABLE __SCHEMA__.wh_event_store IS
'Narrow append-only event POINTER table (full split, migrations 072/077/078): identity, ordering (version, commit_sequence), classification (event_type, flags incl. ephemeral bit 8), scope, and origin. The (event_data, metadata) body lives in wh_event_body — reaped there for consumed, aged ephemeral events (pointer-present/body-NULL is the rebuild-guard signal), kept forever for Sourced.';
