-- Migration: 146_DoorbellsRingAfterCommit
-- Date: 2026-09-08
-- Description: no hot-path transaction issues NOTIFY; doorbells queue and ring in their own tiny commit (#720).
--
--   PostgreSQL serializes the commit of every transaction that issued NOTIFY: PreCommit_Notify takes an
--   AccessExclusiveLock on the database object (pg_locks: locktype=object, classid=pg_database, objid=0)
--   and holds it until the transaction commits, so that queue entries appear in commit order. Every hot
--   path here rang a doorbell inside its own transaction (the stores, the handler commits, the perspective
--   completions, the commit-sequence stamp, the claim), so under load every notifying commit queued behind
--   the longest one. Measured live: handler-commit flushes of 6 to 27 seconds, complete_perspective and
--   the inbox fetch waiting on that one lock behind a 50-item commit batch, and a bulk ingest whose
--   progress froze for the length of each batch. Migration 143 removed the row lock on wh_notify_state;
--   this database-wide lock was underneath it.
--
--   Rule: the hot transaction never calls pg_notify. It calls _queue_doorbell, which inserts one row into
--   the unlogged wh_doorbell_queue (a plain insert; no lock beyond the row). After the hot statement's
--   transaction has committed, the driver runs ring_doorbells() as its own autocommit statement: it takes
--   a bounded batch of queued rows FOR UPDATE SKIP LOCKED, coalesces identical (channel, payload) pairs,
--   deletes them, and issues the pg_notify calls. That transaction holds the NOTIFY lock for the
--   microseconds it takes to commit a few notifies, not for the length of a batch.
--
--   Properties kept: the debounce decision and its bookkeeping (137, 141, 143) are unchanged, only the
--   final ring moves; a doorbell queued by a transaction that rolls back is rolled back with it (no
--   spurious ring for work that never committed); a ring left in the queue by a driver that died between
--   its commit and its ring is picked up by the next ring from any instance, and the claim poll remains
--   the safety net beneath all of it; the queue is unlogged because a crash loses only a doorbell, never
--   the work it pointed at.
--
--   Also here (#727): store_inbox_messages treats a zero-GUID SourceServiceId as unknown so the existing
--   fallback to this service's id applies; producers now stamp their id (driver change in the same PR).
--
--   _notify_debounced (143), _emit_event_store_chain (087), _emit_event_store_chain_for_inbox (100),
--   _notify_dead_letter_ready (056) and store_inbox_messages (140) are reproduced verbatim except for the
--   lines named above. cleanup_stale_instances (106) still notifies directly: it runs on the maintenance
--   path, never inside a hot transaction, and its eviction broadcast is rare by nature.

CREATE UNLOGGED TABLE IF NOT EXISTS __SCHEMA__.wh_doorbell_queue (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  channel TEXT NOT NULL,
  payload TEXT NOT NULL DEFAULT '',
  queued_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE __SCHEMA__.wh_doorbell_queue IS
  'Doorbells queued by hot-path transactions (146). Rung by ring_doorbells() in its own commit so the hot path '
  'never holds the NOTIFY serialization lock. Unlogged on purpose: a crash loses a doorbell, never work; the '
  'claim poll is the safety net.';

CREATE OR REPLACE FUNCTION __SCHEMA__._queue_doorbell(
  p_channel TEXT,
  p_payload TEXT
) RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_doorbell_queue (channel, payload) VALUES (p_channel, COALESCE(p_payload, ''));
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__._queue_doorbell(TEXT, TEXT) IS
  'Records a doorbell to ring after the caller''s transaction commits (146). A plain insert: takes no lock the '
  'caller does not already hold and never the NOTIFY serialization lock. Rolls back with the caller.';

CREATE OR REPLACE FUNCTION __SCHEMA__.ring_doorbells(
  p_max INTEGER DEFAULT 1000
) RETURNS INTEGER AS $$
DECLARE
  v_rung INTEGER := 0;
  r RECORD;
BEGIN
  -- Take a bounded batch without waiting on another ringer, coalesce identical rings, delete, notify.
  FOR r IN
    WITH taken AS (
      DELETE FROM __SCHEMA__.wh_doorbell_queue q
      WHERE q.id IN (
        SELECT id FROM __SCHEMA__.wh_doorbell_queue
        ORDER BY id
        LIMIT GREATEST(p_max, 1)
        FOR UPDATE SKIP LOCKED
      )
      RETURNING q.channel, q.payload
    )
    SELECT DISTINCT channel, payload FROM taken
  LOOP
    PERFORM pg_notify(r.channel, r.payload);
    v_rung := v_rung + 1;
  END LOOP;
  RETURN v_rung;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.ring_doorbells(INTEGER) IS
  'Rings queued doorbells (146): deletes up to p_max queued rows FOR UPDATE SKIP LOCKED, coalesces identical '
  '(channel, payload) pairs and issues pg_notify for each. Called by the driver as its own autocommit statement '
  'right after a hot-path call, so the NOTIFY serialization lock is held only for this tiny commit. Returns the '
  'number of distinct notifications sent. Any instance may ring what any other queued.';

CREATE OR REPLACE FUNCTION __SCHEMA__._notify_debounced(
  p_instance_id UUID,
  p_kind TEXT,
  p_payload TEXT,
  p_window INTEGER
) RETURNS VOID AS $$
DECLARE
  v_channel TEXT := 'wh_work_i_' || p_instance_id::text;
  v_now TIMESTAMPTZ := NOW();
  v_live BOOLEAN;
  v_last_attempt TIMESTAMPTZ;
  v_last_work TIMESTAMPTZ;
  v_rapid_run INTEGER;
  v_floor_ms INTEGER;
  v_rapid_gap_ms INTEGER;
  v_churn_run INTEGER;
  v_gap_ms DOUBLE PRECISION;
  v_effective_ms INTEGER;
  v_suppress BOOLEAN := FALSE;
BEGIN
  -- Adaptive knobs (the ceiling arrives as p_window; read the floor/rapid-gap/churn once).
  SELECT COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_debounce_floor_ms'), 50),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_rapid_gap_ms'), 100),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_churn_run'), 5)
  INTO v_floor_ms, v_rapid_gap_ms, v_churn_run;

  -- Only a LIVE target may ever be suppressed: a corpse's fresh watermark must not strand work
  -- — its doorbell must fire so the deterministic re-target path engages (130).
  SELECT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances si
    WHERE si.instance_id = p_instance_id
      AND si.last_heartbeat_at > v_now - INTERVAL '30 seconds')
  INTO v_live;

  -- This target's current state, taken WITHOUT waiting (143). The row lock we take here lives
  -- until the caller's transaction ends, which is exactly why the next writer must skip it.
  SELECT last_attempt_at, last_work_at, rapid_run
    INTO v_last_attempt, v_last_work, v_rapid_run
  FROM __SCHEMA__.wh_notify_state
  WHERE instance_id = p_instance_id AND payload_kind = p_kind
  FOR UPDATE SKIP LOCKED;

  IF NOT FOUND THEN
    -- Held by another writer (it is ringing or sliding this target right now), or the row does
    -- not exist yet. Held: ring and leave the state to the holder. Absent: one creator at a time;
    -- a loser rings and returns rather than queue behind the winner's transaction.
    IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_notify_state
               WHERE instance_id = p_instance_id AND payload_kind = p_kind)
       OR NOT pg_try_advisory_xact_lock(hashtext(p_instance_id::text), hashtext(p_kind)) THEN
      PERFORM __SCHEMA__._queue_doorbell(v_channel, p_payload);
      RETURN;
    END IF;

    -- Born without a found-work watermark: only claim_work arms last_work_at (131/137).
    INSERT INTO __SCHEMA__.wh_notify_state
      (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run, effective_window_ms,
       fired_count, suppressed_count)
    VALUES (p_instance_id, p_kind, NULL, NULL, 0, 0, 0, 0)
    ON CONFLICT (instance_id, payload_kind) DO NOTHING;

    IF NOT FOUND THEN
      -- Committed by someone else between the probe and the insert: theirs, not ours.
      PERFORM __SCHEMA__._queue_doorbell(v_channel, p_payload);
      RETURN;
    END IF;
    -- The row is ours (uncommitted insert): state is empty, the same as a first doorbell after idle.
  END IF;

  -- VOLUME axis: advance the rapid run while doorbells arrive closer than the rapid gap; reset
  -- on the first calm gap. No prior attempt = calm (a lone doorbell after idle).
  v_gap_ms := CASE WHEN v_last_attempt IS NULL THEN NULL
                   ELSE EXTRACT(EPOCH FROM (v_now - v_last_attempt)) * 1000 END;
  IF v_gap_ms IS NOT NULL AND v_gap_ms < v_rapid_gap_ms THEN
    v_rapid_run := COALESCE(v_rapid_run, 0) + 1;
  ELSE
    v_rapid_run := 0;
  END IF;

  -- TIME axis: floor window normally; escalate to the ceiling (p_window seconds) once the run
  -- trips churn. p_window <= 0 is the global OFF switch — suppression disabled (floor ignored).
  IF p_window <= 0 THEN
    v_effective_ms := 0;
  ELSIF v_rapid_run >= v_churn_run THEN
    v_effective_ms := p_window * 1000;
  ELSE
    v_effective_ms := GREATEST(v_floor_ms, 0);
  END IF;

  -- Suppress iff: live AND the drainer is genuinely draining this kind (found-work watermark
  -- fresh within the EFFECTIVE window). A NULL last_work_at (fire-born row, claim_work never
  -- armed) can never satisfy this — "a fire never arms suppression" (131), by construction.
  IF v_live AND v_effective_ms > 0 AND v_last_work IS NOT NULL
     AND v_last_work > v_now - (v_effective_ms * INTERVAL '1 millisecond') THEN
    v_suppress := TRUE;
  END IF;

  IF v_suppress THEN
    -- Slide the found-work watermark (a suppressed store IS work the linger poll will find) and
    -- record the attempt + rate state + the suppressed count for OTel. The row is ours.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_work_at = v_now,
        last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        suppressed_count = suppressed_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_kind;
  ELSE
    PERFORM __SCHEMA__._queue_doorbell(v_channel, p_payload);
    -- Record the attempt + rate state + fired count WITHOUT arming suppression: last_work_at is
    -- left untouched (only claim_work arms it). The row is ours.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        fired_count = fired_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_kind;
    -- Opportunistic hygiene on the rare fire path: rows for long-departed instances, aged by
    -- most-recent activity (either watermark) so fire-born NULL-last_work_at rows also expire.
    -- Never waits: a row someone holds is, by definition, not long-departed.
    DELETE FROM __SCHEMA__.wh_notify_state ns
    WHERE ns.ctid IN (
      SELECT stale.ctid FROM __SCHEMA__.wh_notify_state stale
      WHERE GREATEST(COALESCE(stale.last_work_at, 'epoch'::timestamptz),
                     COALESCE(stale.last_attempt_at, 'epoch'::timestamptz)) < v_now - INTERVAL '7 days'
      FOR UPDATE SKIP LOCKED);
  END IF;
END;
$$ LANGUAGE plpgsql;

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
  c_field_message_id CONSTANT TEXT := 'MessageId';  -- NOSONAR S1192: the field name recurs per function; PL/pgSQL has no file-level constants
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
      ) || CASE
        WHEN oe.metadata IS NOT NULL
             AND jsonb_typeof(oe.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((oe.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS body_meta,
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
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT
      '00000000-0000-0000-0000-000000000000'::uuid,  -- NOSONAR S1192: the no-source-service sentinel recurs per function; PL/pgSQL has no file-level constants
      COALESCE(c.scope::jsonb ->> 't', ''),
      c.event_type,
      c.stream_id,
      bit_xor(hashtextextended(c.message_id::text, 0)),
      bit_xor(hashtextextended(c.message_id::text, 1)),
      COUNT(*)::int,
      p_now
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      AND COALESCE((c.body_meta ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
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
  -- inversions in a production run. When no live owner is pinned yet (new stream OR stale owner),
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
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  v_local_service_id UUID;
  c_field_message_id CONSTANT TEXT := 'MessageId';
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := 'perspective';
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
BEGIN
  -- Migration 087: resolve the LOCAL service id once. store_inbox_messages (062) COALESCEs a
  -- missing envelope SourceServiceId to the local id, so a wh_inbox row attributed to SELF (or
  -- zero) is a locally-originated event (loopback) — its origin_service_id must stay NULL,
  -- matching the 046 contract ("NULL for locally-originated events").
  SELECT service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config LIMIT 1;

  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Reproduced in production on a consumer's
  -- service during job creation. Lock order is hashtext(stream_id::text), sorted ASC —
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
      i.source_service_id,
      i.source_commit_sequence,
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
      ) || CASE
        WHEN ie.metadata IS NOT NULL
             AND jsonb_typeof(ie.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((ie.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS body_meta,
      ie.scope,
      ie.row_num,
      ie.flags,
      -- Migration 087: normalize the received origin — self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = '00000000-0000-0000-0000-000000000000'::uuid
             OR ie.source_service_id = v_local_service_id
        THEN NULL
        ELSE ie.source_service_id
      END AS origin_service_id,
      NULLIF(ie.source_commit_sequence, 0) AS origin_commit_sequence
    FROM inbox_events ie
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, origin_service_id, origin_commit_sequence
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
      c.flags,
      -- Migration 087: stamp the origin identity the transport delivered (046 columns were never
      -- populated by the emit chain before this — consumer-side origin-keyed verification needs them).
      c.origin_service_id,
      CASE WHEN c.origin_service_id IS NULL THEN NULL ELSE c.origin_commit_sequence END
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
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT
      COALESCE(c.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid),
      COALESCE(c.scope::jsonb ->> 't', ''),
      c.event_type,
      c.stream_id,
      bit_xor(hashtextextended(c.message_id::text, 0)),
      bit_xor(hashtextextended(c.message_id::text, 1)),
      COUNT(*)::int,
      p_now
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      AND COALESCE((c.body_meta ->> 'deliveryGuarantee')::integer, 0) <> 1
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
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

  -- 100: reconcile self-declares the rewind. Any of THIS invocation's fresh work items whose
  -- event id slots BELOW its cursor's last-applied event is a straggler by construction — a
  -- backfilled event keeps its ORIGINAL id, while its fresh local commit_sequence sits above
  -- the cursor and hides it from the runtime inversion detector forever. Flag the cursor
  -- RewindRequired with the earliest straggler as trigger (min-merge with any existing
  -- trigger, mirroring complete_perspective_checkpoint's straggler path); the worker's
  -- existing rewind routing replays the stream through the corrected order.
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN s.straggler_event_id
        WHEN s.straggler_event_id < pc.rewind_trigger_event_id THEN s.straggler_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
  FROM (
    SELECT pe.stream_id, pe.perspective_name,
           (array_agg(pe.event_id ORDER BY pe.event_id))[1] AS straggler_event_id
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_perspective_cursors c
      ON c.stream_id = pe.stream_id AND c.perspective_name = pe.perspective_name
    WHERE pe.event_id = ANY(v_stored_event_ids)
      AND pe.processed_at IS NULL
      AND c.last_event_id IS NOT NULL
      AND pe.event_id < c.last_event_id
    GROUP BY pe.stream_id, pe.perspective_name
  ) s
  WHERE pc.stream_id = s.stream_id AND pc.perspective_name = s.perspective_name;

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
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION __SCHEMA__._notify_dead_letter_ready() RETURNS TRIGGER AS $$
BEGIN
  -- 146: queued, rung after the inserting transaction commits (see ring_doorbells).
  INSERT INTO __SCHEMA__.wh_doorbell_queue (channel, payload)
  SELECT 'wh_work_i_' || instance_id::text, 'deadletter'
  FROM __SCHEMA__.wh_service_instances
  WHERE last_heartbeat_at > NOW() - INTERVAL '5 minutes';
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION __SCHEMA__.store_inbox_messages(
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
  c_field_flags CONSTANT TEXT := 'Flags';
  v_msg RECORD;
  v_partition INTEGER;
  v_observations INTEGER;  -- 1 on first sight; N on the Nth redelivery
  v_probed_streams UUID[] := ARRAY[]::UUID[];
  v_empty_inbox_streams UUID[] := ARRAY[]::UUID[];
  v_notify_inbox_streams UUID[] := ARRAY[]::UUID[];
BEGIN
  IF jsonb_array_length(p_messages) = 0 THEN RETURN; END IF;

  FOR v_msg IN
    SELECT
      (elem->>'MessageId')::UUID as msg_id,
      elem->>'HandlerName' as handler_name,
      elem->>'EnvelopeType' as envelope_type,
      elem->>'MessageType' as message_type,
      elem->'Envelope' as envelope_data,
      elem->'Metadata' as metadata,
      elem->'Scope' as scope,
      (elem->>'StreamId')::UUID as stream_id,
      (elem->>'IsEvent')::BOOLEAN as is_event,
      -- EventFlags (062): same robust read as store_outbox_messages so collective events delivered
      -- cross-service via the inbox also route to the __collective__ sink.
      CASE
        WHEN elem->>c_field_flags IS NULL OR elem->>c_field_flags = '' THEN 0
        WHEN elem->>c_field_flags ~ '^[0-9]+$' THEN (elem->>c_field_flags)::INTEGER
        WHEN elem->>c_field_flags ILIKE '%Collective%' THEN 1
        ELSE 0
      END as flags,
      -- 146 (#727): a zero GUID is "unknown", not a producer; COALESCE below then falls back to this service.
      NULLIF((elem->>'SourceServiceId')::UUID, '00000000-0000-0000-0000-000000000000'::UUID) as source_service_id,
      (elem->>'SourceCommitSequence')::BIGINT as source_commit_sequence
    FROM jsonb_array_elements(p_messages) as elem
    ORDER BY (elem->>'StreamId')::UUID NULLS FIRST, (elem->>'MessageId')::UUID
  LOOP
    -- 121: DO UPDATE (was DO NOTHING) so a redelivery is COUNTED rather than silently swallowed.
    -- RETURNING gives the post-write count on both arms, so newness is read from the value
    -- (= 1) instead of from ROW_COUNT, which DO UPDATE would report as 1 either way.
    INSERT INTO __SCHEMA__.wh_message_deduplication AS dedup
      (message_id, first_seen_at, observation_count)
    VALUES (v_msg.msg_id, p_now, 1)
    ON CONFLICT ON CONSTRAINT wh_message_deduplication_pkey DO UPDATE
      SET observation_count = dedup.observation_count + 1
    RETURNING dedup.observation_count INTO v_observations;

    IF v_observations = 1 THEN
      IF v_msg.stream_id IS NOT NULL THEN
        v_partition := __SCHEMA__.compute_partition(v_msg.stream_id, p_partition_count);
      ELSE
        v_partition := NULL;
      END IF;

      -- 114: emptiness probe — see store_outbox_messages; inbox pending = not processed
      -- and schedule-eligible (the drain-fetch predicate minus the lease dimension).
      IF v_msg.stream_id IS NOT NULL AND NOT (v_msg.stream_id = ANY(v_probed_streams)) THEN
        v_probed_streams := array_append(v_probed_streams, v_msg.stream_id);

        PERFORM 1 FROM __SCHEMA__.wh_inbox i
          WHERE i.stream_id = v_msg.stream_id
            AND i.processed_at IS NULL
            AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
          LIMIT 1
          FOR SHARE OF i SKIP LOCKED;   -- 140: a locked pending row is being completed; treat as empty and ring
        IF NOT FOUND THEN
          v_empty_inbox_streams := array_append(v_empty_inbox_streams, v_msg.stream_id);
        END IF;
      END IF;

      INSERT INTO __SCHEMA__.wh_inbox (
      message_id,
      handler_name,
      message_type,
      event_data,
      metadata,
      scope,
      stream_id,
      partition_number,
      is_event,
      flags,
      status,
      attempts,
      received_at,
      instance_id,
      lease_expiry,
      source_service_id,
      source_commit_sequence
    ) VALUES (
      v_msg.msg_id,
      v_msg.handler_name,
      v_msg.message_type,
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
      NULL,  -- No lease — immediately claimable by WorkCoordinatorPublisherWorker
      NULL,
      COALESCE(v_msg.source_service_id, (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)),
      COALESCE(v_msg.source_commit_sequence, 0)
    )
    ON CONFLICT ON CONSTRAINT wh_inbox_pkey DO NOTHING;

      IF v_msg.stream_id IS NOT NULL
         AND v_msg.stream_id = ANY(v_empty_inbox_streams)
         AND NOT (v_msg.stream_id = ANY(v_notify_inbox_streams)) THEN
        v_notify_inbox_streams := array_append(v_notify_inbox_streams, v_msg.stream_id);
      END IF;

      -- Pinning is ownership/routing, unchanged by 114.
      IF v_msg.stream_id IS NOT NULL AND p_instance_id IS NOT NULL THEN
        INSERT INTO __SCHEMA__.wh_active_streams
          (stream_id, partition_number, assigned_instance_id, last_activity_at)
        VALUES
          (v_msg.stream_id, COALESCE(v_partition, 0), p_instance_id, p_now)
        ON CONFLICT (stream_id) DO NOTHING;
      END IF;
      RETURN QUERY SELECT v_msg.msg_id AS message_id, v_msg.stream_id AS stream_id, TRUE AS was_newly_created;
    END IF;  -- end of the single-observation branch
  END LOOP;

  IF cardinality(v_notify_inbox_streams) > 0 THEN
    PERFORM __SCHEMA__.notify_instance_owners('inbox', v_notify_inbox_streams);
  END IF;
END;
$$ LANGUAGE plpgsql;
