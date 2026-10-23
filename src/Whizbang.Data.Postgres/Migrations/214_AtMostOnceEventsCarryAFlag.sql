-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 214_AtMostOnceEventsCarryAFlag.sql
-- Date: 2026-10-19
-- Description: At-most-once events are left out of digests, epochs, checkpoint counts and re-delivery for real (#1484).
--
--              A schedule declares its delivery guarantee, and _wh_spawn_occurrence wrote it to the occurrence's
--              outbox metadata ('deliveryGuarantee': 1 for at-most-once). Every exclusion that is meant to leave
--              such an occurrence out read it from wh_event_body.metadata instead: the emit-chain digest folds
--              (087, 146, 149, 162), the epoch folds (092), close_stream (087, 093, 212),
--              reclassify_events_ephemeral (087, 093), wh_purge_streams (180) and the coordinator's digest,
--              checkpoint and re-delivery queries. The emit chains build that metadata from the message id and
--              the hops alone, so the key was never there and no exclusion ever matched: at-most-once occurrences
--              were folded into every digest, counted in every checkpoint and selectable for re-delivery. An
--              occurrence is stored for local processing and never published, so a consumer that compared its
--              digests with this service's saw it as missing and asked for it, and re-delivery sent it.
--
--              The guarantee is now bit 5 of the row's flags (EventFlags.AtMostOnce = 32), the column that already
--              carries ephemeral (8). _wh_spawn_occurrence sets it on the outbox row, store_outbox_messages
--              persists it and the emit chain copies it to wh_event_store.flags unchanged, so every exclusion
--              becomes a bit test on a column the query has already read. A flag rather than a metadata key:
--              it costs no bytes on the row (stored metadata is being slimmed, plans/storage-event-slimming.md),
--              and the digest, epoch and checkpoint queries no longer join wh_event_body or detoast a metadata
--              document per row only to evaluate the filter. No index is added or needed: the bit is a residual
--              filter on rows the queries select by stream, sequence or event id, exactly like flags & 8 beside
--              it, and no plan reads wh_event_store by flag.
--
--              Rows stored before this migration carry no flag. _wh_flag_at_most_once_occurrences finds them
--              through wh_schedule_runs (one row per fired occurrence, keyed by its event id), reads the
--              guarantee from the outbox row when it still exists (what was declared at fire time) and from the
--              schedule otherwise, sets the flag on the event-store row and on the outbox row, XORs the flagged
--              events back out of the stream digests the old fold put them in, refolds the sealed epochs that
--              covered them and bumps the origin generation once, as close_stream does for any deliberate change
--              to folded history. It only ever flags a row that does not carry the flag yet, so a re-run of this
--              file (the redefinition closure re-runs it after any of the files it supersedes) finds nothing to
--              do and changes nothing, which is why it needs no wh_settings marker.
--
--              Every function below is its latest definition copied whole (rule 5) with only that change; the
--              files numbered below 148 that they come from wrote shared literals raw, so those are now tokens
--              (rule 12). close_stream was defined at more than one arity, so its overloads are cleared first.
--              wh_seed_stream_digests_batch (087) still names the old key and is not redefined: it runs only
--              inside 087, which redefines it from its own text before every call, against a worklist 087 drops.
-- Dependencies: 068 (_wh_spawn_occurrence), 092 (epoch folds), 093 (epoch refold, origin generation,
--               reclassify_events_ephemeral), 149 (_emit_event_store_chain), 162 (_emit_event_store_chain_for_inbox),
--               180 (wh_purge_streams), 212 (close_stream)
-- Objects: _wh_spawn_occurrence, _emit_event_store_chain, _emit_event_store_chain_for_inbox, close_stream, reclassify_events_ephemeral, _wh_epoch_buckets, compute_type_digests_epoch, compute_type_digests_epoch_window, wh_purge_streams, _wh_flag_at_most_once_occurrences
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- _wh_spawn_occurrence: 068 + the occurrence's flags carry its guarantee.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/temporal/temporal-engine</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AtMostOnceOccurrenceIntegrityTests.cs:AtMostOnceOccurrence_StoredByTheEmitChain_CarriesTheAtMostOnceFlagAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_spawn_occurrence(
  p_schedule_id UUID,
  p_occurrence_id UUID,
  p_occurrence_number BIGINT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_scope JSONB,
  p_stream_id UUID,
  p_delivery_guarantee SMALLINT,
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER,
  p_run_status SMALLINT,
  p_authority_principal_id UUID DEFAULT NULL,
  p_authority_claims JSONB DEFAULT NULL
) RETURNS VOID
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_message JSONB;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  v_message := jsonb_build_array(jsonb_build_object(
    __ENVELOPE_FIELD_MESSAGE_ID__, p_occurrence_id,
    'MessageType', p_event_type,
    -- Envelope carries the event under 'Payload' (what _emit_event_store_chain extracts into
    -- wh_event_store.event_data), plus the message id + empty hop list.
    'Envelope', jsonb_build_object(
      'Payload', COALESCE(p_event_data, '{}'::jsonb),
      'id', p_occurrence_id,
      'hops', '[]'::jsonb),
    -- The occurrence carries its schedule linkage, delivery dial, and the SNAPSHOT run-as authority, so
    -- the pre-fire gate can check/refresh the security context before the job actually runs.
    'Metadata', jsonb_build_object(
      'scheduleId', p_schedule_id,
      'occurrence', p_occurrence_number,
      'deliveryGuarantee', p_delivery_guarantee,
      'authorityPrincipalId', p_authority_principal_id,
      'authorityClaims', p_authority_claims),
    'Scope', COALESCE(p_scope, 'null'::jsonb),
    __ENVELOPE_FIELD_STREAM_ID__, p_stream_id,
    'IsEvent', true,
    -- 214 (#1484): an at-most-once occurrence carries EventFlags.AtMostOnce. store_outbox_messages writes it to
    -- wh_outbox.flags and the emit chain copies it to wh_event_store.flags, where every exclusion reads it.
    __ENVELOPE_FIELD_FLAGS__, CASE WHEN p_delivery_guarantee = 1 THEN c_flag_at_most_once ELSE 0 END
  ));
  PERFORM __SCHEMA__.store_outbox_messages(
    v_message, p_instance_id, p_lease_expiry, p_now, p_partition_count);

  INSERT INTO __SCHEMA__.wh_schedule_runs
    (schedule_id, occurrence_id, fired_at, status, instance_id)
  VALUES (p_schedule_id, p_occurrence_id, p_now, p_run_status, p_instance_id);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_spawn_occurrence IS
  'Emits one schedule occurrence through the normal outbox/event-store chain and logs a wh_schedule_runs '
  'row. Shared by the scheduled fire (status 0) and trigger-now (status 3 TriggeredEarly). 214: an '
  'at-most-once occurrence carries EventFlags.AtMostOnce (32) in its flags.';

-- ---------------------------------------------------------------------------------------------
-- _emit_event_store_chain: 149 + the digest fold leaves out flags & 32.
-- ---------------------------------------------------------------------------------------------
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:EmitChain_AtMostOnceEvent_IsNotFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:EmitChain_OutboxEvent_FoldsIntoLocalBucketAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AtMostOnceOccurrenceIntegrityTests.cs:AtMostOnceOccurrence_StoredByTheEmitChain_IsNotFoldedIntoTheStreamDigestAsync</tests>
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
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;  -- NOSONAR S1192: the field name recurs per function; PL/pgSQL has no file-level constants
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
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
  -- Lock order is hashtext(stream_id::text), sorted ascending, ensures deadlock-free
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
  -- disagree with their UUIDv7 ordering, perspective cursors advance by version, then later
  -- see an "earlier" event_id and trip the cursor-inversion detector + full replay.
  -- Phase H step 10 slice 3: version is computed via a correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, if a future refactor weakens or bypasses the lock, the
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
  -- Migration 072: materialize the extracted payload + built metadata ONCE, so the pointer INSERT
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
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
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
      __EMPTY_UUID__::uuid,  -- NOSONAR S1192: the no-source-service sentinel recurs per function; PL/pgSQL has no file-level constants
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
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
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
  -- the stream, lease to that owner regardless of which instance ran the commit, eliminates
  -- the cross-instance saga race (different instances commit to the same stream, each
  -- selfishly leasing to themselves, drainer races) that produced the residual ~1000 cursor
  -- inversions in a production run. When no live owner is pinned yet (new stream OR stale owner),
  -- fall back to the commit instance so sync paths (UI waiting on a perspective checkpoint)
  -- don't pay claim_orphaned-polling-interval latency on first-event-per-stream.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
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
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
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
  -- association, route it to the single fixed __collective__ sink so the perspective worker
  -- dispatches it via ICollectiveDispatcher exactly once (the dispatcher fans out to every
  -- matching model handler internally). One sink row per collective event, driven by the
  -- flag bit, independent of associations. Same partition / owner-lease / dedupe semantics as
  -- the association branch above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
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
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
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

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain(UUID[], UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32).';

-- ---------------------------------------------------------------------------------------------
-- _emit_event_store_chain_for_inbox: 162 + the digest fold leaves out flags & 32.
-- ---------------------------------------------------------------------------------------------
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:InboxEmitChain_AtMostOnceEvent_IsNotFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:InboxEmitChain_ReceivedEvent_StampsOriginAndFoldsIntoOriginBucketAsync</tests>
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
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- Migration 087: resolve the LOCAL service id once. store_inbox_messages (062) COALESCEs a
  -- missing envelope SourceServiceId to the local id, so a wh_inbox row attributed to SELF (or
  -- zero) is a locally-originated event (loopback), its origin_service_id must stay NULL,
  -- matching the 046 contract ("NULL for locally-originated events").
  SELECT service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config LIMIT 1;

  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Reproduced in production on a consumer's
  -- service during job creation. Lock order is hashtext(stream_id::text), sorted ASC,
  -- ensures deadlock-free nesting between any pair of transactions touching overlapping stream
  -- sets. pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    -- 162: every column this pass reads is on wh_inbox_state, so the lock pass never touches the
    -- wide message row at all. chain_emitted_at moved here precisely so this predicate stays on one
    -- table and one partial index can cover it; see idx_inbox_state_chain_pending.
    SELECT DISTINCT ist.stream_id AS sid
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      -- 158: only rows this chain has never read (idx_inbox_state_chain_pending); every held row was
      -- read against the event store on every poll before, twice.
      AND ist.chain_emitted_at IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
    ORDER BY ist.stream_id
  ) AS streams_to_lock;

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   - are events (is_event = true)
  --   - have a stream_id
  --   - aren't yet in wh_event_store (idempotent, ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- Phase H step 10 slice 1: ORDER BY i.message_id (UUIDv7 = chronological at the source) so
  -- version assignment matches canonical event_id order. See _emit_event_store_chain above
  -- for the rationale, same fix applies to the inbox backfill path.
  -- Migration 061: i.flags carried into wh_event_store.flags (was dropped here previously).
  WITH inbox_events AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.flags,
      i.source_service_id,
      i.source_commit_sequence,
      ist.received_at,
      ROW_NUMBER() OVER (PARTITION BY ist.stream_id ORDER BY i.message_id) AS row_num
    -- 162: this pass needs the message body, so both tables. The LEASE table drives the join, so
    -- selection happens on the narrow table and the wide row is fetched only for the rows that
    -- survive it, which is the opposite of scanning the wide row to apply the same predicate.
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL  -- 158: see the lock pass above
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, see _emit_event_store_chain above for the rationale.
  -- Migration 072: materialize the extracted payload + built metadata ONCE (see outbox fn above).
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
      -- Migration 087: normalize the received origin, self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = __EMPTY_UUID__::uuid
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
      -- populated by the emit chain before this, consumer-side origin-keyed verification needs them).
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
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
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
      COALESCE(c.origin_service_id, __EMPTY_UUID__::uuid),
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
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
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

  -- 158: stamp the held rows the chain is finished with, so the next poll reads none of them.
  -- "Finished" is the row's event being in the event store: either this pass inserted it, or it was
  -- already there and the pass above excluded the row for exactly that reason. A row whose insert hit
  -- ON CONFLICT DO NOTHING has no event-store row, so it stays unstamped and the next poll re-attempts
  -- it with a fresh version snapshot, which is the contract that conflict clause states. The stamp runs
  -- before the nothing-was-stored return below, because that return is the steady state of a busy
  -- instance (every held event already chained) and the case the stamp exists for. The stamp and the
  -- inserts share this transaction, so a failure leaves both undone.
  -- 162: the stamp is the write this migration helps most. It used to rewrite a row carrying the
  -- whole message body across twenty-four indexes to set one timestamp, measured at about 41 blocks
  -- per row stamped with only 30 of those spent finding the rows. It now writes the narrow table,
  -- and the predicate that finds them is local to it.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET chain_emitted_at = p_now
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );

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
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
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
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
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
  -- event id slots BELOW its cursor's last-applied event is a straggler by construction, a
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
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
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
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
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

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32).';

-- ---------------------------------------------------------------------------------------------
-- close_stream: 212 + the digest subtraction and the epoch range leave out flags & 32.
-- ---------------------------------------------------------------------------------------------
-- Exactly one overload per framework function: this name is defined at more than one
-- arity across the migration set, and CREATE OR REPLACE at a different arity ADDS an
-- overload beside the old one rather than replacing it. The duplicate then makes every
-- unqualified reference ambiguous (42725) -- including this file's own COMMENT ON
-- FUNCTION -- which fails the whole startup pass and strands every later migration.
SELECT __SCHEMA__.drop_all_overloads('close_stream');

-- <docs>fundamentals/events/event-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:CloseStream_TruncatingAnAtMostOnceEvent_SubtractsOnlyWhatWasFoldedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:CloseStream_SubtractsTruncatedRows_SurvivorDigestRemainsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs:CloseStream_ConsumptionGate_BlocksThenTruncatesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.close_stream(
  p_stream_id UUID, p_through_version BIGINT, p_archive BOOLEAN DEFAULT FALSE)
RETURNS TABLE(close_status TEXT, events_truncated BIGINT) AS $$
DECLARE
  v_debug BOOLEAN;
  v_blocked BIGINT;
  v_carry BIGINT;
  v_held BIGINT;
  v_boundary UUID;
  v_behind BIGINT;
  v_deleted BIGINT;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- debug_mode retains forensic history; a close is a truncation, so skip it entirely (like the reaper).
  SELECT (setting_value = 'true') INTO v_debug FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode';
  IF COALESCE(v_debug, FALSE) THEN
    RETURN QUERY SELECT 'debug_skipped'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (1) Consumption gate: any perspective work item for an event in this stream at/below the close point that
  -- is still UNPROCESSED blocks the close.
  SELECT count(*) INTO v_blocked
  FROM __SCHEMA__.wh_perspective_events pe
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = pe.event_id
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND pe.processed_at IS NULL;

  IF v_blocked > 0 THEN
    RETURN QUERY SELECT 'blocked'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- (2) Carry-forward guard: refuse to discard a stream's entire history; there must be a surviving event
  -- above the close point (the domain's closing event / new origin).
  SELECT count(*) INTO v_carry
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version > p_through_version;

  IF v_carry = 0 THEN
    RETURN QUERY SELECT 'no_carry_forward'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Migration 212 (#1486), (3) Hold guard: an active destruction hold on an event in the range is a recorded
  -- PreDestruction decision to keep that event (Cancel, or Defer until a time not yet reached).
  SELECT count(*) INTO v_held
  FROM __SCHEMA__.wh_event_destruction_hold h
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
  WHERE es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND h.hold_until > NOW();

  IF v_held > 0 THEN
    RETURN QUERY SELECT 'held'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Migration 212 (#1486), (4) Cursor guard: a cursor inside the range moves onto the boundary event (the
  -- first survivor above the close point). The cursor is the read position (the next apply reads events with
  -- an id above it), so moving it onto a boundary event its perspective still owes would skip that event.
  -- Refuse until the perspective has consumed it.
  SELECT es.event_id INTO v_boundary
  FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version > p_through_version
  ORDER BY es.version
  LIMIT 1;

  SELECT count(*) INTO v_behind
  FROM __SCHEMA__.wh_perspective_cursors c
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = c.last_event_id
  JOIN __SCHEMA__.wh_perspective_events pe
    ON pe.stream_id = c.stream_id
   AND pe.perspective_name = c.perspective_name
   AND pe.event_id = v_boundary
  WHERE c.stream_id = p_stream_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version
    AND pe.processed_at IS NULL;

  IF v_behind > 0 THEN
    RETURN QUERY SELECT 'cursor_behind'::TEXT, 0::BIGINT;
    RETURN;
  END IF;

  -- Archive BEFORE truncate. Same transaction as the DELETEs below => atomic: a failure here rolls back the
  -- whole close, so the detail is never lost (stays hot) nor left half-archived. Body columns come from
  -- wh_event_body (post-split every body lives there); LEFT JOIN tolerates an already-reaped body defensively.
  IF p_archive THEN
    INSERT INTO __SCHEMA__.wh_event_archive
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, version, created_at)
    SELECT es.event_id, es.stream_id, es.aggregate_id, es.aggregate_type, es.event_type,
           eb.event_data, eb.metadata, es.scope, es.version, es.created_at
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
    WHERE es.stream_id = p_stream_id AND es.version <= p_through_version
    ON CONFLICT (event_id) DO NOTHING;
  END IF;

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows this
  -- close is about to truncate; the sealed epochs covering those ranges refold AFTER the delete
  -- (they must fold the post-close world), and the ranges are unknowable once the rows are gone.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = p_stream_id
      AND es.version <= p_through_version
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Migration 087 (A1c): subtract the about-to-be-truncated rows from wh_stream_digests BEFORE
  -- the delete (the rows must still exist to be read). Only rows that ever
  -- contributed to a digest (non-ephemeral, not at-most-once) are XOR-ed back out; XOR is
  -- self-inverse, so folding the same hashes again removes them. A missing bucket is a no-op;
  -- the periodic full-sweep verification heals any residual drift.
  UPDATE __SCHEMA__.wh_stream_digests d
  SET digest_lo = d.digest_lo # s.digest_lo,
      digest_hi = d.digest_hi # s.digest_hi,
      event_count = d.event_count - s.event_count,
      updated_at = NOW()
  FROM (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS origin_service_id,
           COALESCE(es.scope ->> 't', '') AS scope_tenant,
           es.event_type,
           es.stream_id,
           bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
           bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
           COUNT(*)::int AS event_count
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = p_stream_id
      AND es.version <= p_through_version
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
    GROUP BY 1, 2, 3, 4
  ) s
  WHERE d.origin_service_id = s.origin_service_id
    AND d.scope_tenant = s.scope_tenant
    AND d.event_type = s.event_type
    AND d.stream_id = s.stream_id;

  -- Drop buckets the subtraction emptied (a fully-truncated (type, stream) bucket). Separate
  -- statement so the UPDATE above is visible.
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.stream_id = p_stream_id AND d.event_count <= 0;

  -- Migration 212 (#1486): snapshots a rewind could restore and then not replay. Kept: a snapshot at or
  -- above the close point, and, for a perspective whose cursor is inside the range, one at or after that
  -- cursor's event (the perspective applied nothing after it). Removed: every other versioned snapshot below
  -- that floor, and a versionless snapshot on a truncated event. Runs before the re-anchor below, which moves
  -- the cursor this floor is read from.
  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn
  WHERE sn.stream_id = p_stream_id
    AND (
      (sn.snapshot_version IS NULL
       AND EXISTS (
         SELECT 1 FROM __SCHEMA__.wh_event_store es
         WHERE es.event_id = sn.snapshot_event_id
           AND es.stream_id = p_stream_id
           AND es.version <= p_through_version))
      OR sn.snapshot_version < COALESCE(
        (SELECT es.version
         FROM __SCHEMA__.wh_perspective_cursors c
         JOIN __SCHEMA__.wh_event_store es ON es.event_id = c.last_event_id
         WHERE c.stream_id = p_stream_id
           AND c.perspective_name = sn.perspective_name
           AND es.stream_id = p_stream_id
           AND es.version <= p_through_version),
        p_through_version));

  -- Migration 212 (#1486): re-anchor the cursors inside the range onto the boundary event. The cursor guard
  -- above proved none of their perspectives still owes it. fk_perspective_cursors_event is ON DELETE
  -- RESTRICT on the EF Core schema, so this has to happen before the events go.
  UPDATE __SCHEMA__.wh_perspective_cursors c
  SET last_event_id = v_boundary
  FROM __SCHEMA__.wh_event_store es
  WHERE c.stream_id = p_stream_id
    AND es.event_id = c.last_event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  -- Migration 212 (#1486): finished work rows and lapsed destruction holds of the truncated events. The
  -- gates above proved no unprocessed work row and no active hold remains in the range.
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  USING __SCHEMA__.wh_event_store es
  WHERE pe.event_id = es.event_id
    AND pe.stream_id = p_stream_id
    AND pe.processed_at IS NOT NULL
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_destruction_hold h
  USING __SCHEMA__.wh_event_store es
  WHERE h.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  -- Truncate the detail at/below the close point: bodies first, then pointers. Cursors were re-anchored
  -- above, so fk_perspective_cursors_event (EF Core schema) no longer references anything removed here.
  DELETE FROM __SCHEMA__.wh_event_body eb
  USING __SCHEMA__.wh_event_store es
  WHERE eb.event_id = es.event_id
    AND es.stream_id = p_stream_id
    AND es.version <= p_through_version;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE es.stream_id = p_stream_id AND es.version <= p_through_version;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;

  -- Migration 093 (#80-F): refold the sealed epochs the truncation touched (post-delete world),
  -- then bump the generation ONCE; consumers reset their seals and re-verify instead of
  -- alarming on deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT 'closed'::TEXT, v_deleted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.close_stream(UUID, BIGINT, BOOLEAN) IS
'Gated prefix truncate of one stream through a version. Refuses with debug_skipped, blocked (unprocessed work in the range), no_carry_forward (no event above the close point), held (an active destruction hold in the range) or cursor_behind (a cursor in the range whose perspective still owes the boundary event). On closed it re-anchors cursors in the range onto the boundary event, removes snapshots a rewind could no longer replay from, removes processed work rows and lapsed holds of the truncated events, and keeps their deduplication entries (#1486). 214: at-most-once events (flags & 32), never folded, are never subtracted.';

-- ---------------------------------------------------------------------------------------------
-- reclassify_events_ephemeral: 093 + the digest subtraction and the epoch range leave out flags & 32.
-- ---------------------------------------------------------------------------------------------
-- <docs>fundamentals/events/ephemeral-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs:Reclassify_SubtractsReclassifiedRows_EmptiedBucketDroppedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reclassify_events_ephemeral(p_event_types TEXT[])
RETURNS TABLE(
  events_reclassified BIGINT,
  streams_reclassified BIGINT,
  streams_blocked BIGINT
) AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_names TEXT[];
  v_events BIGINT := 0;
  v_streams BIGINT := 0;
  v_blocked BIGINT := 0;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
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

  -- Migration 093 (#80-F): capture, per lane, the sequence range of the fold-relevant rows the
  -- stamp below will remove from the fold, same predicates as the UPDATE, evaluated BEFORE it.
  SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
  INTO v_lanes, v_mins, v_maxs
  FROM (
    SELECT COALESCE(es.origin_service_id, c_zero) AS lane,
           MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
           MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_type = ANY(v_names)
      AND (es.flags & c_flag_ephemeral) = 0
      AND (es.flags & c_flag_at_most_once) = 0  -- 214 (#1484)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store b
        WHERE b.stream_id = es.stream_id
          AND NOT (b.event_type = ANY(v_names))
          AND (b.flags & c_flag_ephemeral) = 0
      )
    GROUP BY 1
  ) t
  WHERE t.mn IS NOT NULL;

  -- Full split (078): bodies live in wh_event_body from birth for BOTH classes, so reclassification is
  -- purely a flags stamp, there is no inline body to offload or null out anymore.
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
    RETURNING es.event_id, es.stream_id, es.event_type, es.scope, es.origin_service_id, es.flags
  ),
  -- Migration 087 (A1c): the reclassified rows leave the audited digest set (flags & 8 excluded),
  -- so XOR them back out of wh_stream_digests. Reads the pre-statement bucket snapshot (data-
  -- modifying CTEs can't see each other's table effects), safe: buckets pre-exist and only this
  -- statement touches them. A missing bucket is a no-op; the full-sweep verification heals drift.
  digest_subtract AS (
    UPDATE __SCHEMA__.wh_stream_digests d
    SET digest_lo = d.digest_lo # s.digest_lo,
        digest_hi = d.digest_hi # s.digest_hi,
        event_count = d.event_count - s.event_count,
        updated_at = NOW()
    FROM (
      SELECT COALESCE(r.origin_service_id, __EMPTY_UUID__::uuid) AS origin_service_id,
             COALESCE(r.scope ->> 't', '') AS scope_tenant,
             r.event_type,
             r.stream_id,
             bit_xor(hashtextextended(r.event_id::text, 0)) AS digest_lo,
             bit_xor(hashtextextended(r.event_id::text, 1)) AS digest_hi,
             COUNT(*)::int AS event_count
      FROM reclassified r
      WHERE (r.flags & c_flag_at_most_once) = 0  -- 214 (#1484): never folded, so never subtracted
      GROUP BY 1, 2, 3, 4
    ) s
    WHERE d.origin_service_id = s.origin_service_id
      AND d.scope_tenant = s.scope_tenant
      AND d.event_type = s.event_type
      AND d.stream_id = s.stream_id
  )
  SELECT COUNT(*), COUNT(DISTINCT stream_id) INTO v_events, v_streams FROM reclassified;

  -- Migration 087: drop buckets the subtraction emptied (separate statement so it sees the
  -- digest_subtract UPDATE; scoped to the reclassified type names).
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.event_type = ANY(v_names) AND d.event_count <= 0;

  -- Migration 093 (#80-F): refold the sealed epochs the reclassification touched (the stamped
  -- rows now fall out of the fold), then bump the generation ONCE.
  IF v_lanes IS NOT NULL AND v_events > 0 THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY SELECT v_events, v_streams, v_blocked;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reclassify_events_ephemeral(TEXT[]) IS
  'Stamps EventFlags.Ephemeral on the historical rows of the named types in homogeneous streams, subtracts them from the stream digests, refolds the sealed epochs they were in and bumps the origin generation (093). 214: at-most-once events (flags & 32), never folded, are never subtracted.';

-- ---------------------------------------------------------------------------------------------
-- The epoch folds: 092 + flags & 32 instead of the body-metadata key, with no join to wh_event_body.
-- ---------------------------------------------------------------------------------------------
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AtMostOnceOccurrenceIntegrityTests.cs:AtMostOnceOccurrence_StoredByTheEmitChain_IsLeftOutOfTheEpochFoldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DigestEpochSqlTests.cs:CloseDigestEpochs_EphemeralAndAtMostOnce_ExcludedFromFoldAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_epoch_buckets(
  p_lane  UUID,
  p_epoch BIGINT,
  p_width BIGINT
) RETURNS TABLE (b_tenant TEXT, b_type TEXT, b_lo BIGINT, b_hi BIGINT, b_cnt INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_from  BIGINT := p_epoch * p_width;
  v_to    BIGINT := (p_epoch + 1) * p_width;   -- exclusive
BEGIN
  IF p_lane = c_zero THEN
    RETURN QUERY
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type::text,
           bit_xor(hashtextextended(es.event_id::text, 0)),
           bit_xor(hashtextextended(es.event_id::text, 1)),
           COUNT(*)::int
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id IS NULL
      AND es.commit_sequence >= v_from AND es.commit_sequence < v_to
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
    GROUP BY 1, 2;
  ELSE
    RETURN QUERY
    SELECT COALESCE(es.scope ->> 't', ''), es.event_type::text,
           bit_xor(hashtextextended(es.event_id::text, 0)),
           bit_xor(hashtextextended(es.event_id::text, 1)),
           COUNT(*)::int
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = p_lane
      AND es.origin_commit_sequence >= v_from AND es.origin_commit_sequence < v_to
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
    GROUP BY 1, 2;
  END IF;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_epoch_buckets IS
'Internal: the canonical epoch fold, the single source of truth for what an epoch should hold. Predicates mirror the emit-chain fold exactly: ephemeral (flags & 8) and at-most-once (flags & 32, 214) events are left out.';

-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AtMostOnceOccurrenceIntegrityTests.cs:AtMostOnceOccurrence_StoredByTheEmitChain_IsLeftOutOfTheTypeAndWindowedDigestsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch(
  p_origin_lane    UUID,          -- NULL = the local lane (this service's own emissions)
  p_event_types    TEXT[],        -- NULL = all types
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_lane      UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier  BIGINT := -1;
  v_width     BIGINT;
  v_open_from BIGINT := 0;
  v_types     TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_frontier := -1;      -- nothing sealed: the whole lane folds live below
    v_open_from := 0;
  ELSE
    v_open_from := (v_frontier + 1) * v_width;
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id <= v_frontier
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  open_fold AS (
    -- The live remainder. A row the stamper has not yet sequenced belongs here by definition , 
    -- it can be in no epoch, so the NULL-sequence branch keeps it from falling through the crack.
    SELECT COALESCE(es.scope ->> 't', '') AS b_tenant, es.event_type AS b_type,
           bit_xor(hashtextextended(es.event_id::text, 0)) AS lo,
           bit_xor(hashtextextended(es.event_id::text, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
      AND (CASE WHEN p_origin_lane IS NULL
                THEN es.commit_sequence IS NULL OR es.commit_sequence >= v_open_from
                ELSE es.origin_commit_sequence IS NULL OR es.origin_commit_sequence >= v_open_from
           END)
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM open_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_type_digests_epoch IS
'Type-level digest answer served from sealed epochs + a live fold of only the open window. Sealed rows are authoritative (the self-sweep owns detecting a bad seal). Falls back to the plain full fold when the lane has no closed epochs. 214: the live fold leaves out at-most-once events (flags & 32).';

-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AtMostOnceOccurrenceIntegrityTests.cs:AtMostOnceOccurrence_StoredByTheEmitChain_IsLeftOutOfTheTypeAndWindowedDigestsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch_window(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_event_types    TEXT[],        -- NULL = all types
  p_since          BIGINT,        -- inclusive
  p_until          BIGINT,        -- exclusive, pre-clamped at the settled max
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_lane     UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier BIGINT := -1;
  v_width    BIGINT;
  v_e_lo     BIGINT;
  v_e_hi     BIGINT;
  v_types    TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_e_lo := 0;
    v_e_hi := -1;   -- no sealed epochs: the whole window folds live
  ELSE
    -- Sealed epochs fully inside [p_since, p_until): first with start >= since, last whose
    -- exclusive end fits, both clamped to the frontier. An empty run (e_lo > e_hi) = all live.
    v_e_lo := (p_since + v_width - 1) / v_width;
    v_e_hi := LEAST(p_until / v_width - 1, v_frontier);
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id BETWEEN v_e_lo AND v_e_hi
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  lane AS (
    SELECT es.*,
           CASE WHEN p_origin_lane IS NULL THEN es.commit_sequence
                ELSE es.origin_commit_sequence END AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
  ),
  live_fold AS (
    -- The window minus the sealed run: the low fringe [since, e_lo*width) and the tail
    -- [(e_hi+1)*width, until), or the whole window when no epoch fits (e_lo > e_hi).
    SELECT COALESCE(l.scope ->> 't', '') AS b_tenant, l.event_type AS b_type,
           bit_xor(hashtextextended(l.event_id::text, 0)) AS lo,
           bit_xor(hashtextextended(l.event_id::text, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM lane l
    WHERE l.lane_seq IS NOT NULL
      AND l.lane_seq >= p_since AND l.lane_seq < p_until
      AND (v_e_lo > v_e_hi
           OR l.lane_seq < v_e_lo * v_width
           OR l.lane_seq >= (v_e_hi + 1) * v_width)
      AND (v_types IS NULL OR l.event_type = ANY(v_types))
      AND COALESCE(l.flags, 0) & 8 = 0
      AND COALESCE(l.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      AND l.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM live_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_type_digests_epoch_window IS
'Windowed type-level fold [p_since, p_until): fully-contained sealed epochs answer from their seal, fringes fold live. Caller pre-clamps p_until at integrity_settled_max. 214: the live fold leaves out at-most-once events (flags & 32).';

-- ---------------------------------------------------------------------------------------------
-- wh_purge_streams: 180 + the epoch range leaves out flags & 32.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/infrastructure/purging-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:Purge_RemovesEveryRowOfTheStreams_AndLeavesOthersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:DryRun_CountsWhatWouldGo_AndChangesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_purge_streams(
  p_stream_ids UUID[],
  p_dry_run BOOLEAN,
  p_purge_id UUID,
  p_batch_index INTEGER,
  p_requested_by TEXT,
  p_reason TEXT
) RETURNS TABLE(purged_table TEXT, purged_rows BIGINT) AS $$
DECLARE
  v_ids UUID[];
  v_event_ids UUID[];
  v_message_ids UUID[];
  v_schema TEXT;
  v_table TEXT;
  v_n BIGINT;
  v_counts JSONB := '{}'::jsonb;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- The empty stream id is "no stream": purging it would reach every streamless row.
  IF __EMPTY_UUID__::uuid = ANY(p_stream_ids) THEN
    RAISE EXCEPTION 'wh_purge_streams: the empty stream id cannot be purged';
  END IF;

  SELECT COALESCE(array_agg(DISTINCT s), ARRAY[]::uuid[]) INTO v_ids FROM unnest(p_stream_ids) s;

  -- Ids the indirect tables are keyed by, read before anything is deleted.
  SELECT COALESCE(array_agg(es.event_id), ARRAY[]::uuid[]) INTO v_event_ids
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  SELECT v_event_ids || COALESCE(array_agg(i.message_id), ARRAY[]::uuid[]) INTO v_message_ids
  FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);

  -- Work tables, in the canonical lock order.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_outbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Counted before the inbox delete that removes them by cascade.
  SELECT count(*) INTO v_n
  FROM __SCHEMA__.wh_inbox_state st
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = st.message_id
  WHERE i.stream_id = ANY(v_ids);
  purged_table := 'wh_inbox_state'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_inbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_events'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_active_streams'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Deduplication entries of the stream's messages: its events and its inbox messages.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_message_deduplication'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_receptor_processing'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Before the events: fk_perspective_cursors_event is ON DELETE RESTRICT.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_snapshots'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_applied'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Every perspective's row for the streams. The registry names the tables; one it names that does not
  -- exist yet is skipped. Going through regclass resolves this function's own schema whichever way the
  -- runner substituted it (see 105).
  SELECT n.nspname INTO v_schema
  FROM pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
  WHERE c.oid = '__SCHEMA__.wh_event_store'::regclass;

  FOR v_table IN
    SELECT DISTINCT r.table_name FROM __SCHEMA__.wh_perspective_registry r ORDER BY r.table_name
  LOOP
    CONTINUE WHEN to_regclass(format('%I.%I', v_schema, v_table)) IS NULL;
    IF p_dry_run THEN
      EXECUTE format('SELECT count(*) FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) INTO v_n USING v_ids;
    ELSE
      EXECUTE format('DELETE FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) USING v_ids;
      GET DIAGNOSTICS v_n = ROW_COUNT;
    END IF;
    purged_table := v_table; purged_rows := v_n; RETURN NEXT;
    v_counts := v_counts || jsonb_build_object(purged_table, v_n);
  END LOOP;

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_row_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_row_eviction_journal'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- The sealed epochs covering the fold-relevant events refold AFTER the delete, and their ranges are
  -- unknowable once the rows are gone (the same capture close_stream makes).
  IF NOT p_dry_run THEN
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = ANY(v_ids)
        AND COALESCE(es.flags, 0) & 8 = 0
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;
  END IF;

  -- Every event of the streams goes, so every digest bucket and ledger row of the streams goes with them.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_digests'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_integrity_ledger'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_body'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_destruction_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_lifecycle_completions'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_apply_fold_watermarks'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_store'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Refold what the delete touched, then bump the generation ONCE so consumers reset their seals and
  -- re-verify instead of alarming on a deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  -- A purged stream stays purged: mark it for every perspective, so a later event is skipped, not applied.
  v_n := COALESCE(array_length(v_ids, 1), 0);
  IF NOT p_dry_run THEN
    INSERT INTO __SCHEMA__.wh_stream_purge_markers (stream_id, perspective_name, purged_at, purge_event_id, purge_id)
    SELECT s, '*', NOW(), NULL, p_purge_id FROM unnest(v_ids) s
    ON CONFLICT (stream_id, perspective_name) DO UPDATE
      SET purged_at = EXCLUDED.purged_at, purge_event_id = NULL, purge_id = EXCLUDED.purge_id;

    INSERT INTO __SCHEMA__.wh_stream_purge_audit (purge_id, batch_index, requested_by, reason, stream_ids, row_counts)
    VALUES (p_purge_id, p_batch_index, p_requested_by, p_reason, v_ids, v_counts || jsonb_build_object('wh_stream_purge_markers', v_n))
    ON CONFLICT (purge_id, batch_index) DO NOTHING;
  END IF;
  purged_table := 'wh_stream_purge_markers'; purged_rows := v_n; RETURN NEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_purge_streams IS
'Operator stream purge (#1030): removes every row of the listed streams from this service''s store in the caller''s transaction, marks them purged for every perspective, and audits the batch. p_dry_run counts the same rows and changes nothing. Returns one row per table. 214: the refolded epoch range leaves out at-most-once events (flags & 32), which no epoch holds.';

-- ---------------------------------------------------------------------------------------------
-- _wh_flag_at_most_once_occurrences: the rows stored before the flag existed.
-- ---------------------------------------------------------------------------------------------
-- <docs>resilience/stream-integrity</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AtMostOnceOccurrenceIntegrityTests.cs:FlagAtMostOnceOccurrences_FlagsRowsStoredWithoutTheFlag_AndTakesThemOutOfDigestsAndEpochsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_flag_at_most_once_occurrences()
RETURNS INTEGER AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5)
  c_at_most_once CONSTANT INTEGER := 1;        -- ScheduleDeliveryGuarantee.AtMostOnce
  v_flagged INTEGER := 0;
  v_streams UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
BEGIN
  -- An outbox row the emit chain could not store yet (a version conflict leaves it for the next claim) is
  -- re-emitted from wh_outbox.flags, so it carries the flag too. Reached by primary key from the runs, never
  -- by scanning the outbox for a metadata key.
  UPDATE __SCHEMA__.wh_outbox o
  SET flags = COALESCE(o.flags, 0) | c_flag_at_most_once
  FROM (SELECT DISTINCT r.occurrence_id FROM __SCHEMA__.wh_schedule_runs r) r
  WHERE o.message_id = r.occurrence_id
    AND COALESCE(o.flags, 0) & c_flag_at_most_once = 0
    AND (o.metadata ->> 'deliveryGuarantee')::integer = c_at_most_once;

  -- The event-store rows. The outbox row records the guarantee declared at fire time and outranks the
  -- schedule, whose guarantee may have changed since; the schedule answers once the outbox row is gone.
  WITH targets AS (
    SELECT DISTINCT es.event_id
    FROM (SELECT DISTINCT r.occurrence_id, r.schedule_id FROM __SCHEMA__.wh_schedule_runs r) r
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = r.occurrence_id
    LEFT JOIN __SCHEMA__.wh_outbox o ON o.message_id = r.occurrence_id
    LEFT JOIN __SCHEMA__.wh_schedules s ON s.schedule_id = r.schedule_id
    WHERE COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND COALESCE((o.metadata ->> 'deliveryGuarantee')::integer, s.delivery_guarantee::integer) = c_at_most_once
  ),
  flagged AS (
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = COALESCE(es.flags, 0) | c_flag_at_most_once
    FROM targets t
    WHERE es.event_id = t.event_id
    RETURNING es.event_id, es.stream_id, es.event_type, es.scope, es.flags, es.origin_service_id,
              CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS lane_seq
  ),
  -- Only a row the old fold could have taken in (not ephemeral) is in a bucket; XOR is self-inverse, so folding
  -- its hashes again takes it out. Reads the flagged rows from RETURNING (data-modifying CTEs do not see each
  -- other's table effects). A missing bucket is a no-op; the verification sweep heals anything left over.
  digest_subtract AS (
    UPDATE __SCHEMA__.wh_stream_digests d
    SET digest_lo = d.digest_lo # s.digest_lo,
        digest_hi = d.digest_hi # s.digest_hi,
        event_count = d.event_count - s.event_count,
        updated_at = NOW()
    FROM (
      SELECT COALESCE(f.origin_service_id, __EMPTY_UUID__::uuid) AS origin_service_id,
             COALESCE(f.scope ->> 't', '') AS scope_tenant,
             f.event_type,
             f.stream_id,
             bit_xor(hashtextextended(f.event_id::text, 0)) AS digest_lo,
             bit_xor(hashtextextended(f.event_id::text, 1)) AS digest_hi,
             COUNT(*)::int AS event_count
      FROM flagged f
      WHERE f.flags & c_flag_ephemeral = 0
      GROUP BY 1, 2, 3, 4
    ) s
    WHERE d.origin_service_id = s.origin_service_id
      AND d.scope_tenant = s.scope_tenant
      AND d.event_type = s.event_type
      AND d.stream_id = s.stream_id
  ),
  lanes AS (
    SELECT COALESCE(f.origin_service_id, __EMPTY_UUID__::uuid) AS lane, MIN(f.lane_seq) AS mn, MAX(f.lane_seq) AS mx
    FROM flagged f
    WHERE f.flags & c_flag_ephemeral = 0
    GROUP BY 1
  )
  SELECT (SELECT COUNT(*)::int FROM flagged),
         (SELECT array_agg(DISTINCT f.stream_id) FROM flagged f),
         (SELECT array_agg(l.lane) FROM lanes l WHERE l.mn IS NOT NULL),
         (SELECT array_agg(l.mn) FROM lanes l WHERE l.mn IS NOT NULL),
         (SELECT array_agg(l.mx) FROM lanes l WHERE l.mn IS NOT NULL)
  INTO v_flagged, v_streams, v_lanes, v_mins, v_maxs;

  IF v_flagged = 0 THEN
    RETURN 0;
  END IF;

  -- Drop the buckets the subtraction emptied (an occurrence stream holds nothing else). A separate statement so
  -- it sees the subtraction.
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.stream_id = ANY(v_streams) AND d.event_count <= 0;

  -- The sealed epochs that counted the flagged rows refold without them (_wh_epoch_buckets above reads the
  -- flag), and the generation moves once so consumers reset their seals instead of alarming on the change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN v_flagged;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_flag_at_most_once_occurrences() IS
  'Internal (214, #1484): flags the at-most-once schedule occurrences stored before EventFlags.AtMostOnce existed, takes them out of the stream digests and sealed epochs, and bumps the origin generation once. Returns the event-store rows flagged; a re-run flags none and changes nothing.';

SELECT __SCHEMA__._wh_flag_at_most_once_occurrences();
