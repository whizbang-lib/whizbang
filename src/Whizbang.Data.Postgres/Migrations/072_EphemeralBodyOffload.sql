-- Migration: 072_EphemeralBodyOffload.sql
-- Date: 2026-07-15 (E1 ephemeral events — #13b1: optional body-offload table)
-- Description: Additive, strangler-first step toward the pointer+body event-store split. Introduces an
--              OPTIONAL body table that, for now, holds ONLY the bodies of EPHEMERAL events, leaving the
--              durable/Sourced path (inline event_data/metadata in wh_event_store) completely untouched.
--                wh_event_body                — event_id -> (event_data, metadata), the uniform envelope
--                                               body. Empty until an ephemeral event exists. Reapable by
--                                               the consumption-gated reaper (#13b2) without ever touching
--                                               wh_event_store, so the durable path never bloats.
--                wh_event_store.event_data/metadata -> NULLABLE — backward-compatible (existing rows and
--                                               all Sourced writes still carry inline values); ephemeral
--                                               events store NULL inline and put the body in wh_event_body.
--              The emit chain (_emit_event_store_chain[_for_inbox]) branches on the persisted
--              EventFlags.Ephemeral bit ((flags & 8) = 8) to route the body — added in the function-rewrite
--              section below.
-- Dependencies: 061 (the _emit_event_store_chain[_for_inbox] functions + flags column), 062 (persists flags).

-- ── The optional body table ────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_body (
  event_id   UUID  NOT NULL PRIMARY KEY,
  event_data JSONB NOT NULL,
  metadata   JSONB NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_event_body IS
  'E1 #13b1: optional envelope-body table. Currently holds ONLY ephemeral event bodies (the emit chain '
  'offloads them here when (wh_event_store.flags & 8) = 8), leaving the durable inline body on '
  'wh_event_store for Sourced events. The consumption-gated reaper deletes from here once every '
  'perspective has consumed the event; the wh_event_store pointer row stays as the ordering anchor '
  '(reaped body reads back pointer-present/body-NULL). Future (#13b4): migrate Sourced bodies here too '
  'and drop the inline columns, making wh_event_store the pure pointer table.';

-- ── Relax the durable inline body so ephemeral events can null it out ───────────────────────────────
-- Guarded per column for LEDGER-REPLAY idempotency: on first apply (pre-split shape) the columns
-- exist NOT NULL and get relaxed; on a replay against an already-split store — 078 dropped them,
-- and the base ensure's BackfillExempt deliberately never restores migration-owned columns — a
-- bare ALTER would fail with 42703 and wedge the whole init behind the schema-ready gate. A
-- replay reaches here whenever wh_schema_migrations rows are missing or stale (crashed pod
-- mid-init, a restore that skipped the tracking tables, the hash-recheck fallback). Note
-- to_regclass takes __SCHEMA__ verbatim: the placeholder resolves to a quoted identifier, which
-- is exactly the qualified-name form to_regclass parses.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_event_store')
               AND attname = 'event_data' AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_event_store ALTER COLUMN event_data DROP NOT NULL;
  END IF;
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_event_store')
               AND attname = 'metadata' AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_event_store ALTER COLUMN metadata DROP NOT NULL;
  END IF;
END $$;

-- ── Emit chain: branch the body write on the ephemeral flag ─────────────────────────────────────
-- CREATE OR REPLACE the two emit-chain fns (verbatim from 061) so an ephemeral event ((flags & 8) = 8)
-- writes NULL inline event_data/metadata and offloads the real body to wh_event_body. ONLY the CTE that
-- stores the event body changes; advisory locks, perspective-event creation, collective routing, and
-- NOTIFY are byte-identical to 061.

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
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  -- Migration 072: EventFlags.Ephemeral (1 << 3). (flags & 8) = 8 -> body offloaded to wh_event_body.
  c_flag_ephemeral CONSTANT INTEGER := 8;
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
      event_data, metadata, scope, version, created_at, flags
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      -- Migration 072: ephemeral events null the inline body (it lives in wh_event_body); Sourced stay inline.
      CASE WHEN (c.flags & c_flag_ephemeral) = c_flag_ephemeral THEN NULL ELSE c.body_data END,
      CASE WHEN (c.flags & c_flag_ephemeral) = c_flag_ephemeral THEN NULL ELSE c.body_meta END,
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
  -- Migration 072: offload the ephemeral bodies. Joined to stored_events so only events actually
  -- stored this call get a body row; ON CONFLICT keeps re-store idempotent.
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE (c.flags & c_flag_ephemeral) = c_flag_ephemeral
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
  -- inversions observed in production. When no live owner is pinned yet (new stream OR stale owner),
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
-- _emit_event_store_chain_for_inbox — same two additions for the transport-arrival path.
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
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  -- Migration 072: EventFlags.Ephemeral (1 << 3). (flags & 8) = 8 -> body offloaded to wh_event_body.
  c_flag_ephemeral CONSTANT INTEGER := 8;
BEGIN
  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Production reproduction observed on a
  -- consumer's service during job creation. Lock order is hashtext(stream_id::text), sorted ASC —
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
      event_data, metadata, scope, version, created_at, flags
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      -- Migration 072: ephemeral events null the inline body (it lives in wh_event_body); Sourced stay inline.
      CASE WHEN (c.flags & c_flag_ephemeral) = c_flag_ephemeral THEN NULL ELSE c.body_data END,
      CASE WHEN (c.flags & c_flag_ephemeral) = c_flag_ephemeral THEN NULL ELSE c.body_meta END,
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
  -- Migration 072: offload the ephemeral bodies (see outbox fn above).
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE (c.flags & c_flag_ephemeral) = c_flag_ephemeral
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

-- ── Read paths: COALESCE the offloaded ephemeral body back in ──────────────────────────────────
-- CREATE OR REPLACE the two body-read fns (verbatim from 059 / 043) so an ephemeral event's body is
-- read from wh_event_body when the inline wh_event_store body is NULL. Sourced events read inline
-- exactly as before (COALESCE short-circuits on the non-NULL inline value). Only the SELECT body
-- columns + a LEFT JOIN change; the claim CTE / ownership gate / ordering are byte-identical.

SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

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
    COALESCE(es.event_data, eb.event_data)::TEXT,
    COALESCE(es.metadata, eb.metadata)::TEXT,
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
'Mig 059 — atomic per-stream claim+fetch with the grace-windowed unstamped gate (058) AND a single-writer ownership gate: a row whose stream is owned by a different live instance is not claimable, so two pods never apply one stream concurrently (the cross-pod lost-update that stranded a saga in production). Caller-owned / unowned / dead-owner streams stay claimable for clean failover. Supersedes mig 058.';

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
    COALESCE(es.event_data, eb.event_data)::TEXT,
    COALESCE(es.metadata, eb.metadata)::TEXT,
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
