-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 391_CollectiveRowsOnlyForHandledTypes.sql
-- Date: 2026-11-10
-- Description: A collective event gets a __collective__ perspective row only when a handler on this service applies its
--              type (#2145).
--
--              The emit chains routed every stored collective event to the __collective__ sink, whether or not a
--              [CollectiveApplyFor] handler here applies it. A service stores the collective events of every namespace
--              it subscribes to, so most such rows were written, claimed and completed without applying (#2142) for
--              nothing. The startup associations step now writes, beside the perspective associations:
--
--                collective       one row per collective event type a handler on the service applies, from the
--                                 consumer registry (target __collective__)
--                collective_gate  one marker row per service, saying its registration gates
--
--              _wh_collective_row_wanted(event_type) is true unless the gate is on and the type is not associated. The
--              gate is on only when a marker exists and every service with associations in the schema has one, so:
--
--                Mixed fleet      the migration lands before any instance of this version registers, and an older
--                                 instance's registration deletes this version's rows as orphans. Either way no marker
--                                 means every row is created, as before.
--                Unknown set      a service that cannot tell what it handles writes no marker, so nothing is gated.
--                Shared schema    a second service without a marker keeps the gate off for the schema.
--
--              The function runs only for a stored collective event, after the flag test, against wh_message_associations
--              (a few rows per service); an ordinary event pays nothing. The two emit chains are copied whole from their
--              last words with the one condition added to each collective routing insert.
--
--              Number 391: 373 to 390 are taken by changes on develop or in flight.
--
-- Reversal: reversible (the helper is dropped and both emit chains are restored from their last words, 352 and 367;
--           the collective and collective_gate association rows the startup step writes are inert without the helper)
-- Dependencies: 352 (_emit_event_store_chain's last word), 367 (_emit_event_store_chain_for_inbox's last word),
--               008 (wh_message_associations.normalized_message_type)
-- Objects: _wh_collective_row_wanted, _emit_event_store_chain, _emit_event_store_chain_for_inbox
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_PERSPECTIVE__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Whether a stored collective event of p_event_type gets its __collective__ row. True unless every service with
-- associations in the schema has written its collective_gate marker and none of them associates the type.
-- <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_AMarkerWithNoHandledType_GatesEveryCollectiveEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveRowGateSqlTests.cs:Outbox_AnotherServiceInTheSchemaWithoutAMarker_KeepsEveryRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_collective_row_wanted(p_event_type TEXT)
RETURNS BOOLEAN AS $$
  SELECT NOT EXISTS (
           SELECT 1 FROM __SCHEMA__.wh_message_associations g WHERE g.association_type = 'collective_gate')
      -- A service registered here that does not gate (an older release, or one that cannot say what it handles).
      OR EXISTS (
           SELECT 1 FROM __SCHEMA__.wh_message_associations a
           WHERE a.association_type <> 'collective_gate'
             AND NOT EXISTS (
               SELECT 1 FROM __SCHEMA__.wh_message_associations g
               WHERE g.association_type = 'collective_gate' AND g.service_name = a.service_name))
      OR EXISTS (
           SELECT 1 FROM __SCHEMA__.wh_message_associations c
           WHERE c.normalized_message_type = p_event_type AND c.association_type = 'collective');
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_collective_row_wanted(TEXT) IS
  'Whether a stored collective event of the type gets its __collective__ perspective row (391, #2145): true unless a '
  'collective_gate marker exists, every service with associations in the schema has one, and no service associates '
  'the type as collective. Missing knowledge creates rows; it never drops them.';

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
    WHERE COALESCE(c.flags, 0) & 8 = 0
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
      -- 216 (#1492): audit records are never digested.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
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

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
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
    WHERE COALESCE(c.flags, 0) & 8 = 0
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
      -- 216 (#1492): audit records are never digested; a received one no longer lands in its origin's lane.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
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
  'Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each received event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each received event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 367: a pass whose lock pass finds no candidate only stamps, and the collective routing runs only when a stored event is collective. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145).';
