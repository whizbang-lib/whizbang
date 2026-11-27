-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 720_VectorEventBodiesArePackedOnceEveryInstanceReadsThem.sql
-- Date: 2026-11-27
-- Description: The stored body of a vector event holds its vectors packed binary32, written by every write path once
--              every live instance reads the packed form (#2625, V2 of the vector storage plan, #2333).
--
--              The packed form (read by every reader since #2624): a JSON array of numbers becomes one JSON string,
--              base64 without line breaks of the big-endian IEEE-754 binary32 bits of each element in order.
--              _wh_pack_vectors writes it at the paths the generated message JSON context registers for the event's
--              type: each number's stored text goes through float4in (correctly rounded) and float4send (network order),
--              so the bits are exactly what a C# reader reads from the stored array. jsonb holds a JSON number as
--              numeric, which has no negative zero, so -0 was already stored as 0; packing keeps what the body holds. A
--              value that is not a non-empty array of numbers that fit binary32 (a null, a string, a nested array, a
--              number out of range) is left as it is, never failed on.
--
--              Where: at the stored body only. Both emit chains and wh_append_event pack the body they write to
--              wh_event_body; the outbox and inbox rows, which the transport and other services read, keep the wire
--              form. An event of a type with no paths runs nothing new: the chains read the paths in a lateral whose
--              only filter on the gate is a one-time filter, so a closed gate scans nothing, and an open one probes the
--              paths table's primary key once per event and calls the packing only for a type that has a row.
--
--                the paths      wh_event_vector_paths (event_type, paths, service_name), written by the
--                               vector-packing-gate maintenance step through wh_register_event_vector_paths: a
--                               service writes its own rows and replaces the ones it no longer declares; the latest
--                               registration of a type wins (every service derives it from the same declaration)
--                the marker     each heartbeat of this release writes stored_form_level 2 (HeartbeatRequest
--                               .STORED_FORM_LEVEL), which reads packed vectors; the release before writes 1
--                the gate       wh_open_vector_packing_gate, called by the step each cycle, journals the conversion
--                               once at least one instance is live (wh_live_instance_ids) and every live instance
--                               records level 2 or higher: a wh_stored_form_migrations row named
--                               wh_event_body:packed-vectors, applied by this instance's library version
--                the switch     the journal row itself (_wh_vector_packing_on): nothing can pack unjournaled. One
--                               primary-key probe per write call, beside the trim gate's
--                the latch      one-way. The rollback gate (#2022) reads the row like any conversion
--                               (stored-form:wh_event_body:packed-vectors) and stands an older library down at Assess,
--                               naming it. No application version is recorded: reading packed vectors is a library
--                               capability, so an application rolled back on the same library is not stood down
--
--              Number 720: reserved for this change (720 to 729).
--
-- Reversal: reversible (the down file restores each write path's last word; packed bodies and the journal row stay,
--           and the journal row keeps standing down a library that cannot read them)
-- Dependencies: 573 (_emit_event_store_chain), 574 (_emit_event_store_chain_for_inbox), 537 (wh_append_event,
--               _wh_metadata_trim_on), 316 (wh_live_instance_ids), 176 and 385 (wh_stored_form_migrations)
-- Objects: wh_event_vector_paths, _wh_pack_vector, _wh_pack_vectors, wh_register_event_vector_paths,
--          _wh_vector_packing_on, wh_open_vector_packing_gate, _emit_event_store_chain, _emit_event_store_chain_for_inbox,
--          wh_append_event
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_pack_vector');
SELECT __SCHEMA__.drop_all_overloads('_wh_pack_vectors');
SELECT __SCHEMA__.drop_all_overloads('wh_register_event_vector_paths');
SELECT __SCHEMA__.drop_all_overloads('_wh_vector_packing_on');
SELECT __SCHEMA__.drop_all_overloads('wh_open_vector_packing_gate');

-- ===========================================================================================
-- The paths
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_vector_paths (
  event_type TEXT PRIMARY KEY,
  paths JSONB NOT NULL,
  service_name TEXT NOT NULL,
  registered_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_event_vector_paths IS
  'Issue #2625: each event type''s vector paths (a JSON array of arrays of JSON names from the payload root), keyed by '
  'wh_event_store.event_type, written by the vector-packing-gate step from the generated registry. The write paths pack '
  'the stored body at these paths once the packing gate is open.';

-- Writes one service's vector paths: p_entries is [{"t": event type, "p": [[segment, ...], ...]}, ...]. Upserts each
-- type (the latest registration wins), and deletes the rows this service wrote for a type it no longer declares. A row
-- that is unchanged is not rewritten. Returns the rows written or deleted.
-- <docs>data/event-store#packing-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Paths_TheStepWritesThisServicesGeneratedPaths_AndReplacesOnlyItsOwnAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Paths_TheRegistrationRefusesAnEntryWithoutPathsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_register_event_vector_paths(p_service_name TEXT, p_entries JSONB)
RETURNS INTEGER AS $$
DECLARE
  v_written INTEGER;
  v_deleted INTEGER;
BEGIN
  IF EXISTS (SELECT 1 FROM jsonb_array_elements(p_entries) e
             WHERE jsonb_typeof(e -> 'p') IS DISTINCT FROM 'array' OR jsonb_array_length(e -> 'p') = 0
                OR COALESCE(e ->> 't', '') = '') THEN
    RAISE EXCEPTION 'Every vector path entry names its event type and at least one path: %', p_entries;
  END IF;

  WITH entries AS (
    SELECT DISTINCT ON (e ->> 't') e ->> 't' AS event_type, e -> 'p' AS paths
    FROM jsonb_array_elements(p_entries) e
  ), written AS (
    INSERT INTO __SCHEMA__.wh_event_vector_paths (event_type, paths, service_name, registered_at)
    SELECT en.event_type, en.paths, p_service_name, NOW() FROM entries en
    ON CONFLICT (event_type) DO UPDATE
      SET paths = EXCLUDED.paths, service_name = EXCLUDED.service_name, registered_at = NOW()
      WHERE __SCHEMA__.wh_event_vector_paths.paths IS DISTINCT FROM EXCLUDED.paths
         OR __SCHEMA__.wh_event_vector_paths.service_name IS DISTINCT FROM EXCLUDED.service_name
    RETURNING 1
  )
  SELECT count(*) INTO v_written FROM written;

  DELETE FROM __SCHEMA__.wh_event_vector_paths p
  WHERE p.service_name = p_service_name
    AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements(p_entries) e WHERE e ->> 't' = p.event_type);
  GET DIAGNOSTICS v_deleted = ROW_COUNT;

  RETURN v_written + v_deleted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_register_event_vector_paths(TEXT, JSONB) IS
  'Issue #2625: writes one service''s event vector paths ([{"t": event type, "p": [[segment, ...]]}]): upserts each type, '
  'the latest registration winning, and deletes the rows this service wrote for types it no longer declares.';

-- ===========================================================================================
-- Packing
-- ===========================================================================================

-- One vector, packed: a non-empty JSON array of numbers becomes the base64 (no line breaks) of each element's
-- big-endian binary32 bits. The element's stored text is parsed by float4in, which rounds correctly, so the bits are
-- those a C# reader reads from the array. Anything else (not an array, empty, holding a non-number, or a number that
-- does not fit binary32) is returned as it is.
-- <docs>data/event-store#packed-vectors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:Pack_WritesTheBitsACSharpReaderReadsFromTheStoredArray_ForRandomAndEdgeValuesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:Pack_LeavesAValueThatIsNotAnArrayOfBinary32Numbers_AsItIsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_pack_vector(p_value JSONB)
RETURNS JSONB AS $$
DECLARE
  v_bits BYTEA;
BEGIN
  IF jsonb_typeof(p_value) IS DISTINCT FROM 'array' OR jsonb_array_length(p_value) = 0
     OR p_value @? 'strict $[*] ? (@.type() != "number")' THEN
    RETURN p_value;
  END IF;
  BEGIN
    SELECT string_agg(float4send(e.t::float4), ''::bytea ORDER BY e.n) INTO v_bits
    FROM jsonb_array_elements_text(p_value) WITH ORDINALITY AS e(t, n);
  EXCEPTION WHEN numeric_value_out_of_range THEN
    -- A number outside binary32 (or one that would round to zero) is not a float a reader holds: keep the array.
    RETURN p_value;
  END;
  RETURN to_jsonb(translate(encode(v_bits, 'base64'), E'\n', ''));
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_pack_vector(JSONB) IS
  'Issue #2625: one vector packed: a non-empty JSON array of numbers becomes base64 (no line breaks) of each element''s '
  'big-endian binary32 bits; anything else is returned as it is.';

-- A body with the vectors at p_paths packed (_wh_pack_vector). A path that is missing, or whose value is not an array,
-- is passed over. Null paths return the body unchanged.
-- <docs>data/event-store#packed-vectors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:Pack_FollowsEveryPath_NestedAndMissing_AndLeavesTheRestOfTheBodyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WritePath_StoresJsonNumbersWhileTheGateIsClosed_AndPackedVectorsOnceItIsOpenAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_pack_vectors(p_body JSONB, p_paths JSONB)
RETURNS JSONB AS $$
DECLARE
  v_path TEXT[];
  v_body JSONB := p_body;
BEGIN
  IF p_body IS NULL OR p_paths IS NULL THEN
    RETURN p_body;
  END IF;
  FOR v_path IN SELECT ARRAY(SELECT jsonb_array_elements_text(p.path)) FROM jsonb_array_elements(p_paths) AS p(path) LOOP
    IF jsonb_typeof(v_body #> v_path) = 'array' THEN
      v_body := jsonb_set(v_body, v_path, __SCHEMA__._wh_pack_vector(v_body #> v_path), false);
    END IF;
  END LOOP;
  RETURN v_body;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_pack_vectors(JSONB, JSONB) IS
  'Issue #2625: the body with the vectors at each path ([[segment, ...], ...]) packed binary32 (_wh_pack_vector).';

-- ===========================================================================================
-- The gate
-- ===========================================================================================

-- Whether the write paths pack: the packed-vectors conversion is journaled with a known applier. The journal row is the
-- switch, so nothing packs without the record the rollback gate reads. One primary-key probe.
-- <docs>data/event-store#packing-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Switch_IsTheJournalRow_ARowWithNoApplierDoesNotOpenItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WritePath_StoresJsonNumbersWhileTheGateIsClosed_AndPackedVectorsOnceItIsOpenAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_vector_packing_on()
RETURNS BOOLEAN AS $$
  SELECT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_stored_form_migrations j
    WHERE j.name = 'wh_event_body:packed-vectors' AND j.applied_library_version IS NOT NULL);
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_vector_packing_on() IS
  'Issue #2625: whether the packing gate is open (the wh_stored_form_migrations row wh_event_body:packed-vectors has an '
  'applier), read once per call by the write paths.';

-- Opens the packing gate once every live instance reads packed vectors: at least one instance is live (the one liveness
-- rule), and each live instance's registry row says stored_form_level 2 or higher. Opening journals the conversion,
-- applied by p_library_version (an unknown version keeps the gate closed: an unjournaled conversion could not stand an
-- older library down). One-way. Returns 'open' (already), 'opened' (this call), or 'closed (...)'.
-- <docs>data/event-store#packing-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_AFreshService_OpensOnlyOnceThisInstanceHasRegisteredWithItsMarkerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_StaysClosedWhileALiveInstanceReadsNoPackedVectors_AndOpensOnceItIsGoneStaleOrRaisedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_OnceOpen_StaysOpen_WhenAnInstanceWithoutTheMarkerAppearsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_WithoutALibraryVersion_StaysClosed_SoNothingPacksUnjournaledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Journal_RecordsTheLibraryThatStartedPacking_AndNoApplicationVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Rollback_AnOlderLibrary_StandsDownAtAssess_NamingPackedVectorsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_open_vector_packing_gate(p_library_version TEXT)
RETURNS TEXT AS $$
DECLARE
  v_live BIGINT;
  v_ready BIGINT;
  v_opened BOOLEAN;
BEGIN
  IF __SCHEMA__._wh_vector_packing_on() THEN
    RETURN 'open';
  END IF;
  IF COALESCE(btrim(p_library_version), '') = '' THEN
    RETURN 'closed (this instance''s library version is unknown, so the conversion cannot be journaled)';
  END IF;

  SELECT count(*),
         count(*) FILTER (WHERE jsonb_typeof(si.metadata -> 'stored_form_level') = 'number'
                            AND (si.metadata ->> 'stored_form_level')::NUMERIC >= 2)
    INTO v_live, v_ready
  FROM __SCHEMA__.wh_service_instances si
  WHERE si.instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l);

  IF v_live = 0 OR v_ready < v_live THEN
    RETURN 'closed (' || v_ready || ' of ' || v_live || ' live instances read packed vectors)';
  END IF;

  WITH journaled AS (
    INSERT INTO __SCHEMA__.wh_stored_form_migrations
      (name, table_name, kind, declared_at, first_applied_at, last_applied_at, rows_converted, applied_library_version)
    VALUES ('wh_event_body:packed-vectors', 'wh_event_body', 'library', NOW(), NOW(), NOW(), 0, p_library_version)
    ON CONFLICT (name) DO UPDATE
      SET applied_library_version = EXCLUDED.applied_library_version,
          first_applied_at = COALESCE(__SCHEMA__.wh_stored_form_migrations.first_applied_at, NOW()),
          last_applied_at = NOW()
      WHERE __SCHEMA__.wh_stored_form_migrations.applied_library_version IS NULL
    RETURNING 1
  )
  SELECT EXISTS (SELECT 1 FROM journaled) INTO v_opened;

  RETURN CASE WHEN v_opened THEN 'opened' ELSE 'open' END;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_open_vector_packing_gate(TEXT) IS
  'Issue #2625: journals the packed-vectors conversion (wh_stored_form_migrations wh_event_body:packed-vectors, applied '
  'by p_library_version), which opens the write paths'' packing, once at least one instance is live and every live '
  'instance''s registry metadata says stored_form_level >= 2. One-way. Returns open, opened, or closed (...). Called by '
  'the vector-packing-gate maintenance step.';

-- ===========================================================================================
-- The write paths
-- ===========================================================================================

-- ── _emit_event_store_chain: last word 573, copied whole, its stored body packed under the gate (720) ──
SELECT __SCHEMA__.drop_all_overloads('_emit_event_store_chain');
-- <docs>data/event-store#packed-vectors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WritePath_StoresJsonNumbersWhileTheGateIsClosed_AndPackedVectorsOnceItIsOpenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WirePath_TheOutboxAndInboxRowsKeepJsonNumbers_WhileTheStoredBodyIsPackedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:EmitChain_AnOrdinaryEvent_NeverRunsThePacking_EvenWithTheGateOpen_AndAVectorEventDoesAsync</tests>
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
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_LeavesOutTheMessageIdAndTheHopScopeTheRowHolds_AndNothingElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_KeepsWhatTheRowDoesNotHoldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:FullRows_KeepEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesReadTests.cs:EveryReader_ReadsATrimmedRow_AsItReadTheSameRowInTheSlimFormAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipByMapStoreSqlTests.cs:EmitChain_InMapMode_LeasesByPartition_NeverToTheStoredOwnerAsync</tests>
-- <docs>fundamentals/event-store/version-ordering#emission-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:EmitChain_EventsListedAgainstTheirIdOrder_AreVersionedInTheOrderListedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EmissionOrderVersioningSqlTests.cs:EmitChain_AnIdListedTwice_KeepsItsFirstPositionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreVersionOrderingSqlTests.cs:EmitChain_BatchedShuffledMessageIds_InsertsInTheBatchsOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreVersionOrderingSqlTests.cs:EmitEventStoreChain_AssignsVersions_InTheOrderTheBatchListsThem_NotByIdOrCreatedAtAsync</tests>
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
  v_trim BOOLEAN;                          -- 537: the trim gate is open (wh_settings metadata_trim_row_values)
  v_pack BOOLEAN;                          -- 720: the packing gate is open (the packed-vectors conversion is journaled)
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
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
  -- 537 (#2323): the trim gate, read once for the whole batch, beside the settings.
  v_trim := __SCHEMA__._wh_metadata_trim_on();
  -- 720 (#2625): the packing gate, read once for the whole batch, beside the trim gate.
  v_pack := __SCHEMA__._wh_vector_packing_on();

  -- Phase 4.5A-equivalent: store outbox events into wh_event_store with sequential versioning.
  -- 573 (#2551): a stream's events are numbered in the order the batch lists them, which is the order the producer
  -- emitted them (store_outbox_messages hands them over in that order), never by id. An id is time-ordered and says
  -- nothing about stream position: a derived id sorts at its cause, so an effect derived from a cause can sort before
  -- it. An id listed twice keeps its first position.
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
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY batch.position) AS row_num
    FROM (
      SELECT listed.message_id, min(listed.position) AS position
      FROM unnest(p_outbox_message_ids) WITH ORDINALITY AS listed(message_id, position)
      GROUP BY listed.message_id
    ) AS batch
    JOIN __SCHEMA__.wh_outbox o ON o.message_id = batch.message_id
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
      -- 720 (#2625): once the packing gate is open, the stored body holds the vectors of a type with registered paths
      -- packed binary32. The wire form (oe.event_data) is unchanged. An event of a type with no paths runs nothing new.
      CASE WHEN vp.paths IS NULL THEN COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload')
           ELSE __SCHEMA__._wh_pack_vectors(COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload'), vp.paths)
      END AS body_data,
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
    -- 720 (#2625): the type's vector paths, read only while the gate is open (a one-time filter: closed, no scan).
    LEFT JOIN LATERAL (
      SELECT p.paths FROM __SCHEMA__.wh_event_vector_paths p
      WHERE v_pack AND p.event_type = __SCHEMA__.normalize_event_type(oe.message_type)
    ) vp ON true
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
        -- 537 (#2323): once the trim gate is open, less what the row holds in its own columns, its event id and scope.
        WHEN v_trim THEN __SCHEMA__._wh_trim_row_values(
               __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id), cr.message_id, cr.scope::jsonb)
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
      array_agg(c.stream_id),
      -- 461 (#2364): a superseding copy counts as its key's slot, once: only when the store holds no copy of its key yet,
      -- and only the first such copy of this batch. The slot is passed already: the rows this statement stores are not
      -- visible to it, so the helper could not read them back.
      array_agg(CASE WHEN c.supersede_key IS NULL THEN c.message_id
                     ELSE __SCHEMA__._wh_slot_id(c.stream_id, COALESCE(c.supersede_group, c.event_type), c.supersede_key) END),
      'fold', p_now, TRUE) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE __DIGEST_ELIGIBLE__(c)
      AND (c.supersede_key IS NULL
           OR (NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_event_store o
                 WHERE o.stream_id = c.stream_id
                   AND COALESCE(o.supersede_group, o.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND o.supersede_key = c.supersede_key)
               AND NOT EXISTS (
                 SELECT 1 FROM computed c2
                 JOIN stored_events se2 ON se2.event_id = c2.message_id
                 WHERE c2.stream_id = c.stream_id
                   AND COALESCE(c2.supersede_group, c2.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND c2.supersede_key = c.supersede_key
                   AND se2.version < se.version)))
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
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
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
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
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
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
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
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
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
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145). 537 (#2323): the slim row leaves out its message id when it is the event id, and its hop scope when it is the scope column alone. 567 (#2555): with the service''s switch on, no stored owner is pinned or leased to; leased by partition (wh_i_own). 573 (#2551): a stream''s events are numbered in the order the id array lists them, the order they were emitted, never by id. 720 (#2625): once the packing gate is open, the stored body of a type with registered vector paths holds them packed binary32 (_wh_pack_vectors); the wire form is unchanged.';

-- ── _emit_event_store_chain_for_inbox: last word 574, copied whole, its stored body packed under the gate (720) ──
SELECT __SCHEMA__.drop_all_overloads('_emit_event_store_chain_for_inbox');
-- <docs>data/event-store#packed-vectors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WritePath_StoresJsonNumbersWhileTheGateIsClosed_AndPackedVectorsOnceItIsOpenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WirePath_TheOutboxAndInboxRowsKeepJsonNumbers_WhileTheStoredBodyIsPackedAsync</tests>
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
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:InboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <docs>fundamentals/work-coordinator/claim-loop#a-row-waiting-for-its-stream-head</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_ARowHeldBackBehindAnUnstoredHead_ShowsItAwaitsTheStreamHeadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_AWaitingRowWhoseLeaseLapsed_IsReclaimedStillAwaitingTheHead_NotAsALeaseExpiryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_OnceTheHeadIsHeld_TheWaitingRowIsStored_AndNoLongerShowsItWaitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_AHeldBackRowCarryingAnEarlierLeaseExpiry_ShowsItAwaitsTheHeadInsteadAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AwaitingStreamHeadSqlTests.cs:Claim_AHeldBackRowCarryingAnotherRecordedFailure_KeepsItAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <docs>fundamentals/event-store/version-ordering#receive-order</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:OneClaim_AnOriginsEvents_AreVersionedByTheOriginsSequence_NotByIdAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:AnUnpositionedEvent_TakesTheReplaysPlace_AmongItsOriginsPositionedEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:TwoOrigins_KeepTheirArrivalSlots_EachDealtInItsOwnOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:AnOriginsEarlierEvent_HeldElsewhere_HoldsBackTheLaterOne_WhateverTheIdsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveOrderVersioningSqlTests.cs:ALaterArrivalHeldElsewhere_DoesNotHoldBackAnEarlierArrivalOfAnotherOriginAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcileRewindDeclarationSqlTests.cs:InboxEmitChain_LateEventBelowCursor_DeclaresRewindOnTheCursorAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcileRewindDeclarationSqlTests.cs:InboxEmitChain_EventAboveCursor_DoesNotDeclareRewindAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReconcileRewindDeclarationSqlTests.cs:InboxEmitChain_AnotherOriginsEvent_WithALowerId_DoesNotDeclareRewindAsync</tests>
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
  v_trim BOOLEAN;                          -- 537: the trim gate is open (wh_settings metadata_trim_row_values)
  v_pack BOOLEAN;                          -- 720: the packing gate is open (the packed-vectors conversion is journaled)
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  v_any_collective BOOLEAN;                -- 367: an event this pass stored carries the collective flag
  -- 551 (#1630): the failure reasons a held-back row's state is read and written with (MessageFailureReason).
  c_reason_awaiting_stream_head CONSTANT INTEGER := 30;  -- AwaitingStreamHead
  c_reason_lease_expired CONSTANT INTEGER := 6;          -- LeaseExpired, as the claim's lapsed-lease branch records it
  c_reason_unknown CONSTANT INTEGER := 99;               -- Unknown, the column's default: a row that has not failed
  -- 566 (#2554, #2555): the service's switch, read once: on, the partition ownership table alone decides.
  v_map BOOLEAN := __SCHEMA__.wh_partition_ownership_by_map();
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
    SET chain_emitted_at = p_now,
        -- 551 (#1630): a row that waited for its stream's head and is now stored no longer waits.
        failure_reason = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN c_reason_unknown
                              ELSE ist.failure_reason END,
        error = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN NULL ELSE ist.error END
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
  --
  -- 574 (#2551): the candidates are listed in RECEIVE ORDER, the order this pass versions each stream in and the order
  -- the refusals judge "first" and "after" by (their position in these arrays). Never by id: an id is time-ordered,
  -- never stream position. Receive order is the order a replay folds the stream in (ReplayEventOrder, #2257), so what
  -- is stored and what a replay folds agree:
  --   * each origin's events in the origin's order: its commit sequence (the origin position the event carries);
  --   * an origin's event that carries no position (0: a fan-out child, or a redelivery that lost it) goes before the
  --     first of its origin's positioned events, in origin order, whose id sorts after its own, and after them all when
  --     none does. This is the replay's rule for it (#2379) and the one fallback to an id: nothing positional exists;
  --   * between origins, the arrival order: each origin's events are dealt, in the origin's order, into the arrival
  --     slots that origin holds (received_at, then origin, then the origin's own order breaking a tie).
  -- A received event of this service, or one with no origin, is one origin (the empty id), as the copy files it.
  WITH cand AS (
    SELECT ist.message_id, ist.stream_id, i.flags, i.message_type, ist.received_at,
           CASE WHEN i.source_service_id IS NULL OR i.source_service_id = __EMPTY_UUID__::uuid
                     OR i.source_service_id = v_local_service_id
                THEN __EMPTY_UUID__::uuid ELSE i.source_service_id END AS origin,
           NULLIF(i.source_commit_sequence, 0) AS origin_seq
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
      )
  ),
  -- The origin-order key: a positioned event's own (position, id); for an unpositioned event, the (position, id) of the
  -- positioned event it goes just before (#2379), and none when it goes after them all.
  keyed AS (
    SELECT k.*,
           COALESCE(k.origin_seq, nx.origin_seq) AS origin_key,
           CASE WHEN k.origin_seq IS NOT NULL THEN k.message_id ELSE nx.message_id END AS origin_key_id
    FROM cand k
    LEFT JOIN LATERAL (
      SELECT p.origin_seq, p.message_id FROM cand p
      WHERE p.stream_id = k.stream_id AND p.origin = k.origin AND p.origin_seq IS NOT NULL
        AND p.message_id > k.message_id
      ORDER BY p.origin_seq, p.message_id
      LIMIT 1
    ) AS nx ON k.origin_seq IS NULL
  ),
  -- The event's rank in its origin's order: by the key, an unpositioned event just before the positioned one whose key it
  -- took, the id breaking what is left (the replay's tie-break).
  ranked AS (
    SELECT r.*,
           row_number() OVER (PARTITION BY r.stream_id, r.origin
                              ORDER BY r.origin_key NULLS LAST, r.origin_key_id, (r.origin_seq IS NOT NULL), r.message_id)
             AS origin_rank
    FROM keyed r
  ),
  -- The arrival slots, and each slot's rank among its origin's slots.
  slotted AS (
    SELECT s.*,
           row_number() OVER (PARTITION BY s.stream_id ORDER BY s.received_at, s.origin, s.origin_rank) AS slot
    FROM ranked s
  ),
  -- The event of origin rank n takes its origin's n-th slot. Each side is numbered over the whole batch in (stream,
  -- origin, rank) order, so the n-th event and the n-th slot of an origin share one number and the two meet on a single
  -- key: a join on (stream, origin) with the rank as a filter compared every pair of an origin's rows.
  events AS (
    SELECT e.message_id, e.stream_id, e.flags, e.message_type,
           row_number() OVER (ORDER BY e.stream_id, e.origin, e.origin_rank) AS pair
    FROM slotted e
  ),
  slots AS (
    SELECT p.slot, row_number() OVER (ORDER BY p.stream_id, p.origin, p.slot) AS pair
    FROM slotted p
  )
  SELECT array_agg(e.message_id ORDER BY e.stream_id, place.slot), array_agg(e.stream_id ORDER BY e.stream_id, place.slot),
         array_agg(e.flags ORDER BY e.stream_id, place.slot), array_agg(e.message_type ORDER BY e.stream_id, place.slot)
    INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
  FROM events e
  JOIN slots place ON place.pair = e.pair;
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
  -- 537 (#2323): the trim gate, read once for the whole batch, beside the settings.
  v_trim := __SCHEMA__._wh_metadata_trim_on();
  -- 720 (#2625): the packing gate, read once for the whole batch, beside the trim gate.
  v_pack := __SCHEMA__._wh_vector_packing_on();

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   - are events (is_event = true)
  --   - have a stream_id
  --   - aren't yet in wh_event_store (idempotent, ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- 574 (#2551): versions follow the candidates' receive order (their position in v_candidate_ids), never the id.
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
      -- 574 (#2551): the event's place in receive order, and its receive-order key (arrival, origin, origin position;
      -- an unpositioned event sorts after its origin's positioned events of the same arrival, then by id, the one
      -- fallback where nothing positional exists), which the hold-back below compares with a row this pass does not hold.
      cand.position AS receive_position,
      CASE WHEN i.source_service_id IS NULL OR i.source_service_id = __EMPTY_UUID__::uuid
                OR i.source_service_id = v_local_service_id
           THEN __EMPTY_UUID__::uuid ELSE i.source_service_id END AS receive_origin,
      COALESCE(NULLIF(i.source_commit_sequence, 0), 9223372036854775807) AS receive_seq
    -- 162: this pass needs the message body, so both tables. The LEASE table drives the join, so
    -- selection happens on the narrow table and the wide row is fetched only for the rows that
    -- survive it, which is the opposite of scanning the wide row to apply the same predicate.
    -- 574: a row the candidates do not list (committed by a concurrent claim after they were read) is left for the
    -- next pass, unstamped, as a row that pass would have missed.
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    JOIN unnest(v_candidate_ids) WITH ORDINALITY AS cand(message_id, position) ON cand.message_id = ist.message_id
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
  -- 230 (#1607): a received stream is stored in its receive order, however many claims carry it. A claim numbers
  -- the rows it holds after the stream's tail, so a claim that holds a stream's later events and reaches this pass
  -- first would number them ahead of the head another claim leased earlier and has not stored yet. The head is visible
  -- here as a pending event of the stream that this pass does not hold: not yet in the event store, and not leased to
  -- this instance under a live lease (a peer's lease, an expired one, none, or a lease a concurrent claim has not
  -- committed). A row of this batch waits when such an event comes before it, and so does every row after it in the
  -- batch's receive order: it is not stored and not stamped, so the next claim re-reads it, and fetch_inbox_batch does
  -- not hand it to the dispatcher before it is stored. Rows before it are stored now, so the head never waits.
  --
  -- 574 (#2551): "comes before" is the receive-order key (arrival, origin, origin position, then the id for an
  -- unpositioned event), never the id alone. The key is a strict total order, so two claims of one stream can never
  -- each wait for the other. An event of another origin that arrived after this row is never its head: between origins
  -- the arrival order stands.
  --
  -- One probe per stream, over idx_inbox_state_stream_order (stream_id, received_at, message_id), bounded by the
  -- batch: only rows received no later than the batch's newest row can hold it back. In a stream's FIFO backlog that
  -- range is the batch and the rows before it, never the rows queued behind it, and the lease test is answered from the
  -- index, so only a row this pass does not hold reads the heap (and its message row, for the origin). No index is
  -- added: wh_inbox_state's index count is a gated design property (InboxStateWriteCostScenarioTests).
  batch_streams AS (
    SELECT ie.stream_id, MAX(ie.received_at) AS newest_received_at
    FROM inbox_events ie
    GROUP BY ie.stream_id
  ),
  held_back_from AS (
    SELECT s.stream_id, h.received_at, h.origin, h.seq, h.message_id
    FROM batch_streams s
    CROSS JOIN LATERAL (
      SELECT b.received_at,
             CASE WHEN bi.source_service_id IS NULL OR bi.source_service_id = __EMPTY_UUID__::uuid
                       OR bi.source_service_id = v_local_service_id
                  THEN __EMPTY_UUID__::uuid ELSE bi.source_service_id END AS origin,
             COALESCE(NULLIF(bi.source_commit_sequence, 0), 9223372036854775807) AS seq,
             b.message_id
      FROM __SCHEMA__.wh_inbox_state b
      JOIN __SCHEMA__.wh_inbox bi ON bi.message_id = b.message_id
      WHERE b.stream_id = s.stream_id
        AND b.processed_at IS NULL
        AND b.received_at <= s.newest_received_at
        AND NOT COALESCE(b.instance_id = p_instance_id AND b.lease_expiry > p_now, false)
        AND b.is_event = true
        AND b.chain_emitted_at IS NULL
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = b.message_id
        )
      ORDER BY b.received_at, 2, 3, b.message_id
      LIMIT 1
    ) AS h
  ),
  -- Per stream, the first row (in receive order) that the unheld head comes before; it and every row after it wait.
  first_waiting AS (
    SELECT ie.stream_id, MIN(ie.receive_position) AS position
    FROM inbox_events ie
    JOIN held_back_from h ON h.stream_id = ie.stream_id
    WHERE (h.received_at, h.origin, h.seq, h.message_id) < (ie.received_at, ie.receive_origin, ie.receive_seq, ie.message_id)
    GROUP BY ie.stream_id
  ),
  storable_events AS (
    SELECT ie.*,
           ROW_NUMBER() OVER (PARTITION BY ie.stream_id ORDER BY ie.receive_position) AS row_num
    FROM inbox_events ie
    LEFT JOIN first_waiting w ON w.stream_id = ie.stream_id
    WHERE w.position IS NULL OR ie.receive_position < w.position
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
      -- 720 (#2625): once the packing gate is open, the stored body holds the vectors of a type with registered paths
      -- packed binary32. The wire form (ie.event_data) is unchanged. An event of a type with no paths runs nothing new.
      CASE WHEN vp.paths IS NULL THEN COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload')
           ELSE __SCHEMA__._wh_pack_vectors(COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload'), vp.paths)
      END AS body_data,
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
    -- 720 (#2625): the type's vector paths, read only while the gate is open (a one-time filter: closed, no scan).
    LEFT JOIN LATERAL (
      SELECT p.paths FROM __SCHEMA__.wh_event_vector_paths p
      WHERE v_pack AND p.event_type = __SCHEMA__.normalize_event_type(ie.message_type)
    ) vp ON true
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
        -- 537 (#2323): once the trim gate is open, less what the row holds in its own columns, its event id and scope.
        WHEN v_trim THEN __SCHEMA__._wh_trim_row_values(
               __SCHEMA__._wh_slim_event_metadata(cr.full_meta, cr.route_id), cr.message_id, cr.scope::jsonb)
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
      array_agg(c.stream_id),
      -- 461 (#2364): a superseding copy counts as its key's slot, once: only when the store holds no copy of its key yet,
      -- and only the first such copy of this batch. The slot is passed already: the rows this statement stores are not
      -- visible to it, so the helper could not read them back.
      array_agg(CASE WHEN c.supersede_key IS NULL THEN c.message_id
                     ELSE __SCHEMA__._wh_slot_id(c.stream_id, COALESCE(c.supersede_group, c.event_type), c.supersede_key) END),
      'fold', p_now, TRUE) AS folded
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE __DIGEST_ELIGIBLE__(c)
      AND (c.supersede_key IS NULL
           OR (NOT EXISTS (
                 SELECT 1 FROM __SCHEMA__.wh_event_store o
                 WHERE o.stream_id = c.stream_id
                   AND COALESCE(o.supersede_group, o.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND o.supersede_key = c.supersede_key)
               AND NOT EXISTS (
                 SELECT 1 FROM computed c2
                 JOIN stored_events se2 ON se2.event_id = c2.message_id
                 WHERE c2.stream_id = c.stream_id
                   AND COALESCE(c2.supersede_group, c2.event_type) = COALESCE(c.supersede_group, c.event_type)
                   AND c2.supersede_key = c.supersede_key
                   AND se2.version < se.version)))
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
  SET chain_emitted_at = p_now,
      -- 551 (#1630): a row that waited for its stream's head and is now stored no longer waits.
      failure_reason = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN c_reason_unknown
                            ELSE ist.failure_reason END,
      error = CASE WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN NULL ELSE ist.error END
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
  -- 551 (#1630): and shows that it waits for its stream's head (MessageFailureReason.AwaitingStreamHead), so a waiting row
  -- is told apart from a fresh one and from a failing one. The error names the stream and when the wait began; a row
  -- already waiting keeps the time it began, however many claims re-lease it. The state replaces the claim's lapsed-lease
  -- record (an unstored row was never dispatched, so a lease that ran out on it was a wait, not a dispatch cut off), and
  -- nothing else a row recorded: that failure is the better diagnostic. Once a row shows it waits, the claim's
  -- lapsed-lease branch (guarded on error IS NULL) leaves it as it is. The stamps above clear the state when the row is
  -- stored. The same statement as the refund, on the same rows: no extra read.
  SET attempts = GREATEST(ist.attempts - 1, 0),
      failure_reason = CASE
        WHEN ist.error IS NULL OR ist.failure_reason IN (c_reason_lease_expired, c_reason_awaiting_stream_head)
        THEN c_reason_awaiting_stream_head
        ELSE ist.failure_reason
      END,
      error = CASE
        WHEN ist.failure_reason = c_reason_awaiting_stream_head THEN ist.error
        WHEN ist.error IS NULL OR ist.failure_reason = c_reason_lease_expired
        THEN 'Waiting for the stream head since ' || p_now::text || ': an earlier event of stream '
             || ist.stream_id::text || ' is not yet stored and is not held by this instance, so this event is held back,'
             || ' unstored and undispatched, until it is. The wait is not charged an attempt.'
        ELSE ist.error
      END
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry = p_lease_expiry
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
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
    -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
    -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
    CASE WHEN p_lease_expiry IS NULL THEN NULL
         WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
         ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
    CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
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
      -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
      AND NOT v_map
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- 100: reconcile self-declares the rewind. Any of THIS invocation's fresh work items that
  -- slots BELOW its cursor's last-applied event is a straggler by construction: its fresh local
  -- commit_sequence sits above the cursor and hides it from the runtime inversion detector forever.
  -- Flag the cursor RewindRequired with the earliest straggler as trigger (min-merge with any
  -- existing trigger); the worker's existing rewind routing replays the stream through the
  -- corrected order.
  -- 574 (#2551): "below the cursor" is the replay's order, never the id. A stored event is appended after everything
  -- the cursor applied, and a replay moves it only within its own origin's slots (#2257), so it is a straggler exactly
  -- when its origin had already delivered a later event the cursor applied: the origin's latest event at or below the
  -- cursor's version comes after it in the origin's order (origin sequence; when either carries none, the id, the
  -- replay's rule for an unpositioned event, #2379). A local event, or another origin's, keeps its place and is never
  -- one. The earliest straggler, and the earlier of it and an existing trigger, are by stream version; the worker judges
  -- the replay floor from the trigger in the replay's order (ReplayFloor).
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN s.straggler_event_id
        WHEN s.straggler_version < COALESCE(
               (SELECT t.version FROM __SCHEMA__.wh_event_store t WHERE t.event_id = pc.rewind_trigger_event_id),
               9223372036854775807)
          THEN s.straggler_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
  FROM (
    SELECT pe.stream_id, pe.perspective_name,
           (array_agg(pe.event_id ORDER BY se.version))[1] AS straggler_event_id,
           MIN(se.version) AS straggler_version
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_perspective_cursors c
      ON c.stream_id = pe.stream_id AND c.perspective_name = pe.perspective_name
    JOIN __SCHEMA__.wh_event_store se ON se.event_id = pe.event_id
    -- The cursor's position: the stream version of its last applied event (a bound on the stream, never a judgment of
    -- which copy of a superseding key is newer, which _wh_supersedes alone decides).
    CROSS JOIN LATERAL (
      SELECT ce.version AS through_version FROM __SCHEMA__.wh_event_store ce WHERE ce.event_id = c.last_event_id
    ) AS cursor_at
    CROSS JOIN LATERAL (
      -- The origin's latest event at or below the cursor's version: one backward probe of idx_event_store_stream.
      SELECT a.event_id, a.origin_commit_sequence
      FROM __SCHEMA__.wh_event_store a
      WHERE a.stream_id = se.stream_id
        AND a.origin_service_id = se.origin_service_id
        AND a.version <= cursor_at.through_version
      ORDER BY a.version DESC
      LIMIT 1
    ) AS applied
    WHERE pe.event_id = ANY(v_stored_event_ids)
      AND pe.processed_at IS NULL
      AND c.last_event_id IS NOT NULL
      AND se.origin_service_id IS NOT NULL
      AND CASE WHEN se.origin_commit_sequence IS NOT NULL AND applied.origin_commit_sequence IS NOT NULL
               THEN se.origin_commit_sequence < applied.origin_commit_sequence
               ELSE se.event_id < applied.event_id
          END
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
      -- 567 (#2555): in map mode the row is leased to the committing instance only when it owns the stream's
      -- partition; otherwise it is left unleased for the partition's owner to claim. Never to a stored owner.
      CASE WHEN p_lease_expiry IS NULL THEN NULL
           WHEN v_map THEN CASE WHEN __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN p_instance_id END
           ELSE COALESCE(owner.assigned_instance_id, p_instance_id) END,
      CASE WHEN v_map AND NOT __SCHEMA__.wh_i_own(__SCHEMA__.compute_partition(es.stream_id, p_partition_count), p_instance_id) IS NOT NULL THEN NULL ELSE p_lease_expiry END,
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
        -- 567: the stored owner decides nothing in map mode, so the ledger is not read.
        AND NOT v_map
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
  '551 (#1630): a row this pass held back shows it waits for its stream''s head (failure_reason 30, AwaitingStreamHead, and an error naming the stream and when the wait began), replacing only a lapsed-lease record, and the stamp clears it once the row is stored. 411 (#2120): a row the calling claim leased (lease_expiry = p_lease_expiry) that this pass held back is refunded the attempt the claim counted. Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each received event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each received event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 367: a pass whose lock pass finds no candidate only stamps, and the collective routing runs only when a stored event is collective. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145). 537 (#2323): the slim row leaves out its message id when it is the event id, and its hop scope when it is the scope column alone. 567 (#2555): with the service''s switch on, no stored owner is pinned or leased to; leased by partition (wh_i_own). 574 (#2551): versions a received stream in receive order (each origin by its origin position, dealt into its arrival slots; the id only for an event with no origin position, as the replay places it), holds a batch back behind an unheld head by that order, and declares a straggler by the origin''s order, never by id. 720 (#2625): once the packing gate is open, the stored body of a type with registered vector paths holds them packed binary32 (_wh_pack_vectors); the wire form is unchanged.';

-- ── wh_append_event: last word 537, copied whole, its stored body packed under the gate (720) ──
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');
-- <docs>data/event-store#packed-vectors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WritePath_StoresJsonNumbersWhileTheGateIsClosed_AndPackedVectorsOnceItIsOpenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorStorageSqlTests.cs:WritePath_AnEventOfATypeWithNoRegisteredPath_IsStoredAsWrittenAsync</tests>
-- <docs>fundamentals/events/superseding-events#superseding-groups</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:DirectAppend_StampsTheGroup_AndQueuesAcrossTypesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSupersedingKeyTests.cs:AppendAsync_GroupedTypes_SupersedeEachOther_AndTheOlderIsReapedAsync</tests>
-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_ThroughTheEventStore_CreatesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EphemeralAndCompactionOriginEvents_CreateNoRow_WhileASourcedEventDoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EventsWithoutAStream_CreateNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ParallelAppendsToOneStream_AllSucceed_WithVersionsZeroToNMinusOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:MixedPathStream_TableDigestEqualsARecomputationOverAllItsEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendMixedStreamGuardTests.cs:DirectAppend_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_DirectAppend_SealsTheStream_AndTheNextAppendThrowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:DirectAppend_ToASealedStream_ThrowsStreamSealedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs:AppendAsync_TerminalEvent_SealsTheStream_AndTheNextAppendThrowsStreamSealedExceptionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationTests.cs:EnsureWhizbangDatabaseInitialized_ADatabaseAlreadyPast267_StillApplies267_AndANewStreamStartsAtOneAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/superseding-events#the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:DirectAppend_StampsTheKey_AndQueuesTheOlderOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:DirectAppend_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:DirectAppend_AutoRestore_RaisesTheRestoreState_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveAppendPolicyTests.cs:AppendAsync_ToAnArchivedStream_Refuse_ThrowsStreamArchivedExceptionAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DirectAppend_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:EFCoreEventStore_Append_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DirectAppend_KeysTheSlimFormDoesNotReduce_AreKeptAsWrittenAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AppendAsync_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_EveryParkedCursorHasItsSnapshot_StoresAtTheNextVersion_AndTheArchiveBecomesAPrefixAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_AParkedCursorWithoutASnapshotAtIt_FallsBackToAutoRestoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_SealedAndArchived_TheSealStillRefusesAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamDispositionTests.cs:AppendAsync_NewGeneration_ContinuesAtTheNextVersion_AndTheCoordinatorResumesFromTheParkedCursorAsync</tests>
-- <docs>resilience/stream-integrity#one-digest-fold</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestWritersTests.cs:DirectAppend_FoldsEveryLaneAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:DirectAppend_LeavesOutTheMessageIdInEitherDriversForm_AndTheScopeTheColumnHoldsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AppendAsync_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_append_event(
  p_event_id UUID,
  p_stream_id UUID,
  p_aggregate_id UUID,
  p_aggregate_type TEXT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_metadata JSONB,
  p_scope JSONB,
  p_created_at TIMESTAMPTZ,
  -- 228 (#1546): the event's kind bits (EventFlags.Ephemeral, EventFlags.Compacted), stored on the row as the outbox
  -- and inbox copies store them. Defaulted so a caller from before this migration still resolves.
  p_flags INTEGER DEFAULT 0
) RETURNS INTEGER AS $$
DECLARE
  v_version INTEGER;
  v_flags INTEGER;
  v_refusal RECORD;
  v_body_bytes BIGINT;  -- 260: the size of the body row as written, for the lifecycle ledger
  c_flag_ephemeral CONSTANT INTEGER := 8;     -- EventFlags.Ephemeral (1 << 3), as the emit chain tests it
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  c_flag_terminal CONSTANT INTEGER := 64;  -- 266: EventFlags.Terminal (1 << 6)
  c_flag_superseding CONSTANT INTEGER := 128;  -- 295: EventFlags.Superseding (1 << 7)
  v_key TEXT;                              -- 295: the superseding key this event is stored with
  v_group TEXT;                            -- 352: the superseding group it is stored with (#2124)
  v_ends_stream BOOLEAN;                   -- 266: this event seals its stream
  v_closed RECORD;                         -- 271: the stream's row when it is sealed or archived
  v_archived_refusal RECORD;               -- 296: the archived stream's refusal, when its policy refuses
  v_new_generation BOOLEAN := false;       -- 296: this append starts the stream's new generation
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
  v_full JSONB;                            -- 285: the metadata as the caller wrote it
  v_route_id UUID;                         -- 285: the event's route id
  v_body_meta JSONB;                       -- 285: the metadata its row keeps
  v_paths JSONB;                           -- 720: the event type's vector paths, while the packing gate is open
BEGIN
  -- The emit chains' key for this stream (214), so a direct append and a chain emit serialize too.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  -- 266 (#1733): a sealed stream refuses every append. The caller awaits the append, so the refusal is raised (SQLSTATE
  -- WHS01, which both drivers surface as StreamSealedException) and nothing is stored.
  -- 271 (#1738): so does an archived stream (its tombstone holds the versions an append here would reuse), raised as
  -- SQLSTATE WHA01 under its type's Refuse policy or WHA02 under AutoRestore, which tells the caller to restore the
  -- stream and retry once (both drivers surface either as StreamArchivedException). One probe of
  -- idx_stream_lifecycle_closed, which holds only the sealed and the archived streams, answers both, under the lock above.
  SELECT l.sealed_at, l.sealed_reason, l.state, l.last_version, l.stream_type INTO v_closed
  FROM __SCHEMA__.wh_stream_lifecycle l
  WHERE l.stream_id = p_stream_id
    AND (l.sealed_at IS NOT NULL OR l.state = 'Archived');
  IF FOUND THEN
    IF v_closed.sealed_at IS NOT NULL THEN
      RAISE EXCEPTION USING
        ERRCODE = 'WHS01',
        MESSAGE = __SCHEMA__._wh_stream_sealed_refusal_text(p_stream_id, v_closed.sealed_at, v_closed.sealed_reason),
        HINT = 'A sealed stream has ended; append to a new stream instead.';
    END IF;
    -- 296 (#1734): the policy as every write path reads it. NewGeneration with a snapshot at every parked cursor is no
    -- refusal: the stream starts its new generation below; without one it reads AutoRestore.
    SELECT r.append_policy, r.refusal_text INTO v_archived_refusal
    FROM __SCHEMA__._wh_archived_stream_refusals(ARRAY[p_event_id], ARRAY[p_stream_id]) r
    LIMIT 1;
    IF FOUND THEN
      RAISE EXCEPTION USING
        ERRCODE = CASE WHEN v_archived_refusal.append_policy = 'AutoRestore' THEN 'WHA02' ELSE 'WHA01' END,
        MESSAGE = v_archived_refusal.refusal_text,
        HINT = 'Restore the stream (IArchiveStore.RestoreAsync) before appending to it.';
    END IF;
    PERFORM __SCHEMA__._wh_start_new_generations(ARRAY[p_stream_id]);
    v_new_generation := true;
  END IF;

  -- 228 (#1546): an event that would make its stream hold both ephemeral and Sourced events is refused, under the
  -- lock above, before anything is stored. The caller awaits the append, so the refusal is raised (SQLSTATE WH133,
  -- which both drivers surface as MixedStreamEventException). The check only reads.
  SELECT r.refusal_text, r.refusal_detail INTO v_refusal
  FROM __SCHEMA__._wh_mixed_stream_refusals(
    ARRAY[p_event_id], ARRAY[p_stream_id], ARRAY[COALESCE(p_flags, 0)], ARRAY[p_event_type]) r
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION USING
      ERRCODE = 'WH133',
      MESSAGE = v_refusal.refusal_text,
      DETAIL = v_refusal.refusal_detail::TEXT,
      HINT = 'Publish the ephemeral events on their own stream.';
  END IF;

  -- 285 (#1725): the row keeps the slim form unless the event is kept full, and carries its route id; under the Linked
  -- form the metadata as written is staged. The two drivers write different forms (EnvelopeMetadata, Hops; the Dapper
  -- store's, hops), and the slim form reduces whichever hop list is there and keeps every other key as written.
  SELECT s.write_form, s.full_for, s.keep_all INTO v_write_form, v_full_for, v_keep_all
  FROM __SCHEMA__._wh_metadata_write_settings() s;
  v_full := COALESCE(p_metadata, '{}'::jsonb);
  v_route_id := __SCHEMA__._wh_route_id(split_part(p_event_type, ',', 1), COALESCE(v_full -> 'Hops', v_full -> 'hops'));
  v_body_meta := CASE
    WHEN v_keep_all OR __SCHEMA__._wh_metadata_kept_full(split_part(p_event_type, ',', 1), v_full_for)
    THEN v_full || jsonb_build_object('rid', v_route_id)
    -- 537 (#2323): once the trim gate is open, less what the row holds in its own columns, its event id and scope.
    WHEN __SCHEMA__._wh_metadata_trim_on()
    THEN __SCHEMA__._wh_trim_row_values(__SCHEMA__._wh_slim_event_metadata(v_full, v_route_id), p_event_id, p_scope)
    ELSE __SCHEMA__._wh_slim_event_metadata(v_full, v_route_id)
  END;

  -- 295 (#1729): a superseding event's key, from the metadata the driver stamped it in, on a row carrying the flag only.
  v_key := CASE WHEN COALESCE(p_flags, 0) & c_flag_superseding <> 0 THEN p_metadata ->> 'sk' END;
  -- 352 (#2124): and its superseding group, beside the key.
  v_group := CASE WHEN COALESCE(p_flags, 0) & c_flag_superseding <> 0 THEN p_metadata ->> 'sg' END;

  -- A separate statement: its snapshot is taken after the lock was granted.
  INSERT INTO __SCHEMA__.wh_event_store (
    event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at, flags, supersede_key,
    supersede_group
  )
  SELECT
    p_event_id,
    p_stream_id,
    p_aggregate_id,
    p_aggregate_type,
    p_event_type,
    p_scope,
    -- 296 (#1734): a new generation continues from its ledger row's last version; no other append reads the ledger here.
    COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id),
             CASE WHEN v_new_generation THEN
               (SELECT l.last_version FROM __SCHEMA__.wh_stream_lifecycle l WHERE l.stream_id = p_stream_id)
             END,
             0) + 1,
    p_created_at,
    COALESCE(p_flags, 0),
    v_key,
    v_group
  RETURNING version, flags INTO v_version, v_flags;

  -- 295 (#1729): queue the older events of this key for the reaper.
  IF v_key IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_queue_superseded_events(ARRAY[p_event_id], p_created_at);
  END IF;

  -- The body half of the pointer/body split (077). Constraint-less ON CONFLICT, as the emit chain writes it:
  -- event_id is the table's only key.
  -- 720 (#2625): once the packing gate is open, the stored body holds the vectors of a type with registered paths packed
  -- binary32. One primary-key probe of the paths only while the gate is open.
  IF __SCHEMA__._wh_vector_packing_on() THEN
    SELECT p.paths INTO v_paths FROM __SCHEMA__.wh_event_vector_paths p WHERE p.event_type = p_event_type;
    IF v_paths IS NOT NULL THEN
      p_event_data := __SCHEMA__._wh_pack_vectors(p_event_data, v_paths);
    END IF;
  END IF;

  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  VALUES (p_event_id, p_event_data, v_body_meta)
  ON CONFLICT DO NOTHING
  -- 260: measured on the row the insert formed, as the emit chains measure theirs (the values as the row holds them,
  -- before any TOAST compression), so the two paths count a body the same way.
  RETURNING pg_column_size(event_data) + pg_column_size(metadata) INTO v_body_bytes;

  IF v_write_form = 'Linked' THEN
    INSERT INTO __SCHEMA__.wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
    VALUES (p_event_id, p_created_at, v_route_id, split_part(p_event_type, ',', 1), v_full);
  END IF;

  -- 225 (#1600): fold the event into its stream digest bucket, as the emit chain's digest_folds does, still under
  -- the stream lock taken above. One event, so one row: the chain's bit_xor and COUNT(*) over a single event are
  -- its two hashes and 1.
  IF EXISTS (SELECT 1 FROM (SELECT v_flags AS flags, p_event_type AS event_type) ev WHERE __DIGEST_ELIGIBLE__(ev)) THEN
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      ARRAY[__EMPTY_UUID__::uuid], ARRAY[COALESCE(p_scope ->> 't', '')], ARRAY[p_event_type], ARRAY[p_stream_id],
      ARRAY[p_event_id], 'fold', p_created_at);
  END IF;

  -- 260 (#1732): the stream lifecycle ledger, as the emit chains keep it, still under the stream lock taken above. The
  -- same events count (not ephemeral, not a compaction origin, on a stream), and the same columns move: an append never
  -- writes an indexed column, so the update is heap-only.
  -- 266 (#1733): a terminal event seals the stream in this transaction; any other append writes the sealing columns
  -- back unchanged, which keeps the update heap-only.
  IF COALESCE(v_flags, 0) & c_flags_off_the_ledger = 0 AND p_stream_id <> __EMPTY_UUID__::uuid THEN
    v_ends_stream := COALESCE(v_flags, 0) & c_flag_terminal <> 0;
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes,
       state, sealed_at, sealed_reason)
    VALUES (
      p_stream_id,
      p_aggregate_type,
      p_scope ->> 't',
      p_created_at,
      date_trunc('hour', p_created_at, 'UTC'),
      v_version,
      COALESCE(v_body_bytes, 0),
      CASE WHEN v_ends_stream THEN 'Sealed' ELSE 'Active' END,
      CASE WHEN v_ends_stream THEN p_created_at END,
      CASE WHEN v_ends_stream THEN __SCHEMA__._wh_terminal_seal_reason(split_part(p_event_type, ',', 1), p_event_id) END
    )
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
      state = CASE WHEN EXCLUDED.sealed_at IS NOT NULL AND l.sealed_at IS NULL AND l.state = 'Active'
                   THEN 'Sealed' ELSE l.state END,
      sealed_reason = CASE WHEN l.sealed_at IS NULL THEN EXCLUDED.sealed_reason ELSE l.sealed_reason END,
      sealed_at = COALESCE(l.sealed_at, EXCLUDED.sealed_at);
  END IF;

  RETURN v_version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_append_event(UUID, UUID, UUID, TEXT, TEXT, JSONB, JSONB, JSONB, TIMESTAMPTZ, INTEGER) IS
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), 0) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555). 225: folds the stored event into wh_stream_digests as the emit chain does, unless it is ephemeral, at-most-once or of a digest-exempt type (#1600). 228: stores the event''s kind bits (p_flags) and refuses, raising SQLSTATE WH133, an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, #1546). 260: keeps the stream''s wh_stream_lifecycle row (#1732). 266: refuses an append to a sealed stream, raising SQLSTATE WHS01 (StreamSealedException), and a terminal event seals its stream (#1733). 267: a new stream starts at version 1, as on the outbox and inbox paths; an existing stream continues from its highest version (#1913). 271: refuses an append to an archived stream, raising SQLSTATE WHA01 (Refuse) or WHA02 (AutoRestore) by its type''s AppendToArchived policy (StreamArchivedException, #1738). 285: the row keeps the slim form unless kept full, carries its route id, and under the Linked form the metadata as written is staged (#1725). 295: stamps a superseding event''s key and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: stamps the superseding group beside the key (#2124). 537: the slim row leaves out its message id when it is the event id, and its hop scope when it is the scope column alone (#2323). 720 (#2625): once the packing gate is open, the stored body of a type with registered vector paths holds them packed binary32 (_wh_pack_vectors); the wire form is unchanged.';
