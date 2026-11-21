-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 537_StoredMetadataLeavesOutWhatTheRowHolds.sql
-- Date: 2026-11-20
-- Description: The slim stored form leaves out what the event row already holds in its own columns, and readers put it
--              back (#2323).
--
--              A slim row (285) still repeated two values wh_event_store holds for it: the message id, which is the
--              row's event_id, and the slim hop's scope, which is the row's scope column. Once the trim gate is open
--              (below), every write path (both emit chains and wh_append_event) stores the slim form less those two,
--              through _wh_trim_row_values:
--
--                the message id   left out when it equals the row's event id, under the key the writer used
--                                 (MessageId, or message_id in the Dapper store's form); a message id that differs
--                                 from the event id is not the row's, and is kept
--                the hop scope    left out when the slim form has one hop whose scope is the scope value alone
--                                 ({"v":{"Sc":...}}), that value names something a reader restores (an id, an
--                                 extension, or the system or declared-unscoped marker), and it equals the scope
--                                 column (_wh_canonical_scope: the column's written form carries empty lists and null
--                                 values the hop's does not). A hop scope carrying roles, claims, principals or any
--                                 other part holds more than the column, and is kept whole
--
--              Every reader already restored a row's scope onto a first hop that carries none (#1855), so a trimmed scope
--              reads back as it was written, on this release and on the ones before it. The message id is read from the
--              row's event id when the metadata has none (EnvelopeMetadata.MessageIdOf, the Dapper store's readers), which
--              a library from before this release cannot do. So the write paths trim only once every live instance can:
--
--                the marker     each heartbeat of this release writes stored_form_level (HeartbeatRequest
--                               .STORED_FORM_LEVEL, 1) into its registry row's metadata; an earlier release writes
--                               none, which reads as "cannot read trimmed rows"
--                the gate       wh_open_metadata_trim_gate, called by the metadata-trim-gate maintenance step, latches
--                               wh_settings 'metadata_trim_row_values' = 'true' once at least one instance is live (the
--                               one liveness rule, wh_live_instance_ids) and every live instance has level 1 or higher;
--                               an empty registry keeps it closed, so a new service opens it once its own first
--                               heartbeat is in
--                the latch      one-way: nothing clears it, so an instance of an earlier release that starts after it
--                               cannot read the trimmed rows; rolling back below this release then is not supported
--                the write path reads the one flag once per call (_wh_metadata_trim_on), a primary-key probe beside the
--                               settings read it already makes; no fleet scan
--
--              Rows the trim does not reach: full rows (debug mode, the Full form, a type kept full) are the metadata as
--              written and keep everything; the staged full form is unchanged.
--
--              History: wh_convert_event_metadata_history follows the same gate. While it is closed it converts a row
--              written before 285 to the slim form as before; once it is open it converts such a row to the trimmed slim
--              form and also trims a row already in the slim form (_wh_metadata_needs_conversion), without counting its
--              route again. Opening the gate starts its walk over (the cursor is reset in the statement that latches),
--              so the slim rows written before are trimmed; rows already trimmed are passed over.
--
--              Reconstruction: wh_trace_metadata puts the message id and the hop scope back (_wh_restore_row_values), so
--              its answer is the metadata as before.
--
--              Integrity: no digest, epoch, fingerprint or route id reads a row's metadata (the digests hash event ids,
--              the route id is computed from the full hops before the slim form is taken), so no digest changes.
--
--              Number 537: reserved for this change.
--
-- Reversal: lossy (the functions go back to their last words; rows already trimmed stay trimmed, and are readable only
--           by a library at or after this release; the latch stays)
-- Dependencies: 285 (_wh_metadata_write_settings, _wh_metadata_kept_full, _wh_route_id, _wh_slim_event_metadata),
--               286 (_wh_reconstruct_metadata, _wh_try_uuid, wh_routes, wh_traces), 298 (wh_metadata_history_conversion),
--               453 (wh_append_event), 461 (the emit chains)
-- Objects: _wh_canonical_scope, _wh_scope_names_something, _wh_trim_row_values, _wh_restore_row_values,
--          _wh_is_slim_event_metadata, _wh_metadata_needs_conversion, _wh_metadata_trim_on, wh_open_metadata_trim_gate,
--          _emit_event_store_chain,
--          _emit_event_store_chain_for_inbox, wh_append_event, wh_convert_event_metadata_history, wh_trace_metadata
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_canonical_scope');
SELECT __SCHEMA__.drop_all_overloads('_wh_scope_names_something');
SELECT __SCHEMA__.drop_all_overloads('_wh_trim_row_values');
SELECT __SCHEMA__.drop_all_overloads('_wh_restore_row_values');
SELECT __SCHEMA__.drop_all_overloads('_wh_is_slim_event_metadata');
SELECT __SCHEMA__.drop_all_overloads('_wh_metadata_needs_conversion');
SELECT __SCHEMA__.drop_all_overloads('_wh_metadata_trim_on');
SELECT __SCHEMA__.drop_all_overloads('wh_open_metadata_trim_gate');

-- ===========================================================================================
-- The row's values
-- ===========================================================================================

-- A stored scope in one written form: the hop's (ScopeDelta's) form, which leaves out null ids, empty allowed-principal
-- and extension lists and false markers, where the scope column's (PerspectiveScope's) form writes them. NULL for a
-- value that is not an object.
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_AScopeColumnInItsWrittenForm_MatchesTheHopScopeItEqualsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_canonical_scope(p_scope JSONB)
RETURNS JSONB AS $$
  SELECT CASE WHEN jsonb_typeof(p_scope) = 'object' THEN
    jsonb_strip_nulls(p_scope) - array_remove(ARRAY[
      CASE WHEN p_scope -> 'ap' = '[]'::jsonb THEN 'ap' END,
      CASE WHEN p_scope -> 'ex' = '[]'::jsonb THEN 'ex' END,
      CASE WHEN p_scope -> 'sys' = 'false'::jsonb THEN 'sys' END,
      CASE WHEN p_scope -> 'dec' = 'false'::jsonb THEN 'dec' END], NULL)
  END;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_canonical_scope(JSONB) IS
  'Issue #2323: a stored scope in the hop''s written form (null values, empty ap and ex lists and false sys and dec '
  'markers left out), so a scope column and a hop scope compare as values. NULL for a value that is not an object.';

-- Whether a stored scope names something a reader restores from the scope column (ScopeDelta.NamesNothing negated): an
-- id, an extension, or the system or declared-unscoped marker. A scope naming nothing is restored as no scope at all.
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_KeepsWhatTheRowDoesNotHoldAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_scope_names_something(p_scope JSONB)
RETURNS BOOLEAN AS $$
  SELECT jsonb_typeof(p_scope) = 'object' AND (
    COALESCE(p_scope ->> 't', '') <> '' OR COALESCE(p_scope ->> 'u', '') <> ''
    OR COALESCE(p_scope ->> 'c', '') <> '' OR COALESCE(p_scope ->> 'o', '') <> ''
    OR (jsonb_typeof(p_scope -> 'ex') = 'array' AND jsonb_array_length(p_scope -> 'ex') > 0)
    OR p_scope -> 'sys' = 'true'::jsonb OR p_scope -> 'dec' = 'true'::jsonb);
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_scope_names_something(JSONB) IS
  'Issue #2323: whether a stored scope names an id, an extension or a marker, the parts a reader restores from the '
  'scope column (ScopeDelta.NamesNothing negated).';

-- The slim form less what the row holds in its own columns: the message id when it equals the row's event id, and the
-- one slim hop's scope when it is the scope value alone, names something, and equals the scope column. Everything else
-- is kept as it is. Plain assignments rather than a query, as _wh_slim_event_metadata is written.
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_LeavesOutTheMessageIdAndTheHopScopeTheRowHolds_AndNothingElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_KeepsWhatTheRowDoesNotHoldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_AScopeColumnInItsWrittenForm_MatchesTheHopScopeItEqualsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:DirectAppend_LeavesOutTheMessageIdInEitherDriversForm_AndTheScopeTheColumnHoldsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_trim_row_values(p_metadata JSONB, p_event_id UUID, p_scope JSONB)
RETURNS JSONB AS $$
DECLARE
  v_out JSONB := p_metadata;
  v_key TEXT;
  v_hop JSONB;
  v_scope JSONB;
BEGIN
  IF jsonb_typeof(p_metadata) IS DISTINCT FROM 'object' THEN
    RETURN p_metadata;
  END IF;
  IF p_event_id IS NOT NULL THEN
    IF __SCHEMA__._wh_try_uuid(v_out ->> __ENVELOPE_FIELD_MESSAGE_ID__) = p_event_id THEN
      v_out := v_out - __ENVELOPE_FIELD_MESSAGE_ID__;
    END IF;
    IF __SCHEMA__._wh_try_uuid(v_out ->> 'message_id') = p_event_id THEN
      v_out := v_out - 'message_id';
    END IF;
  END IF;
  v_key := CASE WHEN v_out ? 'Hops' THEN 'Hops' WHEN v_out ? 'hops' THEN 'hops' END;
  IF v_key IS NOT NULL AND jsonb_typeof(v_out -> v_key) = 'array' AND jsonb_array_length(v_out -> v_key) = 1 THEN
    v_hop := v_out -> v_key -> 0;
    v_scope := v_hop -> 'sc' -> 'v' -> 'Sc';
    IF jsonb_typeof(v_hop) = 'object'
       AND (v_hop -> 'sc') - 'v' = '{}'::jsonb
       AND (v_hop -> 'sc' -> 'v') - 'Sc' = '{}'::jsonb
       AND __SCHEMA__._wh_scope_names_something(v_scope)
       AND __SCHEMA__._wh_canonical_scope(v_scope) = __SCHEMA__._wh_canonical_scope(p_scope) THEN
      v_out := v_out || jsonb_build_object(v_key, jsonb_build_array(v_hop - 'sc'));
    END IF;
  END IF;
  RETURN v_out;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_trim_row_values(JSONB, UUID, JSONB) IS
  'Issue #2323: stored metadata less what its event row holds: the message id (MessageId or message_id) when it equals '
  'the event id, and the one slim hop''s scope when it is the scope value alone, names something and equals the scope '
  'column. Everything else is kept.';

-- The inverse, for an answer that is the metadata as written: the message id from the event id under the key the row's
-- form uses (message_id beside the Dapper store's hops, MessageId otherwise) when the row has none, and the one slim
-- hop's scope from the scope column when the hop has none and the column names something.
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:TraceMetadata_PutsTheMessageIdAndTheScopeBack_FromTheRowAndFromItsTraceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_restore_row_values(p_metadata JSONB, p_event_id UUID, p_scope JSONB)
RETURNS JSONB AS $$
DECLARE
  v_out JSONB := p_metadata;
  v_key TEXT;
  v_hop JSONB;
BEGIN
  IF jsonb_typeof(p_metadata) IS DISTINCT FROM 'object' THEN
    RETURN p_metadata;
  END IF;
  v_key := CASE WHEN v_out ? 'Hops' THEN 'Hops' WHEN v_out ? 'hops' THEN 'hops' END;
  IF p_event_id IS NOT NULL AND NOT (v_out ? __ENVELOPE_FIELD_MESSAGE_ID__) AND NOT (v_out ? 'message_id') THEN
    v_out := v_out || jsonb_build_object(
      CASE WHEN v_key = 'hops' THEN 'message_id' ELSE __ENVELOPE_FIELD_MESSAGE_ID__ END, p_event_id::text);
  END IF;
  IF v_key IS NOT NULL AND jsonb_typeof(v_out -> v_key) = 'array' AND jsonb_array_length(v_out -> v_key) = 1 THEN
    v_hop := v_out -> v_key -> 0;
    IF jsonb_typeof(v_hop) = 'object' AND NOT (v_hop ? 'sc') AND __SCHEMA__._wh_scope_names_something(p_scope) THEN
      v_out := v_out || jsonb_build_object(v_key, jsonb_build_array(
        v_hop || jsonb_build_object('sc', jsonb_build_object('v', jsonb_build_object('Sc', __SCHEMA__._wh_canonical_scope(p_scope))))));
    END IF;
  END IF;
  RETURN v_out;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_restore_row_values(JSONB, UUID, JSONB) IS
  'Issue #2323: stored metadata with what its event row holds put back: the message id from the event id when the row '
  'has none, and the one slim hop''s scope from the scope column when the hop has none.';

-- Whether stored metadata is in the slim form: it carries its route id, no dispatch context or receptor invocations,
-- and at most one hop, which names no service instance. A full row (debug mode, the Full form, a type kept full) has
-- its hops as written, each naming its instance.
-- <docs>data/event-store#history-conversion</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:Convert_AFullRowWithItsRouteId_IsNotTouchedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_is_slim_event_metadata(p_metadata JSONB)
RETURNS BOOLEAN AS $$
  SELECT jsonb_typeof(p_metadata) = 'object' AND p_metadata ? 'rid' AND NOT (p_metadata ? 'dc') AND NOT (p_metadata ? 'rin')
    AND CASE WHEN jsonb_typeof(h.hops) = 'array'
             THEN jsonb_array_length(h.hops) <= 1 AND NOT COALESCE((h.hops -> 0) ?| ARRAY['si', 'ServiceInstance'], false)
             ELSE true END
  FROM (SELECT COALESCE(p_metadata -> 'Hops', p_metadata -> 'hops') AS hops) h;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_is_slim_event_metadata(JSONB) IS
  'Issue #2323: whether stored metadata is in the slim form (route id, no dc or rin, at most one hop naming no service '
  'instance), as opposed to a full row.';

-- Whether history conversion rewrites a row: it was written before 285 (no route id), or, once the trim gate is open
-- (p_trim), it is in the slim form and still holds a value its row's columns hold.
-- <docs>data/event-store#history-conversion</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:Convert_ASlimRowWrittenBeforeTheTrim_IsTrimmed_AndItsRouteIsNotCountedAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:Convert_AFullRowWithItsRouteId_IsNotTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Conversion_WaitsForTheGate_ThenTrimsWhatItWroteAndWhatWasSlimAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_metadata_needs_conversion(p_metadata JSONB, p_event_id UUID, p_scope JSONB, p_trim BOOLEAN)
RETURNS BOOLEAN AS $$
  SELECT jsonb_typeof(p_metadata) = 'object'
    AND (NOT (p_metadata ? 'rid')
         OR (p_trim
             AND __SCHEMA__._wh_is_slim_event_metadata(p_metadata)
             AND __SCHEMA__._wh_trim_row_values(p_metadata, p_event_id, p_scope) IS DISTINCT FROM p_metadata));
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_metadata_needs_conversion(JSONB, UUID, JSONB, BOOLEAN) IS
  'Issue #2323: whether history conversion rewrites a row: no route id (written before 285), or, once the trim gate is '
  'open, slim and still holding a value its row''s columns hold.';

-- ===========================================================================================
-- The trim gate
-- ===========================================================================================

-- Whether the write paths trim: the latch wh_open_metadata_trim_gate writes. One primary-key probe of wh_settings; only
-- the latched value ('true') opens it.
-- <docs>data/event-store#trim-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Writes_BeforeTheGateOpens_AreUntrimmedAndReadByAnEarlierReader_AndAfterItAreTrimmedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Gate_ASettingWrittenByHandOtherThanTrue_IsNotOpenAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_metadata_trim_on()
RETURNS BOOLEAN AS $$
  SELECT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_settings s
    WHERE s.setting_key = 'metadata_trim_row_values' AND s.setting_value = 'true');
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_metadata_trim_on() IS
  'Issue #2323: whether the trim gate is open (wh_settings metadata_trim_row_values = true), read once per call by the '
  'write paths and history conversion.';

-- Opens the trim gate once every live instance reads trimmed rows: at least one instance is live (the one liveness
-- rule), and each live instance's registry row says stored_form_level 1 or higher in its metadata (an earlier release
-- writes none). The latch is one-way: once written it is never cleared, whatever instances come later, and the statement
-- that writes it also starts the history conversion's walk over, so the slim rows written before are trimmed.
-- Returns 'open' (already), 'opened' (this call), or 'closed (n of m live instances read trimmed rows)'.
-- <docs>data/event-store#trim-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Gate_AFreshService_OpensOnlyOnceThisInstanceHasRegisteredWithItsMarkerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Gate_StaysClosedWhileALiveInstanceLacksTheMarker_AndOpensOnceItIsGoneOrStaleAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Gate_OnceOpen_StaysOpen_WhenAnInstanceWithoutTheMarkerAppearsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Conversion_WaitsForTheGate_ThenTrimsWhatItWroteAndWhatWasSlimAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_open_metadata_trim_gate()
RETURNS TEXT AS $$
DECLARE
  v_live BIGINT;
  v_ready BIGINT;
  v_opened BOOLEAN;
BEGIN
  IF __SCHEMA__._wh_metadata_trim_on() THEN
    RETURN 'open';
  END IF;

  SELECT count(*),
         count(*) FILTER (WHERE jsonb_typeof(si.metadata -> 'stored_form_level') = 'number'
                            AND (si.metadata ->> 'stored_form_level')::NUMERIC >= 1)
    INTO v_live, v_ready
  FROM __SCHEMA__.wh_service_instances si
  WHERE si.instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l);

  IF v_live = 0 OR v_ready < v_live THEN
    RETURN 'closed (' || v_ready || ' of ' || v_live || ' live instances read trimmed rows)';
  END IF;

  WITH latched AS (
    INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
    VALUES ('metadata_trim_row_values', 'true', 'boolean',
            'Migration 537 (#2323): every live instance read trimmed stored metadata when this was written, so the write '
            'paths leave out the message id and hop scope a row holds. One-way: never cleared.',
            NOW(), 'wh_open_metadata_trim_gate')
    ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true', updated_at = NOW(), updated_by = EXCLUDED.updated_by
    WHERE __SCHEMA__.wh_settings.setting_value IS DISTINCT FROM 'true'
    RETURNING 1
  ), restarted AS (
    UPDATE __SCHEMA__.wh_metadata_history_conversion
    SET last_event_id = NULL, completed_at = NULL, updated_at = NOW()
    WHERE only_row AND EXISTS (SELECT 1 FROM latched)
    RETURNING 1
  )
  SELECT EXISTS (SELECT 1 FROM latched) INTO v_opened;

  RETURN CASE WHEN v_opened THEN 'opened' ELSE 'open' END;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_open_metadata_trim_gate() IS
  'Issue #2323: latches wh_settings metadata_trim_row_values (one-way) once at least one instance is live and every live '
  'instance''s registry metadata says stored_form_level >= 1, and starts the history conversion''s walk over in the same '
  'statement. Returns open, opened, or closed (n of m live instances read trimmed rows). Called by the metadata-trim-gate '
  'maintenance step.';

-- ===========================================================================================
-- The write paths
-- ===========================================================================================

-- ── _emit_event_store_chain: last word 461, copied whole, its row less what the row holds (537) ──
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
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_LeavesOutTheMessageIdAndTheHopScopeTheRowHolds_AndNothingElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:OutboxChain_KeepsWhatTheRowDoesNotHoldAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:FullRows_KeepEverythingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesReadTests.cs:EveryReader_ReadsATrimmedRow_AsItReadTheSameRowInTheSlimFormAsync</tests>
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
  -- 537 (#2323): the trim gate, read once for the whole batch, beside the settings.
  v_trim := __SCHEMA__._wh_metadata_trim_on();

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
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145). 537 (#2323): the slim row leaves out its message id when it is the event id, and its hop scope when it is the scope column alone.';

-- ── _emit_event_store_chain_for_inbox: last word 461, copied whole, its row less what the row holds (537) ──
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
-- <docs>fundamentals/events/superseding-events#the-digest-counts-the-key</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingCountsTheKeySqlTests.cs:Emit_CopiesOfAKey_AreCountedOnce_AsTheKeysSlotAsync</tests>
-- <docs>data/event-store#row-values</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:InboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
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
  -- 537 (#2323): the trim gate, read once for the whole batch, beside the settings.
  v_trim := __SCHEMA__._wh_metadata_trim_on();

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
  '411 (#2120): a row the calling claim leased (lease_expiry = p_lease_expiry) that this pass held back is refunded the attempt the claim counted. Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each received event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each received event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725). 295: stamps a superseding event''s key (wh_event_store.supersede_key) and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: and its superseding group (wh_event_store.supersede_group, #2124), so the older events of its group, not only its type, are queued. 367: a pass whose lock pass finds no candidate only stamps, and the collective routing runs only when a stored event is collective. 391: a collective event of a type no handler on this service applies gets no __collective__ row once every service registered in the schema has written its collective_gate marker (_wh_collective_row_wanted, #2145). 537 (#2323): the slim row leaves out its message id when it is the event id, and its hop scope when it is the scope column alone.';

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests): wh_append_event has had more than
-- one arity, so every definition site clears its overloads first.
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');

-- ── wh_append_event: last word 453, copied whole, its row less what the row holds (537) ──
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
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), 0) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555). 225: folds the stored event into wh_stream_digests as the emit chain does, unless it is ephemeral, at-most-once or of a digest-exempt type (#1600). 228: stores the event''s kind bits (p_flags) and refuses, raising SQLSTATE WH133, an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, #1546). 260: keeps the stream''s wh_stream_lifecycle row (#1732). 266: refuses an append to a sealed stream, raising SQLSTATE WHS01 (StreamSealedException), and a terminal event seals its stream (#1733). 267: a new stream starts at version 1, as on the outbox and inbox paths; an existing stream continues from its highest version (#1913). 271: refuses an append to an archived stream, raising SQLSTATE WHA01 (Refuse) or WHA02 (AutoRestore) by its type''s AppendToArchived policy (StreamArchivedException, #1738). 285: the row keeps the slim form unless kept full, carries its route id, and under the Linked form the metadata as written is staged (#1725). 295: stamps a superseding event''s key and queues the older events of its key for the reaper (#1729). 296: starts the new generation of an archived stream whose type declares NewGeneration, numbered on from its last version (#1734). 352: stamps the superseding group beside the key (#2124). 537: the slim row leaves out its message id when it is the event id, and its hop scope when it is the scope column alone (#2323).';

-- ===========================================================================================
-- History conversion
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('wh_convert_event_metadata_history');

-- 537 (#2323): a row also needs converting when it is in the slim form and still holds what its row's columns hold
-- (_wh_metadata_needs_conversion); a row written before 285 is converted to the slim form less those values, and a slim
-- row is trimmed without its route being counted again.
-- <docs>data/event-store#history-conversion</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:Convert_ARowWrittenBeforeSlimMetadata_BecomesTheTrimmedFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:Convert_ASlimRowWrittenBeforeTheTrim_IsTrimmed_AndItsRouteIsNotCountedAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:Convert_AFullRowWithItsRouteId_IsNotTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_ARowWithoutARouteId_BecomesSlimWithItsRouteId_AndARowWithOneIsUntouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_AConvertedRow_IsNotTouchedAgain_EvenFromTheStartAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_ARowOfATypeKeptFull_IsSkipped_AndCountedAsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_RowsItCannotRead_AreLeftAlone_AndNeverStopTheBatchAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderDebugMode_SkipsTheTask_AndRunsOnceItIsOffAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderTheFullFormOrSwitchedOff_SkipsTheTaskAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_AnyOtherConvertHistoryValue_RunsTheTaskAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderLinked_CountsTheRoute_AndTracesOnlyWhatIsInsideTheTraceWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_AnExistingRoute_OnlyCountsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_UnderSlim_WritesNoRoutesOrTracesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_TakesAtMostTheBatch_ResumesFromItsCursor_AndCompletesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_ARowAnotherTransactionHolds_IsLeftForTheNextBatch_WithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Convert_WhileAnotherInstanceConverts_SkipsWithoutWaitingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredMetadataTests.cs:AddWhizbangPostgres_RegistersTheHistoryConversionStep_ThatConvertsTheStoresOwnFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Performance/MetadataHistoryConversionCostScenarioTests.cs:Conversion_OnACopiedHistory_ReportAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_convert_event_metadata_history(p_batch_size INTEGER DEFAULT 1000)
RETURNS TABLE(
  rows_scanned BIGINT,
  rows_affected BIGINT,
  rows_kept_full BIGINT,
  routes_added BIGINT,
  routes_updated BIGINT,
  traces_written BIGINT,
  status TEXT
) AS $$
DECLARE
  v_batch INTEGER := GREATEST(COALESCE(p_batch_size, 1), 1);
  v_form TEXT;
  v_full_for TEXT[];
  v_debug BOOLEAN;
  v_convert BOOLEAN;
  v_trim BOOLEAN;  -- 537: the trim gate is open
  v_trace_from TIMESTAMPTZ;
  v_from UUID;
  v_completed TIMESTAMPTZ;
  v_scanned_ids UUID[];
  v_scanned BIGINT;
  v_need_ids UUID[];
  v_need_types TEXT[];
  v_need_times TIMESTAMPTZ[];
  v_kept BIGINT;
  v_affected BIGINT := 0;
  v_added BIGINT := 0;
  v_updated BIGINT := 0;
  v_traces BIGINT := 0;
  v_held UUID;
  v_held_count BIGINT;
  v_cursor UUID;
BEGIN
  -- The settings, read once. A debug flag or an off switch is read in the spellings PostgreSQL gives a boolean; any
  -- other value of metadata_convert_history is the default, on. A trace window that is not a whole number is the
  -- default, as a missing one is.
  SELECT s.write_form, s.full_for INTO v_form, v_full_for FROM __SCHEMA__._wh_metadata_write_settings() s;
  -- 537 (#2323): until the trim gate is open, a converted row keeps what an earlier release needs, and a slim row is
  -- left as it is.
  v_trim := __SCHEMA__._wh_metadata_trim_on();
  SELECT
    COALESCE(bool_or(CASE WHEN st.setting_key = 'debug_mode'
                          THEN lower(btrim(st.setting_value)) IN ('true', 't', '1', 'yes', 'y', 'on') END), FALSE),
    NOT COALESCE(bool_or(CASE WHEN st.setting_key = 'metadata_convert_history'
                              THEN lower(btrim(st.setting_value)) IN ('false', 'f', '0', 'no', 'n', 'off') END), FALSE),
    NOW() - (COALESCE(MAX(CASE WHEN st.setting_key = 'trace_retention_days' AND btrim(st.setting_value) ~ '^[0-9]{1,6}$'
                               THEN btrim(st.setting_value)::INTEGER END), 30) * INTERVAL '1 day')
  INTO v_debug, v_convert, v_trace_from
  FROM __SCHEMA__.wh_settings st
  WHERE st.setting_key IN ('debug_mode', 'metadata_convert_history', 'trace_retention_days');

  IF v_debug THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;
  IF NOT v_convert THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (metadata_convert_history=false)'::TEXT;
    RETURN;
  END IF;
  IF v_form = 'Full' THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (metadata_write_form=Full)'::TEXT;
    RETURN;
  END IF;

  -- The cursor. A deleted row starts the conversion over; a row another instance holds means that instance is
  -- converting, and this call leaves it to it rather than waiting.
  INSERT INTO __SCHEMA__.wh_metadata_history_conversion (only_row) VALUES (TRUE) ON CONFLICT DO NOTHING;
  SELECT c.last_event_id, c.completed_at INTO v_from, v_completed
  FROM __SCHEMA__.wh_metadata_history_conversion c
  WHERE c.only_row
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'skipped (another instance is converting)'::TEXT;
    RETURN;
  END IF;
  IF v_completed IS NOT NULL THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'complete'::TEXT;
    RETURN;
  END IF;

  -- The next rows by event id, through the key index, and which of them need converting: a row with its pointer (for
  -- its type) whose metadata is an object without a route id, or (537) in the slim form still holding what its row's
  -- columns hold. Rows of types kept full are counted and left alone.
  SELECT
    array_agg(s.event_id ORDER BY s.event_id),
    count(*),
    array_agg(s.event_id ORDER BY s.event_id) FILTER (WHERE s.needs AND NOT s.kept),
    array_agg(s.type_name ORDER BY s.event_id) FILTER (WHERE s.needs AND NOT s.kept),
    array_agg(s.created_at ORDER BY s.event_id) FILTER (WHERE s.needs AND NOT s.kept),
    count(*) FILTER (WHERE s.needs AND s.kept)
  INTO v_scanned_ids, v_scanned, v_need_ids, v_need_types, v_need_times, v_kept
  FROM (
    SELECT
      b.event_id,
      t.type_name,
      es.created_at,
      (es.event_id IS NOT NULL AND __SCHEMA__._wh_metadata_needs_conversion(b.metadata, es.event_id, es.scope, v_trim)) AS needs,
      __SCHEMA__._wh_metadata_kept_full(t.type_name, v_full_for) AS kept
    FROM (
      SELECT eb.event_id, eb.metadata
      FROM __SCHEMA__.wh_event_body eb
      WHERE eb.event_id > COALESCE(v_from, __EMPTY_UUID__::uuid)
      ORDER BY eb.event_id
      LIMIT v_batch
    ) b
    LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = b.event_id
    CROSS JOIN LATERAL (SELECT split_part(es.event_type, ',', 1) AS type_name) t
  ) s;

  IF v_scanned = 0 THEN
    UPDATE __SCHEMA__.wh_metadata_history_conversion SET completed_at = NOW(), updated_at = NOW() WHERE only_row;
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, 'complete'::TEXT;
    RETURN;
  END IF;

  IF v_need_ids IS NOT NULL THEN
    WITH needed AS (
      SELECT n.event_id, n.type_name, n.created_at
      FROM unnest(v_need_ids, v_need_types, v_need_times) AS n(event_id, type_name, created_at)
    ),
    -- Only the rows no other transaction holds, and still needing it once locked.
    locked AS MATERIALIZED (
      SELECT b.event_id, b.metadata, es.scope
      FROM __SCHEMA__.wh_event_body b
      JOIN __SCHEMA__.wh_event_store es ON es.event_id = b.event_id
      WHERE b.event_id = ANY(v_need_ids)
        AND __SCHEMA__._wh_metadata_needs_conversion(b.metadata, b.event_id, es.scope, v_trim)
      ORDER BY b.event_id
      FOR UPDATE OF b SKIP LOCKED
    ),
    -- Each event's route id, computed once, from the same inputs the write paths use. 537: only for a row written
    -- before 285 (fresh); a slim row keeps its route id and is only trimmed.
    batch AS MATERIALIZED (
      SELECT l.event_id, n.type_name, n.created_at, l.metadata AS full_meta, x.hops, l.scope, x.fresh,
             CASE WHEN x.fresh THEN __SCHEMA__._wh_route_id(n.type_name, x.hops) END AS route_id
      FROM locked l
      JOIN needed n ON n.event_id = l.event_id
      CROSS JOIN LATERAL (SELECT COALESCE(l.metadata -> 'Hops', l.metadata -> 'hops') AS hops, NOT (l.metadata ? 'rid') AS fresh) x
    ),
    converted AS (
      UPDATE __SCHEMA__.wh_event_body b
      SET metadata = CASE WHEN v_trim THEN __SCHEMA__._wh_trim_row_values(
        CASE WHEN bt.fresh THEN __SCHEMA__._wh_slim_event_metadata(bt.full_meta, bt.route_id) ELSE bt.full_meta END,
        bt.event_id, bt.scope) ELSE __SCHEMA__._wh_slim_event_metadata(bt.full_meta, bt.route_id) END
      FROM batch bt
      WHERE b.event_id = bt.event_id
      RETURNING b.event_id
    ),
    -- Under Linked, what distillation would have made of the staged form (286): occurrences, durations, traces.
    occurrences AS (
      SELECT bt.event_id, bt.created_at, bt.route_id, bt.type_name, COALESCE(bt.hops, '[]'::jsonb) AS hops,
             NULLIF((SELECT jsonb_object_agg(e.key, e.value) FROM jsonb_each(bt.full_meta) e WHERE e.key IN ('dc', 'rin')),
                    '{}'::jsonb) AS extra,
             __SCHEMA__._wh_hops_duration_ms(bt.hops) AS duration_ms
      FROM batch bt
      WHERE v_form = 'Linked' AND bt.fresh
    ),
    route_rows AS (
      INSERT INTO __SCHEMA__.wh_routes AS r
        (route_id, event_type, hops, first_seen, last_seen, occurrences,
         duration_count, duration_sum_ms, duration_min_ms, duration_max_ms, duration_buckets)
      SELECT
        o.route_id,
        (array_agg(o.type_name ORDER BY o.event_id))[1],
        __SCHEMA__._wh_route_hops((array_agg(o.hops ORDER BY o.event_id))[1]),
        MIN(o.created_at),
        MAX(o.created_at),
        COUNT(*),
        COUNT(*) FILTER (WHERE o.duration_ms > 0),
        COALESCE(SUM(o.duration_ms) FILTER (WHERE o.duration_ms > 0), 0),
        MIN(o.duration_ms) FILTER (WHERE o.duration_ms > 0),
        MAX(o.duration_ms) FILTER (WHERE o.duration_ms > 0),
        ARRAY[
          COUNT(*) FILTER (WHERE o.duration_ms > 0 AND o.duration_ms <= 1),
          COUNT(*) FILTER (WHERE o.duration_ms > 1 AND o.duration_ms <= 10),
          COUNT(*) FILTER (WHERE o.duration_ms > 10 AND o.duration_ms <= 100),
          COUNT(*) FILTER (WHERE o.duration_ms > 100 AND o.duration_ms <= 1000),
          COUNT(*) FILTER (WHERE o.duration_ms > 1000 AND o.duration_ms <= 10000),
          COUNT(*) FILTER (WHERE o.duration_ms > 10000)]::BIGINT[]
      FROM occurrences o
      GROUP BY o.route_id
      ORDER BY o.route_id
      ON CONFLICT (route_id) DO UPDATE SET
        first_seen = LEAST(r.first_seen, EXCLUDED.first_seen),
        last_seen = GREATEST(r.last_seen, EXCLUDED.last_seen),
        occurrences = r.occurrences + EXCLUDED.occurrences,
        duration_count = r.duration_count + EXCLUDED.duration_count,
        duration_sum_ms = r.duration_sum_ms + EXCLUDED.duration_sum_ms,
        duration_min_ms = LEAST(r.duration_min_ms, EXCLUDED.duration_min_ms),
        duration_max_ms = GREATEST(r.duration_max_ms, EXCLUDED.duration_max_ms),
        duration_buckets = ARRAY(
          SELECT u.a + u.b FROM unnest(r.duration_buckets, EXCLUDED.duration_buckets) WITH ORDINALITY AS u(a, b, ord) ORDER BY u.ord)
      RETURNING (xmax = 0) AS inserted
    ),
    -- A trace only inside the trace window, kept for the window from the event's own time as a distilled one is.
    traces AS (
      INSERT INTO __SCHEMA__.wh_traces (event_id, recorded_at, route_id, occurred_at, correlation_id, causation_id, hops, extra)
      SELECT o.event_id, o.created_at, o.route_id,
             __SCHEMA__._wh_first_current_hop_at(o.hops),
             __SCHEMA__._wh_try_uuid(o.hops -> 0 ->> 'co'),
             __SCHEMA__._wh_try_uuid(o.hops -> 0 ->> 'ca'),
             __SCHEMA__._wh_trace_hops(o.hops),
             o.extra
      FROM occurrences o
      WHERE o.created_at >= v_trace_from
      ON CONFLICT DO NOTHING
      RETURNING 1
    )
    SELECT
      (SELECT COUNT(*) FROM converted),
      (SELECT COUNT(*) FILTER (WHERE rr.inserted) FROM route_rows rr),
      (SELECT COUNT(*) FILTER (WHERE NOT rr.inserted) FROM route_rows rr),
      (SELECT COUNT(*) FROM traces)
    INTO v_affected, v_added, v_updated, v_traces;
  END IF;

  -- A row this batch needed that still needs it was held by another transaction. The cursor stops before the
  -- first of them, so the next batch reads it again; the rows after it that were converted are passed over then.
  -- (uuid has no min or max aggregate, so the first and last are read by order.)
  SELECT count(*) INTO v_held_count
  FROM __SCHEMA__.wh_event_body b
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = b.event_id
  WHERE b.event_id = ANY(v_need_ids)
    AND __SCHEMA__._wh_metadata_needs_conversion(b.metadata, b.event_id, es.scope, v_trim);
  IF v_held_count = 0 THEN
    v_cursor := v_scanned_ids[cardinality(v_scanned_ids)];
  ELSE
    SELECT b.event_id INTO v_held
    FROM __SCHEMA__.wh_event_body b
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = b.event_id
    WHERE b.event_id = ANY(v_need_ids)
      AND __SCHEMA__._wh_metadata_needs_conversion(b.metadata, b.event_id, es.scope, v_trim)
    ORDER BY b.event_id
    LIMIT 1;
    SELECT u.id INTO v_cursor FROM unnest(v_scanned_ids) AS u(id) WHERE u.id < v_held ORDER BY u.id DESC LIMIT 1;
    v_cursor := COALESCE(v_cursor, v_from);
  END IF;

  UPDATE __SCHEMA__.wh_metadata_history_conversion SET
    last_event_id = v_cursor,
    rows_converted = rows_converted + v_affected,
    completed_at = CASE WHEN v_held_count = 0 AND v_scanned < v_batch THEN NOW() END,
    updated_at = NOW()
  WHERE only_row;

  RETURN QUERY SELECT v_scanned, v_affected, v_kept, v_added, v_updated, v_traces,
    CASE WHEN v_held_count = 0 THEN 'ok'
         ELSE 'held (' || v_held_count || ' locked row(s) left for the next batch)' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_convert_event_metadata_history(INTEGER) IS
  'Issue #1727: converts up to p_batch_size event rows past the cursor in wh_metadata_history_conversion that carry no '
  'route id (rows written before 285) to the slim form plus their route id; under Linked counts their routes and '
  'traces the ones inside trace_retention_days. Skips rows of types kept full, rows it cannot read and rows another '
  'transaction holds, never waits, and is skipped under debug mode, the Full form and metadata_convert_history=false. '
  'Called by the metadata-history-conversion maintenance step. 537 (#2323): also trims a slim row still holding its message id or hop scope where its row''s columns hold them, '
  'counting no route; a converted row is the slim form less those values.';

-- ===========================================================================================
-- Reconstruction
-- ===========================================================================================

SELECT __SCHEMA__.drop_all_overloads('wh_trace_metadata');

-- Each event's full metadata, from the staged form while it exists, else the row plus its trace plus its route, else
-- the row alone; its payload; its route id; and which of the three answered.
-- 537 (#2323): the trace's and the row's answers get back what the row leaves out (_wh_restore_row_values).
-- <docs>data/event-store#reconstruction</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRowValuesSqlTests.cs:TraceMetadata_PutsTheMessageIdAndTheScopeBack_FromTheRowAndFromItsTraceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetByMessageId_ReadsStaging_ThenTraceAndRoute_ThenTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Reconstruct_WithinTheTraceWindow_IsLosslessAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Reconstruct_AfterTheTraceAgesOut_KeepsTheRouteAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_trace_metadata(p_event_ids UUID[])
RETURNS TABLE(
  event_id UUID,
  metadata JSONB,
  event_data JSONB,
  route_id UUID,
  source TEXT
) AS $$
  SELECT
    ids.eid,
    COALESCE(CASE
      WHEN st.metadata IS NOT NULL THEN st.metadata
      WHEN tr.hops IS NOT NULL AND ro.hops IS NOT NULL
        THEN __SCHEMA__._wh_restore_row_values(__SCHEMA__._wh_reconstruct_metadata(eb.metadata, ro.hops, tr.hops, tr.extra), ids.eid, es.scope)
      ELSE __SCHEMA__._wh_restore_row_values(eb.metadata - 'rid', ids.eid, es.scope)
    END, '{}'::jsonb),
    eb.event_data,
    COALESCE(st.route_id, tr.route_id, __SCHEMA__._wh_try_uuid(eb.metadata ->> 'rid')),
    CASE
      WHEN st.metadata IS NOT NULL THEN 'staged'
      WHEN tr.hops IS NOT NULL AND ro.hops IS NOT NULL THEN 'trace'
      ELSE 'row'
    END
  FROM (SELECT DISTINCT u.eid FROM unnest(p_event_ids) AS u(eid)) ids
  LEFT JOIN LATERAL (
    SELECT s.metadata, s.route_id FROM __SCHEMA__.wh_event_metadata_staging s WHERE s.event_id = ids.eid LIMIT 1) st ON TRUE
  LEFT JOIN LATERAL (
    SELECT t.hops, t.extra, t.route_id FROM __SCHEMA__.wh_traces t WHERE t.event_id = ids.eid LIMIT 1) tr ON TRUE
  LEFT JOIN __SCHEMA__.wh_routes ro ON ro.route_id = tr.route_id
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = ids.eid
  LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = ids.eid
  WHERE st.metadata IS NOT NULL OR tr.hops IS NOT NULL OR eb.event_id IS NOT NULL;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_trace_metadata(UUID[]) IS
  'Issue #1726: each event''s full metadata (staged form, else row + trace + route, else the row), payload and route '
  'id, and the source that answered (staged, trace or row). Read by the Postgres trace store. 537 (#2323): with the message id and the slim hop''s scope the row leaves out put back.';
