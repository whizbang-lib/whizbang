-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 466_SupersedingTypesCountTheKey.sql (#2364): every function goes back to its last word before 466 (336
-- for the fold helper, 453 for the emit chains, the windowed answers and the seed, 454 for the reaper, 465 for the cut
-- helpers, the heal statement, the epoch fold and the type answer), and the slot helpers, columns and index go. Digests
-- counted by slot stay so until the heal rewrites them per copy.

SELECT __SCHEMA__.drop_all_overloads('_wh_fold_stream_digests');
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldsEveryLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldingIntoARowWithoutIdLanes_KeepsThemNullAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:TruncateStreamPrefix_SubtractsEveryLaneOfTheTruncatedEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:SeedStreamDigestsBatch_FoldsEveryLaneOfTheSeededStreamsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestFoldHelperSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fold_stream_digests(
  p_origins UUID[],
  p_tenants TEXT[],
  p_types TEXT[],
  p_streams UUID[],
  p_event_ids UUID[],
  p_mode TEXT,
  p_now TIMESTAMPTZ
) RETURNS INTEGER AS $$
DECLARE
  v_rows INTEGER := 0;
BEGIN
  IF p_mode IS NULL OR p_mode NOT IN ('fold', 'unfold', 'seed') THEN
    RAISE EXCEPTION '_wh_fold_stream_digests: unknown mode %', p_mode;
  END IF;
  IF COALESCE(cardinality(p_event_ids), 0) = 0 THEN
    RETURN 0;
  END IF;
  IF cardinality(p_origins) IS DISTINCT FROM cardinality(p_event_ids)
     OR cardinality(p_tenants) IS DISTINCT FROM cardinality(p_event_ids)
     OR cardinality(p_types) IS DISTINCT FROM cardinality(p_event_ids)
     OR cardinality(p_streams) IS DISTINCT FROM cardinality(p_event_ids) THEN
    RAISE EXCEPTION '_wh_fold_stream_digests: % event ids but % origins, % tenants, % types and % streams must pair by position',
      cardinality(p_event_ids), cardinality(p_origins), cardinality(p_tenants), cardinality(p_types), cardinality(p_streams);
  END IF;

  IF p_mode = 'unfold' THEN
    UPDATE __SCHEMA__.wh_stream_digests d
    SET digest_lo = d.digest_lo # s.lo,
        digest_hi = d.digest_hi # s.hi,
        id_lo = d.id_lo # s.id_lo,
        id_hi = d.id_hi # s.id_hi,
        event_count = d.event_count - s.n,
        updated_at = p_now
    FROM (
      SELECT m.origin_service_id, m.scope_tenant, m.event_type, m.stream_id,
             bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 2)) AS id_lo,
             bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 3)) AS id_hi,
             COUNT(*)::int AS n
      FROM unnest(p_origins, p_tenants, p_types, p_streams, p_event_ids)
        AS m(origin_service_id, scope_tenant, event_type, stream_id, event_id)
      GROUP BY 1, 2, 3, 4
    ) s
    WHERE d.origin_service_id = s.origin_service_id
      AND d.scope_tenant = s.scope_tenant
      AND d.event_type = s.event_type
      AND d.stream_id = s.stream_id
      -- An index condition on idx_stream_digests_stream (301) the generic plan can always use: the join keys alone
      -- leave it free to scan the table when it misjudges the size of the set.
      AND d.stream_id = ANY(p_streams);
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
  END IF;

  INSERT INTO __SCHEMA__.wh_stream_digests AS d
    (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at, id_lo, id_hi)
  SELECT m.origin_service_id, m.scope_tenant, m.event_type, m.stream_id,
         bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 0)),
         bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 1)),
         COUNT(*)::int,
         p_now,
         bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 2)),
         bit_xor(__SCHEMA__._wh_digest_lane(m.event_id, 3))
  FROM unnest(p_origins, p_tenants, p_types, p_streams, p_event_ids)
    AS m(origin_service_id, scope_tenant, event_type, stream_id, event_id)
  GROUP BY 1, 2, 3, 4
  ORDER BY 1, 2, 3, 4
  ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
    digest_lo = d.digest_lo # EXCLUDED.digest_lo,
    digest_hi = d.digest_hi # EXCLUDED.digest_hi,
    id_lo = d.id_lo # EXCLUDED.id_lo,
    id_hi = d.id_hi # EXCLUDED.id_hi,
    event_count = d.event_count + EXCLUDED.event_count,
    updated_at = EXCLUDED.updated_at
  WHERE p_mode = 'fold';
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_stream_digests(UUID[], TEXT[], TEXT[], UUID[], UUID[], TEXT, TIMESTAMPTZ) IS
  '336 (#2106): the one stream digest fold. Folds (''fold''), takes out (''unfold'') or writes where absent (''seed'') '
  'a set of events, every lane together (hash lanes, id lanes, count), buckets in key order; a NULL id lane stays NULL. '
  'Every function that writes wh_stream_digests calls it.';


-- ── _emit_event_store_chain: last word 391, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmitEventStoreChainCollectiveSqlTests.cs:EmitEventStoreChain_CollectiveEvent_CreatesSingleCollectiveSinkRowAsync</tests>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:OutboxAppend_CreatesTheRow_FromTheStreamsFirstEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Append_WritesOneLedgerRowPerStreamPerBatch_NotOnePerEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:AppendWithinTheHour_IsAHeapOnlyUpdate_WhileAnIndexedColumnChangeIsNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Bucket_AdvancesOnlyWhenTheHourChanges_AndAnEarlierClockNeverMovesItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:ConcurrentBatchesOnDifferentStreams_NeitherWaitsForTheOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheOutbox_SealsTheStream_InTheTransactionThatStoresItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxAppend_ToASealedStream_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:OutboxBatch_EventsAfterATerminalEvent_AreDeadLettered_AndTheOnesBeforeItAreStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalFlag_OnAnEphemeralEvent_SealsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/StreamSealingEmitCostScenarioTests.cs:EmitChain_WithAndWithoutTheSealCheck_ReportAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Outbox_StampsTheKeyOnlyOnASupersedingEvent_AndQueuesTheOlderOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_TwoEventsOfOneKeyInOneBatch_QueueTheOlderAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_ANewerEventOfAnotherTypeInTheGroup_QueuesTheOlder_InEitherOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_Refuse_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesOneRequestPerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/ArchivedStreamEmitCostScenarioTests.cs:EmitChain_With266sProbeAndWith271s_ReportAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:OutboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:FullListedType_KeepsTheFullRow_ByTypeAndByNamespaceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DebugMode_KeepsEveryRowFullAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:WriteFormFull_KeepsFullRows_AndStagesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:WriteFormSlim_StoresSlimRows_AndStagesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/StoredMetadataEmitCostScenarioTests.cs:EmitChain_FullAgainstLinked_ReportAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_StoresTheEventAtTheNextVersion_AndDeadLettersNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_AParkedCursorWithoutASnapshotAtIt_IsDeadLetteredAwaitingRestoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:StoreOutboxMessages_AnAwaitedWrite_IsStoredAtTheNextVersionAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldsEveryLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:OutboxEmitChain_FoldingIntoARowWithoutIdLanes_KeepsThemNullAsync</tests>
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
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_superseding_ids UUID[];                -- 295: the stored events that carry a superseding key
  v_streams UUID[];                        -- 266: the batch's streams, in lock order
  v_ends_a_stream BOOLEAN;                 -- 266: the batch carries a terminal event
  v_any_sealed BOOLEAN;                    -- 271: a stream of the batch is sealed
  v_any_archived BOOLEAN;                  -- 271: a stream of the batch is archived (and not sealed)
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
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
  -- Lock order is the stream id, sorted ascending, which keeps the nesting deadlock-free between
  -- any pair of transactions touching overlapping stream sets.
  -- pg_advisory_xact_lock auto-releases at commit/rollback.
  -- 266 (#1733): the read that finds the batch's streams also notes whether the batch ends one, so the sealing check
  -- below costs no second read of the batch.
  SELECT array_agg(b.sid ORDER BY b.sid), COALESCE(bool_or(b.ends_stream), false)
    INTO v_streams, v_ends_a_stream
  FROM (
    SELECT o.stream_id AS sid,
           bool_or(COALESCE(o.flags, 0) & c_flag_terminal <> 0
                   AND COALESCE(o.flags, 0) & c_flags_off_the_ledger = 0) AS ends_stream
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
    GROUP BY o.stream_id
  ) AS b;

  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || streams_to_lock.sid::text))
  FROM (
    SELECT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY u.sid
  ) AS streams_to_lock;

  -- 266 (#1733): a sealed stream refuses every append. The refused event alone is dead-lettered, with the refusal as
  -- its error text, and the copy below stores the rest of the batch; a raise here would fail every stream in the batch
  -- for one sealed stream. Under the locks taken above, so a seal cannot land between this check and the copy.
  -- 271 (#1738): so does an archived stream, by its type's AppendToArchived policy, after the seal (a sealed stream's
  -- refusal is the seal's). One probe per stream of idx_stream_lifecycle_closed, which holds only the sealed and the
  -- archived streams, answers both, so the common case (none of either, no terminal event) costs what 266's probe cost.
  SELECT COALESCE(bool_or(l.sealed_at IS NOT NULL), false),
         COALESCE(bool_or(l.sealed_at IS NULL AND l.state = 'Archived'), false)
    INTO v_any_sealed, v_any_archived
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = ANY(v_streams)
    AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
  IF v_ends_a_stream OR v_any_sealed THEN
    PERFORM __SCHEMA__._wh_refuse_sealed_stream_outbox_events(p_outbox_message_ids, p_instance_id, p_now);
  END IF;
  IF v_any_archived THEN
    PERFORM __SCHEMA__._wh_refuse_archived_stream_outbox_events(p_outbox_message_ids, p_instance_id, p_now);
    -- 296 (#1734): the archived streams the refusal left alone start their new generation.
    PERFORM __SCHEMA__._wh_start_new_generations(v_streams);
  END IF;

  -- 285 (#1725): the stored-metadata settings, read once for the whole batch.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;

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
  computed_full AS (
    SELECT
      oe.message_id,
      oe.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(oe.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(oe.message_type) AS event_type,
      COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload') AS body_data,
      COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb) AS hops,
      jsonb_build_object(
        c_field_message_id, COALESCE(oe.event_data::jsonb -> 'id', oe.event_data::jsonb -> c_field_message_id, oe.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN oe.metadata IS NOT NULL
             AND jsonb_typeof(oe.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((oe.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS full_meta,
      oe.scope,
      oe.row_num,
      oe.flags,
      -- 295 (#1729): a superseding event's key, from the metadata the dispatch or receive path stamped it in. Only
      -- a row carrying the flag gets one, so a stray 'sk' on another event is never read.
      CASE WHEN COALESCE(oe.flags, 0) & c_flag_superseding <> 0 THEN oe.metadata::jsonb ->> 'sk' END AS supersede_key,
      -- 352 (#2124): and its superseding group, beside the key, on a flagged row only.
      CASE WHEN COALESCE(oe.flags, 0) & c_flag_superseding <> 0 THEN oe.metadata::jsonb ->> 'sg' END AS supersede_group
    FROM outbox_events oe
  ),
  -- 285 (#1725): each event's route id, and the metadata its row keeps: the full form (plus the route id) under debug
  -- mode, under the Full form or for a listed type, otherwise the slim form. Pure CPU on values already in hand.
  -- Materialized, so each event's route id is computed once however many times the next step reads it.
  computed_routes AS MATERIALIZED (
    SELECT cf.*, __SCHEMA__._wh_route_id(split_part(cf.event_type, ',', 1), cf.hops) AS route_id
    FROM computed_full cf
  ),
  computed AS (
    SELECT cr.*,
      CASE
        WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(cr.event_type, ',', 1), v_full_for)
        THEN cr.full_meta || jsonb_build_object('rid', cr.route_id)
        ELSE __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id)
      END AS body_meta
    FROM computed_routes cr
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, supersede_key, supersede_group
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      -- 296 (#1734): a stream with nothing hot in a batch that held an archived stream may be a new generation, which
      -- continues from its ledger row's last version; any other batch reads nothing more.
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id),
               CASE WHEN v_any_archived THEN
                 (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = c.stream_id)
               END,
               0) + c.row_num,
      p_now,
      c.flags,
      c.supersede_key,
      c.supersede_group
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version, supersede_key
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
  -- 285 (#1725): under the Linked form, the full form is appended to the staging table for maintenance to distill into
  -- routes and traces. Append-only: the key is new for every stored event, so there is nothing to check.
  staged_metadata AS (
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    SELECT c.message_id, p_now, c.route_id, split_part(c.event_type, ',', 1), c.full_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE v_write_form = 'Linked'
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
    -- 336 (#2106): every lane, through the one fold helper. Referenced by the statement's final SELECT, so it
    -- always runs (a CTE that writes nothing runs only when read).
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(__EMPTY_UUID__::uuid), array_agg(COALESCE(c.scope::jsonb ->> 't', '')), array_agg(c.event_type),
      array_agg(c.stream_id), array_agg(c.message_id), 'fold', p_now) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE __DIGEST_ELIGIBLE__(c)
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  -- 266 (#1733): a terminal event among them seals the stream in this statement, so the event and the seal commit
  -- together; every other append writes state, sealed_at and sealed_reason back unchanged, which keeps it heap-only.
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0)),
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN p_now END,
      (array_agg(__SCHEMA__._wh_terminal_seal_reason(c.aggregate_type, c.message_id) ORDER BY se.version)
         FILTER (WHERE COALESCE(c.flags, 0) & c_flag_terminal <> 0))[1]
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at)
  )
  SELECT array_agg(event_id), array_agg(event_id) FILTER (WHERE supersede_key IS NOT NULL)
    INTO v_stored_event_ids, v_superseding_ids
  FROM stored_events CROSS JOIN digest_folds;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 295 (#1729): a stored superseding event queues the older events of its stream, type and key for the reaper. A
  -- separate statement, so it sees every event this batch stored as well as the ones before it. Skipped outright when
  -- the batch stored none, so an ordinary batch pays nothing; otherwise one probe of idx_event_store_superseding per
  -- superseding event.
  IF v_superseding_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(v_superseding_ids, p_now);
  END IF;

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
    -- 391 (#2145): only a type a handler on this service applies, once every service registered here gates.
    AND __SCHEMA__._wh_collective_row_wanted(es.event_type)
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
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145).';

-- ── _emit_event_store_chain_for_inbox: last word 411, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- _emit_event_store_chain_for_inbox: copied from its last word (391), with the held-back refund.
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Inbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Inbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync</tests>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:InboxAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamOrderSqlTests.cs:TwoClaimsOfOneStream_TheHeadsClaimCommitsLast_VersionsFollowTheProducerOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:InboxStoredAuditRecord_LeavesTheOriginsDigestLaneUntouched_WhileADomainEventFoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheInbox_SealsTheStream_AndLaterRowsAreRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:InboxAppend_ToASealedStream_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#across-services</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_Inbox_StampsTheKey_AndQueuesTheOlderOneAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_Inbox_StampsTheGroup_AndQueuesAcrossTypesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_Refuse_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesTheRequestAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:InboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:InboxAppend_StoresTheReceivedEventAtTheNextVersionAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:InboxEmitChain_FoldsEveryLaneIntoTheOriginsBucketAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#the-claim-runs-only-what-can-find-work</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_ARowWhoseEventIsAlreadyStored_IsStamped_WithoutRunningTheCopyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_AfterAPassWithNoCandidate_AReadyRowIsStoredByTheNextClaimAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_AnOrdinaryEvent_RunsNoCollectiveRoutingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimFixedCostSqlTests.cs:StorePass_ACollectiveEvent_IsStillRoutedToTheCollectiveSinkAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#a-held-back-stream-is-claimed-whole</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackStreamsClaimSqlTests.cs:Claim_ARowHeldBackBehindAPredecessorAPeerHolds_IsNotChargedAnAttemptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldBackStreamsClaimSqlTests.cs:Claim_AHeldBackRowAcquiredByAnEarlierClaim_IsNotRefundedAgainAsync</tests>
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
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  -- 228 (#1546): the rows this pass would copy, for the refusal before the copy.
  v_candidate_ids UUID[];
  v_candidate_streams UUID[];
  v_candidate_flags INTEGER[];
  v_candidate_types TEXT[];
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_superseding_ids UUID[];                -- 295: the stored events that carry a superseding key
  v_sealed_refused UUID[];                 -- 266: the rows a sealed stream refused
  v_any_sealed BOOLEAN;                    -- 271: a candidate's stream is sealed
  v_any_archived BOOLEAN;                  -- 271: a candidate's stream is archived (and not sealed)
  v_archived_refused UUID[];               -- 271: the rows an archived stream refused
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  v_any_collective BOOLEAN;                -- 367: an event this pass stored carries the collective flag
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

  -- 367: no candidate, nothing to copy. The lock pass selects by the same predicate as every later read of this pass
  -- (the refusals' candidates, the copy's inbox_events), so when it locks nothing they find nothing, and only the stamp
  -- below has work: rows whose event is already stored. Each statement skipped is one not planned, and this function
  -- inherits claim_work's force_custom_plan. A row a concurrent claim commits after this read is not lost: it is still
  -- unstamped, so the next claim's guard runs this pass for it, as it would have after any pass that missed it.
  IF NOT FOUND THEN
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
    RETURN 0;
  END IF;

  -- 228 (#1546): a received event that would make its stream hold both ephemeral and Sourced events is refused
  -- before the copy. Nobody awaits a claim, so every refusal here is dead-lettered: recorded in wh_dead_letters and
  -- its lease row completed in place, so the copy below no longer sees it and the claim never offers it again. The
  -- message row is not touched here: this runs inside claim_work after the stream ledger, and wh_inbox comes first
  -- in the canonical lock order. The rows are the ones the copy reads, by the same predicate, under the locks taken
  -- above.
  SELECT array_agg(ist.message_id), array_agg(ist.stream_id), array_agg(i.flags), array_agg(i.message_type)
    INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
  FROM __SCHEMA__.wh_inbox_state ist
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );
  -- 266 (#1733): a sealed stream refuses every append, so its rows are dead-lettered and completed in place first, as
  -- the mixed-stream refusal does, and leave the candidates.
  -- 271 (#1738): so does an archived stream, by its type's AppendToArchived policy, after the seal. One probe per stream
  -- of idx_stream_lifecycle_closed, which holds only the sealed and the archived streams, answers both, so the common
  -- case (none of either, no terminal event) costs what 266's probe cost.
  IF v_candidate_ids IS NOT NULL THEN
    SELECT COALESCE(bool_or(l.sealed_at IS NOT NULL), false),
           COALESCE(bool_or(l.sealed_at IS NULL AND l.state = 'Archived'), false)
      INTO v_any_sealed, v_any_archived
    FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.stream_id = ANY(v_candidate_streams)
      AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
    IF v_any_sealed OR EXISTS (
         SELECT 1 FROM unnest(v_candidate_flags) AS f(bits)
         WHERE COALESCE(f.bits, 0) & c_flag_terminal <> 0
           AND COALESCE(f.bits, 0) & c_flags_off_the_ledger = 0) THEN
      v_sealed_refused := __SCHEMA__._wh_refuse_sealed_stream_inbox_events(
        v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
      IF cardinality(v_sealed_refused) > 0 THEN
        SELECT array_agg(k.id ORDER BY k.ord), array_agg(k.sid ORDER BY k.ord), array_agg(k.bits ORDER BY k.ord),
               array_agg(k.type_name ORDER BY k.ord)
          INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
        FROM unnest(v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types)
               WITH ORDINALITY AS k(id, sid, bits, type_name, ord)
        WHERE k.id <> ALL(v_sealed_refused);
      END IF;
    END IF;
    IF v_any_archived AND v_candidate_ids IS NOT NULL THEN
      v_archived_refused := __SCHEMA__._wh_refuse_archived_stream_inbox_events(
        v_candidate_ids, v_candidate_streams, p_instance_id, p_now);
      IF cardinality(v_archived_refused) > 0 THEN
        SELECT array_agg(k.id ORDER BY k.ord), array_agg(k.sid ORDER BY k.ord), array_agg(k.bits ORDER BY k.ord),
               array_agg(k.type_name ORDER BY k.ord)
          INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
        FROM unnest(v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types)
               WITH ORDINALITY AS k(id, sid, bits, type_name, ord)
        WHERE k.id <> ALL(v_archived_refused);
      END IF;
      -- 296 (#1734): the archived streams the refusal left alone start their new generation.
      IF v_candidate_ids IS NOT NULL THEN
        PERFORM __SCHEMA__._wh_start_new_generations(v_candidate_streams);
      END IF;
    END IF;
  END IF;
  IF v_candidate_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refuse_mixed_stream_inbox_events(
      v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
  END IF;

  -- 285 (#1725): the stored-metadata settings, read once for the whole batch.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;

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
      ist.received_at
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
  -- 230 (#1607): a received stream is stored in the producer's order, however many claims carry it. A claim
  -- numbers the rows it holds after the stream's tail, so a claim that holds a stream's later events and reaches
  -- this pass first would number them ahead of the head another claim leased earlier and has not stored yet.
  -- The head is visible here as a pending event of the stream that this pass does not hold: not yet in the event
  -- store, and not leased to this instance under a live lease (a peer's lease, an expired one, none, or a lease a
  -- concurrent claim has not committed). Every row of this batch that sorts after the stream's first such event
  -- waits: it is not stored and not stamped, so the next claim re-reads it, and fetch_inbox_batch does not hand
  -- it to the dispatcher before it is stored. Rows before it are stored now, so the head of a stream never waits.
  -- The order is the message id, the order this pass has always numbered by (slice 1): a fan-out child's id is
  -- derived from its composite and ordinal, so it sorts in producer order whether or not it carries an origin
  -- commit sequence.
  --
  -- One probe per stream, over idx_inbox_state_stream_order (stream_id, received_at, message_id), bounded by the
  -- batch: only rows received no later than the batch's newest row and sorting before its last one can hold it
  -- back. In a stream's FIFO backlog that range is the batch and the rows before it, never the rows queued behind
  -- it, and the lease test is answered from the index, so only a row this pass does not hold reads the heap. No
  -- index is added: wh_inbox_state's index count is a gated design property (InboxStateWriteCostScenarioTests).
  -- An event received after the rows it precedes is not waited for; that is the transport reordering two
  -- deliveries, which the receiver cannot see coming.
  batch_streams AS (
    SELECT ie.stream_id,
           MAX(ie.received_at) AS newest_received_at,
           (array_agg(ie.message_id ORDER BY ie.message_id DESC))[1] AS last_message_id
    FROM inbox_events ie
    GROUP BY ie.stream_id
  ),
  held_back_from AS (
    SELECT s.stream_id,
           (SELECT b.message_id
            FROM __SCHEMA__.wh_inbox_state b
            WHERE b.stream_id = s.stream_id
              AND b.processed_at IS NULL
              AND b.received_at <= s.newest_received_at
              AND b.message_id < s.last_message_id
              AND NOT COALESCE(b.instance_id = p_instance_id AND b.lease_expiry > p_now, false)
              AND b.is_event = true
              AND b.chain_emitted_at IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = b.message_id
              )
            ORDER BY b.message_id
            LIMIT 1) AS first_unheld
    FROM batch_streams s
  ),
  storable_events AS (
    SELECT ie.*,
           ROW_NUMBER() OVER (PARTITION BY ie.stream_id ORDER BY ie.message_id) AS row_num
    FROM inbox_events ie
    JOIN held_back_from h ON h.stream_id = ie.stream_id
    WHERE h.first_unheld IS NULL OR ie.message_id < h.first_unheld
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, see _emit_event_store_chain above for the rationale.
  -- Migration 072: materialize the extracted payload + built metadata ONCE (see outbox fn above).
  computed_full AS (
    SELECT
      ie.message_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(ie.message_type) AS event_type,
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload') AS body_data,
      COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb) AS hops,
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN ie.metadata IS NOT NULL
             AND jsonb_typeof(ie.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((ie.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS full_meta,
      ie.scope,
      ie.row_num,
      ie.flags,
      -- 295 (#1729): a superseding event's key, from the metadata the dispatch or receive path stamped it in. Only
      -- a row carrying the flag gets one, so a stray 'sk' on another event is never read.
      CASE WHEN COALESCE(ie.flags, 0) & c_flag_superseding <> 0 THEN ie.metadata::jsonb ->> 'sk' END AS supersede_key,
      -- 352 (#2124): and the superseding group the receive path stamped beside the key, on a flagged row only.
      CASE WHEN COALESCE(ie.flags, 0) & c_flag_superseding <> 0 THEN ie.metadata::jsonb ->> 'sg' END AS supersede_group,
      -- Migration 087: normalize the received origin, self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = __EMPTY_UUID__::uuid
             OR ie.source_service_id = v_local_service_id
        THEN NULL
        ELSE ie.source_service_id
      END AS origin_service_id,
      NULLIF(ie.source_commit_sequence, 0) AS origin_commit_sequence
    FROM storable_events ie
  ),
  -- 285 (#1725): each event's route id, and the metadata its row keeps: the full form (plus the route id) under debug
  -- mode, under the Full form or for a listed type, otherwise the slim form. Pure CPU on values already in hand.
  -- Materialized, so each event's route id is computed once however many times the next step reads it.
  computed_routes AS MATERIALIZED (
    SELECT cf.*, __SCHEMA__._wh_route_id(split_part(cf.event_type, ',', 1), cf.hops) AS route_id
    FROM computed_full cf
  ),
  computed AS (
    SELECT cr.*,
      CASE
        WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(cr.event_type, ',', 1), v_full_for)
        THEN cr.full_meta || jsonb_build_object('rid', cr.route_id)
        ELSE __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id)
      END AS body_meta
    FROM computed_routes cr
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, origin_service_id, origin_commit_sequence, supersede_key, supersede_group
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      -- 296 (#1734): see _emit_event_store_chain.
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id),
               CASE WHEN v_any_archived THEN
                 (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = c.stream_id)
               END,
               0) + c.row_num,
      p_now,
      c.flags,
      -- Migration 087: stamp the origin identity the transport delivered (046 columns were never
      -- populated by the emit chain before this, consumer-side origin-keyed verification needs them).
      c.origin_service_id,
      CASE WHEN c.origin_service_id IS NULL THEN NULL ELSE c.origin_commit_sequence END,
      c.supersede_key,
      c.supersede_group
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version, supersede_key, flags
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
  -- 285 (#1725): under the Linked form, the full form is appended to the staging table for maintenance to distill into
  -- routes and traces. Append-only: the key is new for every stored event, so there is nothing to check.
  staged_metadata AS (
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    SELECT c.message_id, p_now, c.route_id, split_part(c.event_type, ',', 1), c.full_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE v_write_form = 'Linked'
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
    -- 336 (#2106): every lane, through the one fold helper. Referenced by the statement's final SELECT, so it
    -- always runs (a CTE that writes nothing runs only when read).
    SELECT __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(c.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(c.scope::jsonb ->> 't', '')), array_agg(c.event_type),
      array_agg(c.stream_id), array_agg(c.message_id), 'fold', p_now) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE __DIGEST_ELIGIBLE__(c)
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  -- 266 (#1733): a terminal event among them seals the stream in this statement (see _emit_event_store_chain).
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0)),
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN bool_or(COALESCE(c.flags, 0) & c_flag_terminal <> 0) THEN p_now END,
      (array_agg(__SCHEMA__._wh_terminal_seal_reason(c.aggregate_type, c.message_id) ORDER BY se.version)
         FILTER (WHERE COALESCE(c.flags, 0) & c_flag_terminal <> 0))[1]
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at)
  )
  SELECT array_agg(event_id), array_agg(event_id) FILTER (WHERE supersede_key IS NOT NULL),
         -- 367: whether the collective routing below has anything to route, from the rows this statement stored.
         COALESCE(bool_or((COALESCE(flags, 0) & c_flag_collective) = c_flag_collective), false)
    INTO v_stored_event_ids, v_superseding_ids, v_any_collective
  FROM stored_events CROSS JOIN digest_folds;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 295 (#1729): a stored superseding event queues the older events of its stream, type and key for the reaper. A
  -- separate statement, so it sees every event this batch stored as well as the ones before it. Skipped outright when
  -- the batch stored none, so an ordinary batch pays nothing; otherwise one probe of idx_event_store_superseding per
  -- superseding event.
  IF v_superseding_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(v_superseding_ids, p_now);
  END IF;

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

  -- 411 (#2120): a row this claim leased (its lease is the one this claim set) that the pass above held back keeps the
  -- attempt the claim counted for it. The claim charges an attempt to every row it acquires (claim_orphaned_inbox), but
  -- a held-back row is not stored, so the re-offer never hands it to the drain (349) and the attempt bought nothing:
  -- waiting behind an earlier event its stream needs first is not failing. Only rows this claim acquired are refunded; a
  -- row held back since an earlier claim was refunded by that claim's pass.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET attempts = ist.attempts - 1
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry = p_lease_expiry
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND ist.attempts > 0
    AND NOT EXISTS (
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

  -- 367: only when an event this pass stored is collective; v_any_collective is read from the stored rows themselves.
  IF v_any_collective THEN
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
      -- 391 (#2145): only a type a handler on this service applies, once every service registered here gates.
      AND __SCHEMA__._wh_collective_row_wanted(es.event_type)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
        WHERE pe_check.stream_id = es.stream_id
          AND pe_check.perspective_name = c_collective_sink
          AND pe_check.event_id = es.event_id
      )
    ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;
  END IF;

  -- Slice 26.4: wake the commit-order stamper. See _emit_event_store_chain above for
  -- the rationale and dedup semantics. Inbox backfill is generally a smaller fan-in
  -- than outbox-emit, but the NOTIFY is just as cheap and keeps the stamper hot path
  -- responsive whether events arrive locally or via transport.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  '411 (#2120): a row the calling claim leased (lease_expiry = p_lease_expiry) that this pass held back is refunded the attempt the claim counted. Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each received event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each received event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 367: a pass whose lock pass finds no candidate only stamps, and the collective routing runs only when a stored event is collective. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145).';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests): wh_append_event has had more than
-- one arity, so every definition site clears its overloads first.
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');

-- ── wh_reap_superseded_events: last word 453, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_TheStreamsFirstEvent_RecordsItsBusinessTimeOnTheLedgerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:Reap_ALaterEvent_RecordsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RebuildCreatedAtIntegrationTests.cs:RebuildBlueGreen_AfterTheFirstEventIsReaped_DatesTheRowAtTheStreamsStartAsync</tests>
-- <docs>fundamentals/events/superseding-events#the-reaper</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_SupersededAndConsumed_IsDeletedWithItsBodyAndItsDigestContribution_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_UnprocessedPerspectiveWork_KeepsIt_UntilItIsProcessedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ACursorOnIt_KeepsIt_UntilTheCursorMovesOnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ASnapshotOnIt_KeepsIt_UntilTheSnapshotIsGoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnActiveDestructionHold_KeepsIt_ALapsedOneDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AnEventTheStamperHasNotReached_IsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_ADifferentTypeOrKeyInTheSameStream_IsUntouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TheNewestOfItsKey_IsKept_EvenWhenQueuedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_DebugMode_KeepsEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_AQueuedEventAlreadyGone_IsForgottenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_RemovingFoldedHistory_RefoldsAndBumpsTheGenerationOnce_AnAtMostOnceEventDoesNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Reap_TakesAtMostTheBatchSize_OldestQueuedFirstAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AnEventSupersededByAnotherTypeOfItsGroup_IsDeleted_TheNewestIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reap_AQueuedEventWhoseOnlyNewerEventIsOutsideItsGroup_IsKeptAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_reap_superseded_events(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task_name TEXT, rows_affected BIGINT, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start TIMESTAMPTZ := clock_timestamp();
  v_debug BOOLEAN;
  v_candidates UUID[];
  v_streams UUID[];
  v_victims UUID[];
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  v_deleted BIGINT := 0;
  c_task CONSTANT TEXT := 'reap_superseded_events';
  c_flag_superseding CONSTANT INTEGER := 128;  -- EventFlags.Superseding (1 << 7)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  c_flags_state_based CONSTANT INTEGER := 24;  -- EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4)
BEGIN
  -- debug_mode keeps every event for forensics, as every other reaper does.
  SELECT COALESCE(
    (SELECT s.setting_value::BOOLEAN FROM __SCHEMA__.wh_settings s WHERE s.setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  -- A queued event something else already removed (a prefix truncate, an archive, a purge) is forgotten.
  DELETE FROM __SCHEMA__.wh_superseded_events q
  WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = q.event_id);

  -- The batch, oldest queued first, and its streams in lock order.
  SELECT array_agg(b.event_id), array_agg(DISTINCT b.stream_id)
  INTO v_candidates, v_streams
  FROM (
    SELECT q.event_id, q.stream_id
    FROM __SCHEMA__.wh_superseded_events q
    ORDER BY q.queued_at, q.event_id
    LIMIT p_batch_size
  ) b;
  IF v_candidates IS NULL THEN
    RETURN QUERY SELECT c_task, 0::BIGINT,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
    RETURN;
  END IF;

  -- The emit chains' per-stream locks, in stream id order (array_agg DISTINCT sorts), so nothing is appended to,
  -- truncated from or archived out of these streams while their events are judged and removed.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || u.sid::text))
  FROM unnest(v_streams) AS u(sid)
  ORDER BY u.sid;

  -- The gates, read under the locks.
  SELECT array_agg(es.event_id) INTO v_victims
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_id = ANY(v_candidates)
    AND es.supersede_key IS NOT NULL
    AND COALESCE(es.flags, 0) & c_flag_superseding <> 0
    AND COALESCE(es.flags, 0) & c_flags_state_based = 0
    -- Never the newest of its key: a newer event of the same stream, group and key is stored. 352 (#2124): the group is
    -- the stored supersede_group, or the event type for an ungrouped row, so an ungrouped type is its own group.
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store n
      WHERE n.stream_id = es.stream_id
        AND COALESCE(n.supersede_group, n.event_type) = COALESCE(es.supersede_group, es.event_type)
        AND n.supersede_key = es.supersede_key
        -- 454 (#2350): newer in the origin's order where both copies carry it, so a restored older copy stored above
        -- the newest is the one reaped, and the newest never is
        AND __SCHEMA__._wh_supersedes(n.origin_service_id, n.origin_commit_sequence, n.version,
                                  es.origin_service_id, es.origin_commit_sequence, es.version))
    -- The commit-sequence stamper has reached it, so nothing that walks the stamped order still owes it a place.
    AND es.commit_sequence IS NOT NULL
    -- Every perspective that consumes it has processed it.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
    -- No cursor rests on it (fk_perspective_cursors_event is RESTRICT on the EF Core schema).
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_cursors c
      WHERE c.stream_id = es.stream_id AND c.last_event_id = es.event_id)
    -- No snapshot rests on it: a rewind restores a snapshot and moves the cursor onto its event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_snapshots s
      WHERE s.stream_id = es.stream_id AND s.snapshot_event_id = es.event_id)
    -- A PreDestruction hook's Cancel or Defer is honored, as by every other path that removes an event.
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
      WHERE h.event_id = es.event_id AND h.hold_until > NOW());

  IF v_victims IS NOT NULL THEN
    -- The sequence range of the fold-relevant victims, per lane, for the epoch refold after the delete (as 263).
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(v_victims)
        AND __DIGEST_ELIGIBLE__(es)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;

    -- Subtract the victims from wh_stream_digests before they go, as the prefix truncate does (263): XOR is
    -- self-inverse, so folding the same hashes again removes them. Only rows a fold ever added are subtracted.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)), array_agg(COALESCE(es.scope ->> 't', '')),
      array_agg(es.event_type), array_agg(es.stream_id), array_agg(es.event_id), 'unfold', NOW())
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(v_victims)
      AND __DIGEST_ELIGIBLE__(es);

    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_streams) AND d.event_count <= 0;

    -- 358 (#2152): a victim that is its stream's first event (version 0 or 1) leaves its business time on the stream's
    -- ledger row, so a rebuild that writes the stream's row afresh still dates it at the stream's start. Written once:
    -- the lowest such victim of each stream, and never over a value already recorded.
    UPDATE __SCHEMA__.wh_stream_lifecycle l
    SET first_event_at = f.business_time
    FROM (
      SELECT DISTINCT ON (es.stream_id) es.stream_id,
             COALESCE(__SCHEMA__._wh_event_business_time(eb.metadata), es.created_at) AS business_time
      FROM __SCHEMA__.wh_event_store es
      LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.event_id = ANY(v_victims) AND es.version <= 1
      ORDER BY es.stream_id, es.version
    ) f
    WHERE l.stream_id = f.stream_id AND l.first_event_at IS NULL;

    -- Lapsed holds of the victims go with them; the gates proved no active one remains. Their processed perspective
    -- work rows are left to Task 3's purge: this task runs after the work tables were visited in their canonical order,
    -- and touching one again here would break that order.
    DELETE FROM __SCHEMA__.wh_event_destruction_hold h WHERE h.event_id = ANY(v_victims);

    -- Bodies first, then pointers (wh_receptor_processing rows cascade on the EF Core schema).
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_victims);
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ANY(v_victims);
    GET DIAGNOSTICS v_deleted = ROW_COUNT;

    DELETE FROM __SCHEMA__.wh_superseded_events q WHERE q.event_id = ANY(v_victims);

    -- Refold the sealed epochs that covered the victims, then bump the generation once, so consumers reset their
    -- seals and re-verify instead of alarming on a deliberate change (093).
    IF v_lanes IS NOT NULL THEN
      FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
        PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
      END LOOP;
      PERFORM __SCHEMA__._wh_bump_origin_generation();
    END IF;
  END IF;

  RETURN QUERY SELECT c_task, v_deleted,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_reap_superseded_events(INTEGER) IS
  'Task 16 of perform_maintenance (295, #1729): deletes up to p_batch_size queued superseded events, with their bodies '
  'and their digest contribution, once a newer event of the same stream, group and key is stored and every consumer has '
  'processed them (no unprocessed perspective work, no cursor or snapshot on them, no active destruction hold, commit '
  'sequence stamped). Skipped under debug_mode. Reduces the digests as wh_truncate_stream_prefix does, refolding the '
  'covering sealed epochs and bumping the origin generation once when it removed folded history. 352: a group is the stored supersede_group, or the event type for an ungrouped row (#2124). 358: records the business time of a victim that is its stream''s first event on the stream''s ledger row (first_event_at, #2152).';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Carries the events of the streams (at or below p_through_version; NULL: whole streams) that the digest counts, which
-- the caller is about to remove: their contribution moves into their buckets' carried part (the bucket's digest stays;
-- a missing bucket is written with the contribution as both) and into the removed row of the epoch each sat in.
-- Returns the buckets it carried into. Call it while the rows are still stored.
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Truncate_CarriesWhatItRemoves_TheDigestStaysAndNothingIsRefoldedOrBumpedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:ArchiveWhole_KeepsTheBucketCarried_AndARestoreTakesItBack_NothingBumpedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Purge_KeepsTheBucketCarried_AndARedeliveryOfThePurgedStreamIsRefusedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_carry_cut(p_stream_ids UUID[], p_through_version BIGINT, p_now TIMESTAMPTZ)
RETURNS INTEGER AS $$
DECLARE
  v_rows INTEGER := 0;
  v_epochs INTEGER := 0;   -- the removed rows written; read so both inserts run before the function returns
BEGIN
  WITH ev AS (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane, COALESCE(es.scope ->> 't', '') AS tenant,
           es.event_type AS ty, es.stream_id AS s, es.event_id,
           CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS pos
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = ANY(p_stream_ids)
      AND (p_through_version IS NULL OR es.version <= p_through_version)
      AND __DIGEST_ELIGIBLE__(es)
  ),
  removed AS (
    INSERT INTO __SCHEMA__.wh_digest_epoch_removed AS r
      (origin_service_id, scope_tenant, event_type, epoch_id, removed_lo, removed_hi, removed_count, updated_at)
    SELECT e.lane, e.tenant, e.ty,
           CASE WHEN e.pos IS NULL THEN -1 ELSE e.pos / __SCHEMA__._wh_epoch_width(e.lane) END,
           bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)), bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)),
           COUNT(*)::int, p_now
    FROM ev e
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, epoch_id) DO UPDATE SET
      removed_lo = r.removed_lo # EXCLUDED.removed_lo,
      removed_hi = r.removed_hi # EXCLUDED.removed_hi,
      removed_count = r.removed_count + EXCLUDED.removed_count,
      updated_at = EXCLUDED.updated_at
    RETURNING 1
  ),
  carried AS (
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at, id_lo, id_hi,
       carried_lo, carried_hi, carried_count, carried_id_lo, carried_id_hi, carried_at)
    SELECT b.lane, b.tenant, b.ty, b.s, b.lo, b.hi, b.n, p_now, b.ilo, b.ihi, b.lo, b.hi, b.n, b.ilo, b.ihi, p_now
    FROM (
      SELECT e.lane, e.tenant, e.ty, e.s,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 2)) AS ilo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 3)) AS ihi,
             COUNT(*)::int AS n
      FROM ev e
      GROUP BY 1, 2, 3, 4
    ) b
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      carried_lo = d.carried_lo # EXCLUDED.carried_lo,
      carried_hi = d.carried_hi # EXCLUDED.carried_hi,
      carried_count = d.carried_count + EXCLUDED.carried_count,
      carried_id_lo = d.carried_id_lo # EXCLUDED.carried_id_lo,
      carried_id_hi = d.carried_id_hi # EXCLUDED.carried_id_hi,
      carried_at = EXCLUDED.carried_at
    RETURNING 1
  )
  SELECT (SELECT COUNT(*) FROM carried)::int, (SELECT COUNT(*) FROM removed)::int INTO v_rows, v_epochs;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_carry_cut(UUID[], BIGINT, TIMESTAMPTZ) IS
  '465 (#2363): moves the digest contribution of the counted events a cut is about to remove into their buckets'' carried part and their epochs'' removed rows. The bucket''s digest is unchanged. Returns the buckets carried into.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Takes restored events back out of what was carried for them: out of their buckets' carried part and their epochs'
-- removed rows. Only when every bucket they belong to carries at least as many events as come back; otherwise (a stream
-- archived before 465, which carried nothing) it changes nothing and returns false, and the caller folds them in.
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:ArchiveWhole_KeepsTheBucketCarried_AndARestoreTakesItBack_NothingBumpedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Restore_OfAStreamArchivedBeforeCutsCarried_FoldsItBackAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_uncarry_restored(p_event_ids UUID[], p_now TIMESTAMPTZ)
RETURNS BOOLEAN AS $$
DECLARE
  v_short BOOLEAN;
  v_buckets INTEGER;   -- the buckets and removed rows updated; read so both updates run
  v_epochs INTEGER;
BEGIN
  IF COALESCE(cardinality(p_event_ids), 0) = 0 THEN
    RETURN TRUE;
  END IF;

  SELECT EXISTS (
    SELECT 1
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane, COALESCE(es.scope ->> 't', '') AS tenant,
             es.event_type AS ty, es.stream_id AS s, COUNT(*)::int AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id = ANY(p_event_ids) AND __DIGEST_ELIGIBLE__(es)
      GROUP BY 1, 2, 3, 4
    ) b
    LEFT JOIN __SCHEMA__.wh_stream_digests d
      ON d.origin_service_id = b.lane AND d.scope_tenant = b.tenant AND d.event_type = b.ty AND d.stream_id = b.s
    WHERE d.carried_count IS NULL OR d.carried_count < b.n) INTO v_short;
  IF v_short THEN
    RETURN FALSE;
  END IF;

  WITH ev AS (
    SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane, COALESCE(es.scope ->> 't', '') AS tenant,
           es.event_type AS ty, es.stream_id AS s, es.event_id,
           CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END AS pos
    FROM __SCHEMA__.wh_event_store es
    WHERE es.event_id = ANY(p_event_ids) AND __DIGEST_ELIGIBLE__(es)
  ),
  buckets AS (
    UPDATE __SCHEMA__.wh_stream_digests d
    SET carried_lo = d.carried_lo # b.lo, carried_hi = d.carried_hi # b.hi, carried_count = d.carried_count - b.n,
        carried_id_lo = d.carried_id_lo # b.ilo, carried_id_hi = d.carried_id_hi # b.ihi
    FROM (
      SELECT e.lane, e.tenant, e.ty, e.s,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 2)) AS ilo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 3)) AS ihi,
             COUNT(*)::int AS n
      FROM ev e GROUP BY 1, 2, 3, 4
    ) b
    WHERE d.origin_service_id = b.lane AND d.scope_tenant = b.tenant AND d.event_type = b.ty AND d.stream_id = b.s
    RETURNING 1
  ),
  epochs AS (
    UPDATE __SCHEMA__.wh_digest_epoch_removed r
    SET removed_lo = r.removed_lo # x.lo, removed_hi = r.removed_hi # x.hi, removed_count = r.removed_count - x.n,
        updated_at = p_now
    FROM (
      SELECT e.lane, e.tenant, e.ty,
             CASE WHEN e.pos IS NULL THEN -1 ELSE e.pos / __SCHEMA__._wh_epoch_width(e.lane) END AS epoch,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(e.event_id, 1)) AS hi,
             COUNT(*)::int AS n
      FROM ev e GROUP BY 1, 2, 3, 4
    ) x
    WHERE r.origin_service_id = x.lane AND r.scope_tenant = x.tenant AND r.event_type = x.ty AND r.epoch_id = x.epoch
    RETURNING 1
  )
  SELECT (SELECT COUNT(*) FROM buckets)::int, (SELECT COUNT(*) FROM epochs)::int INTO v_buckets, v_epochs;

  DELETE FROM __SCHEMA__.wh_digest_epoch_removed r
  WHERE r.removed_count <= 0 AND r.removed_lo = 0 AND r.removed_hi = 0;
  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_uncarry_restored(UUID[], TIMESTAMPTZ) IS
  '465 (#2363): takes restored events out of their buckets'' carried part and their epochs'' removed rows; false (and nothing changed) when a bucket carries fewer than come back, so the caller folds them in as before 465.';


-- ── _wh_heal_stream_digests_statement: last word 453, copied whole, comparing with the live fold plus the carried part (465) ──
-- The heal statement for a named batch (p_named) or the whole store, with $1 the streams, $2 the settle window and
-- $3 whether to fill id lanes never computed. One text, planned and run as written.
-- <docs>resilience/stream-integrity#the-heal-is-linear-in-its-batch</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestHealPlanSqlTests.cs:TheHealStatement_NeverRescansASetPerRow_ForANamedBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestHealPlanSqlTests.cs:TheHealStatement_NeverRescansASetPerRow_ForTheWholeStoreAsync</tests>
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Heal_FindsACarriedBucketTrue_AndStillFindsAnUnaccountedLossAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Heal_ABucketWhoseWholeStreamWasCut_IsKept_NotReportedAsAPhantomAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_heal_stream_digests_statement(p_named BOOLEAN)
RETURNS TEXT AS $$
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- The stream filter, as an index condition when streams are named; the whole store for the sweep.
  v_events TEXT := CASE WHEN p_named THEN 'es.stream_id = ANY($1)' ELSE 'TRUE' END;
  v_digests TEXT := CASE WHEN p_named THEN 'd.stream_id = ANY($1)' ELSE 'TRUE' END;
BEGIN
  RETURN format($q$
    WITH recomputed AS (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS o, COALESCE(es.scope ->> 't', '') AS t,
             es.event_type AS ty, es.stream_id AS s,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)) AS lo,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)) AS hi,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 2)) AS ilo,
             bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 3)) AS ihi,
             COUNT(*)::int AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE %1$s
        AND __DIGEST_ELIGIBLE__(es)
        AND es.created_at < NOW() - $2
      GROUP BY 1, 2, 3, 4
    ),
    stored AS (
      SELECT d.origin_service_id AS o, d.scope_tenant AS t, d.event_type AS ty, d.stream_id AS s,
             d.digest_lo, d.digest_hi, d.event_count, d.id_lo, d.id_hi,
             d.carried_lo, d.carried_hi, d.carried_count, d.carried_id_lo, d.carried_id_hi,
             (d.carried_count <> 0 OR d.carried_lo <> 0 OR d.carried_hi <> 0) AS carries
      FROM __SCHEMA__.wh_stream_digests d
      -- 465 (#2363): a bucket whose carried detail rolled off cannot be recomputed from its events: left alone.
      WHERE %2$s AND d.updated_at < NOW() - $2 AND d.carried_lo IS NOT NULL
    ),
    -- One comparison of the two sets, as a FULL JOIN: PostgreSQL runs a full join only as a hash or merge join, never
    -- as a nested loop, so its cost stays linear whatever the planner believes about either set (#2337). A bucket on
    -- both sides is compared; one only stored has no events behind it; one only recomputed has no bucket yet.
    -- 465 (#2363): what the stored digest should be is the live fold plus the carried part, so a bucket a cut carried
    -- is compared with both, and one whose whole stream was cut (no live events, a carried part) is no phantom.
    expected AS (
      SELECT st.o, st.t, st.ty, st.s, st.digest_lo, st.digest_hi, st.event_count, st.id_lo, st.id_hi,
             COALESCE(r.lo, 0) # st.carried_lo AS lo, COALESCE(r.hi, 0) # st.carried_hi AS hi,
             COALESCE(r.ilo, 0) # st.carried_id_lo AS ilo, COALESCE(r.ihi, 0) # st.carried_id_hi AS ihi,
             COALESCE(r.n, 0) + st.carried_count AS n,
             r.s IS NULL AND NOT st.carries AS stored_only,
             (st.s IS NOT NULL AND (r.s IS NOT NULL OR st.carries)) AS on_both
      FROM stored st
      FULL JOIN recomputed r ON r.o = st.o AND r.t = st.t AND r.ty = st.ty AND r.s = st.s
    ),
    compared AS (
      SELECT e.o, e.t, e.ty, e.s, e.digest_lo, e.digest_hi, e.event_count, e.lo, e.hi, e.ilo, e.ihi, e.n,
             e.stored_only, e.on_both,
             (e.digest_lo <> e.lo OR e.digest_hi <> e.hi OR e.event_count <> e.n
              OR e.id_lo <> e.ilo OR e.id_hi <> e.ihi) AS drifted,
             (e.id_lo IS NULL OR e.id_hi IS NULL) AS unfilled
      FROM expected e
    ),
    rewrite AS (
      SELECT c.*, CASE WHEN c.drifted THEN 'drift_updated' ELSE 'filled' END AS why
      FROM compared c
      WHERE c.on_both AND (c.drifted OR ($3 AND c.unfilled))
    ),
    updated AS (
      UPDATE __SCHEMA__.wh_stream_digests d
      SET digest_lo = w.lo, digest_hi = w.hi, id_lo = w.ilo, id_hi = w.ihi, event_count = w.n, updated_at = NOW()
      FROM rewrite w
      WHERE d.origin_service_id = w.o AND d.scope_tenant = w.t AND d.event_type = w.ty AND d.stream_id = w.s
      RETURNING 1
    ),
    removed AS (
      DELETE FROM __SCHEMA__.wh_stream_digests d
      USING compared c
      WHERE c.stored_only
        AND d.origin_service_id = c.o AND d.scope_tenant = c.t AND d.event_type = c.ty AND d.stream_id = c.s
      RETURNING d.origin_service_id, d.scope_tenant, d.event_type, d.stream_id, d.digest_lo, d.digest_hi, d.event_count
    ),
    added AS (
      INSERT INTO __SCHEMA__.wh_stream_digests
        (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, id_lo, id_hi)
      SELECT r.o, r.t, r.ty, r.s, r.lo, r.hi, r.n, r.ilo, r.ihi
      FROM recomputed r
      WHERE NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_stream_digests d
        WHERE d.origin_service_id = r.o AND d.scope_tenant = r.t AND d.event_type = r.ty AND d.stream_id = r.s)
      ORDER BY 1, 2, 3, 4
      ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO NOTHING
      RETURNING origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count
    )
    SELECT w.why, w.o, w.t, w.ty, w.s, w.digest_lo, w.digest_hi, w.event_count, w.lo, w.hi, w.n
    FROM rewrite w
    UNION ALL
    SELECT 'drift_removed', x.origin_service_id, x.scope_tenant, x.event_type, x.stream_id,
           x.digest_lo, x.digest_hi, x.event_count, NULL::bigint, NULL::bigint, NULL::int
    FROM removed x
    UNION ALL
    SELECT 'drift_added', a.origin_service_id, a.scope_tenant, a.event_type, a.stream_id,
           NULL::bigint, NULL::bigint, NULL::int, a.digest_lo, a.digest_hi, a.event_count
    FROM added a
    $q$, v_events, v_digests);
END;
$$ LANGUAGE plpgsql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_heal_stream_digests_statement(BOOLEAN) IS
  '446 (#2337): the statement _wh_heal_stream_digests runs, for a named batch or the whole store: $1 streams, $2 settle '
  'window, $3 fill id lanes. Separate so it can be planned on its own.';


-- ── _wh_epoch_buckets: last word 453, copied whole, with the epoch's removed row folded in (465) ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#what-is-digested</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:EpochRecheck_AfterACut_FindsNoDrift_AndStillFindsADamagedSealAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:Refold_OfAnEpochACutTouched_KeepsTheRemovedPartInTheSealAsync</tests>
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
    SELECT u.t, u.ty, bit_xor(u.lo), bit_xor(u.hi), SUM(u.n)::int
    FROM (
      SELECT COALESCE(es.scope ->> 't', '') AS t, es.event_type::text AS ty,
             __SCHEMA__._wh_digest_lane(es.event_id, 0) AS lo,
             __SCHEMA__._wh_digest_lane(es.event_id, 1) AS hi,
             1 AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id IS NULL
        AND es.commit_sequence >= v_from AND es.commit_sequence < v_to
        AND __DIGEST_ELIGIBLE__(es)  -- 216 (#1492)
      UNION ALL
      -- 465 (#2363): what a cut removed from this epoch still counts in its fold.
      SELECT r.scope_tenant, r.event_type, r.removed_lo, r.removed_hi, r.removed_count
      FROM __SCHEMA__.wh_digest_epoch_removed r
      WHERE r.origin_service_id = p_lane AND r.epoch_id = p_epoch
    ) u
    GROUP BY 1, 2;
  ELSE
    RETURN QUERY
    SELECT u.t, u.ty, bit_xor(u.lo), bit_xor(u.hi), SUM(u.n)::int
    FROM (
      SELECT COALESCE(es.scope ->> 't', '') AS t, es.event_type::text AS ty,
             __SCHEMA__._wh_digest_lane(es.event_id, 0) AS lo,
             __SCHEMA__._wh_digest_lane(es.event_id, 1) AS hi,
             1 AS n
      FROM __SCHEMA__.wh_event_store es
      WHERE es.origin_service_id = p_lane
        AND es.origin_commit_sequence >= v_from AND es.origin_commit_sequence < v_to
        AND __DIGEST_ELIGIBLE__(es)  -- 216 (#1492)
      UNION ALL
      -- 465 (#2363): what a cut removed from this epoch still counts in its fold.
      SELECT r.scope_tenant, r.event_type, r.removed_lo, r.removed_hi, r.removed_count
      FROM __SCHEMA__.wh_digest_epoch_removed r
      WHERE r.origin_service_id = p_lane AND r.epoch_id = p_epoch
    ) u
    GROUP BY 1, 2;
  END IF;
END;
$$;

-- ── compute_type_digests_epoch: last word 453, copied whole, with the open window's removed rows (465) ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#unstamped-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:LocalLane_AnUnstampedRow_IsLeftOutOfTheOpenWindowUntilItIsStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:ReceivedLane_ARowWithNoOriginSequence_StillFoldsInTheOpenWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:EpochComposition_EqualsTheFullFold_AcrossClosedAndOpenRangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestSqlTests.cs:TypeAnswer_IsTheSameBeforeAndAfterACut_SealedOrOpenAsync</tests>
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
    -- The live remainder. #1791: in the local lane only stamped rows fold. A row the stamper has not yet
    -- sequenced is not settled here: the windowed answer, integrity_settled_max and the stamp fence all wait for its
    -- sequence, and once it has one it folds where that sequence puts it, so it never falls through the crack.
    -- A received row keeps the NULL-sequence branch: it can lack the origin's sequence for good (published
    -- before its stamp, or received before 087), and the open window is the only place it can ever fold.
    SELECT COALESCE(es.scope ->> 't', '') AS b_tenant, es.event_type AS b_type,
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 0)) AS lo,
           bit_xor(__SCHEMA__._wh_digest_lane(es.event_id, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
      AND (CASE WHEN p_origin_lane IS NULL
                THEN es.commit_sequence IS NOT NULL AND es.commit_sequence >= v_open_from
                ELSE es.origin_commit_sequence IS NULL OR es.origin_commit_sequence >= v_open_from
           END)
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND __DIGEST_ELIGIBLE__(es)  -- 216 (#1492)
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  ),
  open_removed AS (
    -- 465 (#2363): what a cut removed above the frontier (or with no position) still counts in the open window.
    SELECT r.scope_tenant AS b_tenant, r.event_type AS b_type,
           bit_xor(r.removed_lo) AS lo, bit_xor(r.removed_hi) AS hi, SUM(r.removed_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epoch_removed r
    WHERE r.origin_service_id = v_lane
      AND (r.epoch_id > v_frontier OR r.epoch_id = -1)
      AND (v_types IS NULL OR r.event_type = ANY(v_types))
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM open_fold UNION ALL SELECT * FROM open_removed) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;


-- ── compute_type_digests_epoch_window: last word 336, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>resilience/stream-integrity#windowed-manifests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestLaneReadTests.cs:TypeDigestWindow_ReadsTheWindow_NotTheLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
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
    -- 332 (#2080): one branch per lane, each with the window as a direct range on its own indexed column, so the read
    -- is the window's rows (idx_event_store_commit_sequence for the local lane, the 155 lane index for an origin's)
    -- instead of the whole lane filtered through a CASE no index can serve. Exactly one branch is live per call.
    SELECT es.*, es.commit_sequence AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NULL AND es.origin_service_id IS NULL
      AND es.commit_sequence >= p_since AND es.commit_sequence < p_until
    UNION ALL
    SELECT es.*, es.origin_commit_sequence AS lane_seq
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NOT NULL AND es.origin_service_id = p_origin_lane
      AND es.origin_commit_sequence >= p_since AND es.origin_commit_sequence < p_until
  ),
  live_fold AS (
    -- The window minus the sealed run: the low fringe [since, e_lo*width) and the tail
    -- [(e_hi+1)*width, until), or the whole window when no epoch fits (e_lo > e_hi).
    SELECT COALESCE(l.scope ->> 't', '') AS b_tenant, l.event_type AS b_type,
           bit_xor(__SCHEMA__._wh_digest_lane(l.event_id, 0)) AS lo,
           bit_xor(__SCHEMA__._wh_digest_lane(l.event_id, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM lane l
    WHERE l.lane_seq IS NOT NULL
      AND l.lane_seq >= p_since AND l.lane_seq < p_until
      AND (v_e_lo > v_e_hi
           OR l.lane_seq < v_e_lo * v_width
           OR l.lane_seq >= (v_e_hi + 1) * v_width)
      AND (v_types IS NULL OR l.event_type = ANY(v_types))
      AND __DIGEST_ELIGIBLE__(l)  -- 216 (#1492)
      AND l.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM live_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_type_digests_epoch_window(UUID, TEXT[], BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'Windowed type-level fold [p_since, p_until): fully-contained sealed epochs answer from their seal, fringes fold live. Caller pre-clamps p_until at integrity_settled_max. 214: the live fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 332 (#2080): the window is a direct range on the lane''s indexed sequence column, one branch per lane, so a call reads its window, not the lane.';

-- ── wh_compute_stream_digests_window: last word 347, copied whole ──
-- <docs>resilience/stream-integrity#windowed-manifests</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestTypeIndexTests.cs:TypeFilteredWindow_ReadsThroughAnIndexConditionOnTheType_UnderAGenericPlanAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestTypeIndexTests.cs:TheRead_PlansEachCallAgainstItsOwnWindow_WhateverTheCallersPlanModeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestLaneReadTests.cs:ManifestStreamWindow_ReadsTheWindow_NotTheLaneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochWindowedDigestSqlTests.cs:StreamWindow_OnlyEventsInsideTheWindowContributeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_compute_stream_digests_window(
  p_origin_lane    UUID,          -- NULL = the local lane
  p_event_types    TEXT[],        -- NULL = all types
  p_since          BIGINT,        -- inclusive
  p_until          BIGINT,        -- exclusive, pre-clamped at the settled max
  p_resume_after   UUID,          -- page start: only streams above this id (NULL = from the first)
  p_max_streams    INTEGER,       -- whole streams on this page
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, stream_id UUID, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER,
                id_lo BIGINT, id_hi BIGINT)
LANGUAGE plpgsql STABLE
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_types   TEXT[];
  v_settled TIMESTAMPTZ := p_now - make_interval(secs => p_settle_seconds);
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(DISTINCT __SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  RETURN QUERY
  WITH win AS (
    -- One branch per lane (332), each with the window as a direct range on its own indexed column. Exactly one is live
    -- per call; under the custom plan the other is pruned and the type list is a constant the planner can index.
    SELECT es.event_id AS w_event, es.stream_id AS w_stream, es.event_type::text AS w_type,
           COALESCE(es.scope ->> 't', '') AS w_tenant
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NULL AND es.origin_service_id IS NULL
      AND es.commit_sequence >= p_since AND es.commit_sequence < p_until
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND __DIGEST_ELIGIBLE__(es)
      AND es.created_at < v_settled
      AND (p_resume_after IS NULL OR es.stream_id > p_resume_after)
    UNION ALL
    SELECT es.event_id, es.stream_id, es.event_type::text, COALESCE(es.scope ->> 't', '')
    FROM __SCHEMA__.wh_event_store es
    WHERE p_origin_lane IS NOT NULL AND es.origin_service_id = p_origin_lane
      AND es.origin_commit_sequence >= p_since AND es.origin_commit_sequence < p_until
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND __DIGEST_ELIGIBLE__(es)
      AND es.created_at < v_settled
      AND (p_resume_after IS NULL OR es.stream_id > p_resume_after)
  ),
  last_picked AS (
    -- The page's last stream: the p_max_streams-th distinct stream id in order. Every row up to it is on the page.
    SELECT p.w_stream
    FROM (SELECT DISTINCT w.w_stream FROM win w ORDER BY w.w_stream LIMIT p_max_streams) p
    ORDER BY p.w_stream DESC
    LIMIT 1
  )
  SELECT w.w_tenant, w.w_type, w.w_stream,
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 0)),
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 1)),
         COUNT(*)::int,
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 2)),
         bit_xor(__SCHEMA__._wh_digest_lane(w.w_event, 3))
  FROM win w
  WHERE w.w_stream <= (SELECT lp.w_stream FROM last_picked lp)
  GROUP BY 1, 2, 3
  ORDER BY 3, 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_compute_stream_digests_window(UUID, TEXT[], BIGINT, BIGINT, UUID, INTEGER, TIMESTAMPTZ, INTEGER) IS
'Windowed stream-level fold, every lane from _wh_digest_lane (336) [p_since, p_until), one page of whole streams after p_resume_after (#2107). Caller pre-clamps p_until at integrity_settled_max and asks for one stream past its page bound as the sentinel. Plans each call against its own window and types (force_custom_plan), so a sparse type is read through an index on the type rather than filtered from every event in the window. Leaves out ephemeral (flags & 8) and at-most-once (flags & 32) events and audit records.';

-- ── wh_seed_stream_digests_batch: last word 336, copied whole ──
-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- <docs>operations/infrastructure/migrations#batched-regions</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationWorklistBatchTests.cs:TheStreamDigestSeedClaimsEachStreamExactlyOnceAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationWorklistBatchTests.cs:TheStreamDigestSeedKeepsEveryBucketWholeAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:SeedStreamDigestsBatch_FoldsEveryLaneOfTheSeededStreamsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_seed_stream_digests_batch(p_limit INT)
RETURNS BIGINT AS $$
DECLARE
  v_from BIGINT;
  v_to   BIGINT;
  v_rows BIGINT := 0;
BEGIN
  IF to_regclass('__SCHEMA__.wh_stream_digest_seed_todo') IS NULL THEN
    RETURN 0;
  END IF;

  SELECT last_seq INTO v_from FROM __SCHEMA__.wh_stream_digest_seed_cursor WHERE only_row;
  v_from := coalesce(v_from, 0);

  SELECT max(seq) INTO v_to
  FROM (SELECT seq FROM __SCHEMA__.wh_stream_digest_seed_todo
        WHERE seq > v_from ORDER BY seq LIMIT p_limit) s;

  -- Exhausted worklist is the only way the loop ends, independent of how many buckets the last slice
  -- happened to write -- a replay writes none and must still advance to the end.
  IF v_to IS NULL THEN
    RETURN 0;
  END IF;

  -- Identical projection, predicate and grouping to the unbounded form this replaced, restricted to
  -- the slice's streams. Keeping it byte-identical matters: any change here changes stored digests,
  -- which the periodic sweep then reports as corruption.
  -- 336 (#2106): the same projection and predicate, folded through the one helper; 'seed' writes a bucket only
  -- where none exists, as ON CONFLICT DO NOTHING did.
  SELECT __SCHEMA__._wh_fold_stream_digests(
    array_agg(COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid)),
    array_agg(COALESCE(es.scope ->> 't', '')), array_agg(es.event_type), array_agg(es.stream_id),
    array_agg(es.event_id), 'seed', NOW())
  INTO v_rows
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = es.event_id
  WHERE es.stream_id IN (SELECT t.stream_id FROM __SCHEMA__.wh_stream_digest_seed_todo t
                         WHERE t.seq > v_from AND t.seq <= v_to)
    AND __DIGEST_ELIGIBLE__(es)
    AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1;

  UPDATE __SCHEMA__.wh_stream_digest_seed_cursor SET last_seq = v_to WHERE only_row;

  -- At least one, so the runner keeps going while entries remain: a slice can legitimately write
  -- nothing on a replay, and reporting that would stop the pass with work outstanding.
  RETURN GREATEST(v_rows, 1);
END;
$$ LANGUAGE plpgsql SET enable_seqscan = off;


SELECT __SCHEMA__.drop_all_overloads('wh_recompute_superseding_digests');
SELECT __SCHEMA__.drop_all_overloads('_wh_hand_on_slots');
SELECT __SCHEMA__.drop_all_overloads('_wh_is_slot_anchor');
SELECT __SCHEMA__.drop_all_overloads('_wh_slot_id');
DROP INDEX IF EXISTS __SCHEMA__.idx_event_store_slot_at;
-- RECLAIM: not required. The dropped columns are NULL or zero on every row this reversal reaches unless a cut or a
--          reap wrote them, and a NULL takes no heap bytes; where they hold values, run pg_repack on wh_event_store after the
--          reversal to return the space.
ALTER TABLE __SCHEMA__.wh_event_store DROP COLUMN IF EXISTS supersede_slot_type;  -- RECLAIM: as noted above
ALTER TABLE __SCHEMA__.wh_event_store DROP COLUMN IF EXISTS supersede_slot_at;  -- RECLAIM: as noted above
DELETE FROM __SCHEMA__.wh_settings WHERE setting_key = 'superseding_slot_digests_recomputed';
