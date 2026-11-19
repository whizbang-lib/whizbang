-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 285_StoredMetadataIsSlimAndStaged.sql
-- Date: 2026-11-01
-- Description: Stored event metadata is slim at write time, the full form is staged, and every row carries its route
--              id (#1725, WS-2 S9).
--
--              Stored metadata was about 600 bytes per event, nearly all hops copied from the envelope. Every write
--              path (both emit chains and wh_append_event, which both direct appends call) now decides each event's
--              metadata from three wh_settings rows the maintenance cycle publishes, read once per call:
--
--                metadata_write_form  Full, Slim or Linked (the default, and what a missing or unreadable row means)
--                metadata_full_for    type names and namespaces whose rows stay full, one per line (none by default)
--                debug_mode           every row stays full
--
--              A row stays full under debug mode, under Full, or when its type is listed; otherwise it is slim. Slim
--              (_wh_slim_event_metadata) is EnvelopeMetadata.ToSlim() written in SQL: the message id, the ephemeral
--              expiry, and one hop with the first hop's timestamp, correlation, causation, stream and trace parent and
--              the scope folded over every Current hop. Every row, slim or full, carries rid, its route id
--              (_wh_route_id). Under Linked the full form is also appended to wh_event_metadata_staging, from which
--              maintenance distills routes and traces (#1726); under Full and Slim nothing is staged.
--
--              The route id is a hash of the event's type name and its hops reduced to their route form (hop type,
--              service name, topic, execution strategy, caller class and member, policy decisions), so it never
--              changes with instance ids, line numbers or per-message values. RouteNormalizer is the same function in
--              C#, pinned to this one by a shared corpus. Stamping it at write time means no event row is ever
--              rewritten to link it to its route.
--
--              wh_event_metadata_staging is created range-partitioned by recorded_at with a default partition, so a
--              write never fails for want of a partition; the partition manager keeps daily partitions when
--              partitioning is enabled. Its only index is its key (event_id, recorded_at), so the hot path adds one
--              heap tuple and one index entry per event and checks nothing.
--
--              The readers of stored metadata keep working: ephemeral_expires_at (the TTL reapers and the retry
--              midpoint) is kept, deliveryGuarantee was never in stored metadata (214 made it a flag), and the first
--              hop's service, which redelivered-origin adoption reads (253), already treats an absent service as
--              nothing to compare.
--
--              Number 285: reserved for this change (285 to 289).
--
-- Dependencies: 271 (last word of _emit_event_store_chain, _emit_event_store_chain_for_inbox and wh_append_event),
--               226 and 233 (the partition manager), 028 (wh_settings), 077 (wh_event_body)
-- Objects: wh_event_metadata_staging, wh_event_metadata_staging_default, _wh_metadata_write_settings,
--          _wh_metadata_kept_full, _wh_route_text, _wh_route_id, _wh_fold_scope_deltas, _wh_slim_event_metadata,
--          _emit_event_store_chain, _emit_event_store_chain_for_inbox, wh_append_event
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_metadata_write_settings');
SELECT __SCHEMA__.drop_all_overloads('_wh_metadata_kept_full');
SELECT __SCHEMA__.drop_all_overloads('_wh_route_text');
SELECT __SCHEMA__.drop_all_overloads('_wh_route_id');
SELECT __SCHEMA__.drop_all_overloads('_wh_fold_scope_deltas');
SELECT __SCHEMA__.drop_all_overloads('_wh_slim_event_metadata');

-- ===========================================================================================
-- The staging table
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_metadata_staging (
  event_id UUID NOT NULL,
  recorded_at TIMESTAMPTZ NOT NULL,
  route_id UUID NOT NULL,
  event_type TEXT NOT NULL,
  metadata JSONB NOT NULL,
  CONSTRAINT pk_event_metadata_staging PRIMARY KEY (event_id, recorded_at)
) PARTITION BY RANGE (recorded_at);

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_metadata_staging_default
  PARTITION OF __SCHEMA__.wh_event_metadata_staging DEFAULT;

COMMENT ON TABLE __SCHEMA__.wh_event_metadata_staging IS
  'The full stored metadata of each event written under metadata_write_form Linked (285, #1725), append-only and '
  'partitioned by day on recorded_at. Maintenance distills it into wh_routes and wh_traces and removes what it '
  'distilled; metadata_staging_days (default 2) bounds what it has not.';

-- ===========================================================================================
-- The settings, read once per call
-- ===========================================================================================

-- A missing or unreadable row reads as the default, and nothing here can raise: every write path calls this, and a
-- typed value an operator wrote by hand must never fail an event's write.
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:MissingSettings_BehaveAsTheDefaultAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:UnreadableSettings_BehaveAsTheDefault_AndNeverFailTheWriteAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_metadata_write_settings(
  OUT write_form TEXT,
  OUT full_for TEXT[],
  OUT keep_all BOOLEAN
) AS $$
  SELECT
    v.form,
    COALESCE((
      SELECT array_agg(btrim(e) ORDER BY btrim(e))
      FROM unnest(string_to_array(v.full_for_text, E'\n')) AS u(e)
      WHERE btrim(e) <> ''), '{}'::TEXT[]),
    v.debug OR v.form = 'Full'
  FROM (
    SELECT
      CASE lower(btrim(MAX(CASE WHEN s.setting_key = 'metadata_write_form' THEN s.setting_value END)))
        WHEN 'full' THEN 'Full'
        WHEN 'slim' THEN 'Slim'
        ELSE 'Linked'
      END AS form,
      MAX(CASE WHEN s.setting_key = 'metadata_full_for' THEN s.setting_value END) AS full_for_text,
      COALESCE(bool_or(CASE WHEN s.setting_key = 'debug_mode'
                            THEN lower(btrim(s.setting_value)) IN ('true', 't', '1', 'yes', 'y', 'on') END), FALSE) AS debug
    FROM __SCHEMA__.wh_settings s
    WHERE s.setting_key IN ('metadata_write_form', 'metadata_full_for', 'debug_mode')
  ) v;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_metadata_write_settings() IS
  'Issue #1725: the stored-metadata settings a write path reads once per call: the write form (Full, Slim or Linked, '
  'Linked when absent or unreadable), the full-for list (one type name or namespace per line) and whether every row '
  'stays full (debug mode, or the Full form). Never raises.';

-- A listed entry keeps a type full when it is the type's name, or a namespace or containing type of it.
-- <docs>data/event-store#keeping-an-event-type-full</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:FullListedType_KeepsTheFullRow_ByTypeAndByNamespaceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_metadata_kept_full(p_type_name TEXT, p_full_for TEXT[])
RETURNS BOOLEAN AS $$
DECLARE
  v_entry TEXT;
BEGIN
  -- A loop rather than a query: every write path asks this once per event, and an empty list (the default) answers at once.
  IF p_full_for IS NULL OR cardinality(p_full_for) = 0 THEN
    RETURN FALSE;
  END IF;
  FOREACH v_entry IN ARRAY p_full_for LOOP
    IF p_type_name = v_entry OR left(p_type_name, length(v_entry) + 1) IN (v_entry || '.', v_entry || '+') THEN
      RETURN TRUE;
    END IF;
  END LOOP;
  RETURN FALSE;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_metadata_kept_full(TEXT, TEXT[]) IS
  'Issue #1725: whether a type name is kept full by the full-for list: an entry equal to it, or a namespace (entry + '
  '''.'') or containing type (entry + ''+'') of it. A name that merely starts with an entry is not matched.';

-- ===========================================================================================
-- The route id
-- ===========================================================================================

-- RouteNormalizer.CanonicalText in SQL. Fields join with U+001F, a decision's fields with U+001D, decisions with U+001C,
-- the type and each hop with U+001E. Only the short hop names the hop writer emits are read. Written as loops over the
-- arrays rather than as a query: every write path calls it once per event, and a SQL function with a subquery is never
-- inlined, so each call would run the executor (measured at hundreds of microseconds an event).
-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRouteCorpusTests.cs:EveryCorpusEntry_HasTheSameRouteId_InSqlAndInCSharpAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_route_text(p_type_name TEXT, p_hops JSONB)
RETURNS TEXT AS $$
DECLARE
  v_text TEXT := COALESCE(p_type_name, '');
  v_hop JSONB;
  v_decisions JSONB;
  v_decision JSONB;
  v_trail TEXT;
BEGIN
  IF jsonb_typeof(p_hops) IS DISTINCT FROM 'array' THEN
    RETURN v_text;
  END IF;
  FOR i IN 0 .. jsonb_array_length(p_hops) - 1 LOOP
    v_hop := p_hops -> i;
    v_trail := '';
    v_decisions := v_hop -> 'tr' -> 'd';
    IF jsonb_typeof(v_decisions) = 'array' THEN
      FOR j IN 0 .. jsonb_array_length(v_decisions) - 1 LOOP
        v_decision := v_decisions -> j;
        v_trail := v_trail || CASE WHEN j > 0 THEN chr(28) ELSE '' END
          || COALESCE(v_decision ->> 'pn', '') || chr(29) || COALESCE(v_decision ->> 'r', '') || chr(29)
          || CASE WHEN v_decision ->> 'm' = 'true' THEN '1' ELSE '0' END;
      END LOOP;
    END IF;
    v_text := v_text || chr(30)
      || COALESCE(v_hop ->> 'ty', '0') || chr(31)
      || COALESCE(v_hop -> 'si' ->> 'sn', '') || chr(31)
      || COALESCE(v_hop ->> 'to', '') || chr(31)
      || COALESCE(v_hop ->> 'es', '') || chr(31)
      || regexp_replace(regexp_replace(COALESCE(v_hop ->> 'cf', ''), '^.*[/\\]', ''), '\.[^.]*$', '') || chr(31)
      || COALESCE(v_hop ->> 'cm', '') || chr(31)
      || v_trail;
  END LOOP;
  RETURN v_text;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_route_text(TEXT, JSONB) IS
  'Issue #1725: the canonical route text of an event (RouteNormalizer.CanonicalText): its type name and, per hop, the '
  'hop type, service name, topic, execution strategy, caller class (the file name without folders or extension), '
  'caller member and policy decisions (policy, rule, matched).';

-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRouteCorpusTests.cs:EveryCorpusEntry_HasTheSameRouteId_InSqlAndInCSharpAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:OutboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_route_id(p_type_name TEXT, p_hops JSONB)
RETURNS UUID AS $$
  SELECT encode(substring(sha256(convert_to(__SCHEMA__._wh_route_text(p_type_name, p_hops), 'UTF8')) FROM 1 FOR 16), 'hex')::UUID;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_route_id(TEXT, JSONB) IS
  'Issue #1725: the route id of an event: the first 16 bytes of the SHA-256 of its canonical route text, as a uuid '
  '(RouteNormalizer.RouteId).';

-- ===========================================================================================
-- The slim form
-- ===========================================================================================

-- ScopeDelta.ApplyTo over every Current hop's scope, in order, written as one delta from nothing. One delta (the common
-- case) is returned as it is. Values: a later value replaces an earlier one. Roles, permissions and principals: a set
-- replaces, otherwise removes then adds. Claims: removes then adds (a set is ignored, as ApplyTo ignores it).
-- <docs>messaging/message-envelopes#stored-form</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRouteCorpusTests.cs:SlimForm_InSql_ReadsLikeToSlim_ForEveryCorpusEntryAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fold_scope_deltas(p_hops JSONB)
RETURNS JSONB AS $$
DECLARE
  v_deltas JSONB[];
  v_delta JSONB;
  v_values JSONB := '{}'::jsonb;
  v_sets JSONB := '{}'::jsonb;
  v_claims JSONB;
  v_prop TEXT;
  v_change JSONB;
  v_collections JSONB;
BEGIN
  -- A loop rather than a query, so the common call (one scoped hop) runs no statement.
  IF jsonb_typeof(p_hops) IS DISTINCT FROM 'array' THEN
    RETURN NULL;
  END IF;
  FOR i IN 0 .. jsonb_array_length(p_hops) - 1 LOOP
    v_delta := p_hops -> i;
    IF COALESCE(v_delta ->> 'ty', '0') = '0' AND jsonb_typeof(v_delta -> 'sc') = 'object' THEN
      v_deltas := array_append(v_deltas, v_delta -> 'sc');
    END IF;
  END LOOP;

  IF v_deltas IS NULL THEN
    RETURN NULL;
  END IF;
  IF cardinality(v_deltas) = 1 THEN
    RETURN v_deltas[1];
  END IF;

  FOREACH v_delta IN ARRAY v_deltas LOOP
    IF jsonb_typeof(v_delta -> 'v') = 'object' THEN
      v_values := v_values || (v_delta -> 'v');
    END IF;
    IF jsonb_typeof(v_delta -> 'c') = 'object' THEN
      FOR v_prop, v_change IN SELECT e.key, e.value FROM jsonb_each(v_delta -> 'c') AS e LOOP
        IF v_prop IN ('Cl', 'Claims') THEN
          v_claims := (COALESCE(v_claims, '{}'::jsonb) - COALESCE(ARRAY(
                          SELECT jsonb_array_elements_text(
                            CASE WHEN jsonb_typeof(v_change -> 'r') = 'array' THEN v_change -> 'r' ELSE '[]'::jsonb END)),
                        '{}'::TEXT[]))
                      || CASE WHEN jsonb_typeof(v_change -> 'a') = 'object' THEN v_change -> 'a' ELSE '{}'::jsonb END;
        ELSIF jsonb_typeof(v_change -> 's') = 'array' THEN
          v_sets := v_sets || jsonb_build_object(v_prop, v_change -> 's');
        ELSE
          v_sets := v_sets || jsonb_build_object(v_prop, COALESCE((
            SELECT jsonb_agg(m.item ORDER BY m.ord)
            FROM (
              SELECT k.item, min(k.ord) AS ord
              FROM (
                SELECT cur.item, cur.ord
                FROM jsonb_array_elements(COALESCE(v_sets -> v_prop, '[]'::jsonb)) WITH ORDINALITY AS cur(item, ord)
                UNION ALL
                SELECT added.item, 1000000 + added.ord
                FROM jsonb_array_elements(
                  CASE WHEN jsonb_typeof(v_change -> 'a') = 'array' THEN v_change -> 'a' ELSE '[]'::jsonb END)
                  WITH ORDINALITY AS added(item, ord)
              ) k
              WHERE NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements(
                  CASE WHEN jsonb_typeof(v_change -> 'r') = 'array' THEN v_change -> 'r' ELSE '[]'::jsonb END) AS r(item)
                WHERE r.item = k.item AND k.ord < 1000000)
              GROUP BY k.item
            ) m), '[]'::jsonb));
        END IF;
      END LOOP;
    END IF;
  END LOOP;

  SELECT jsonb_object_agg(e.key, jsonb_build_object('s', e.value)) INTO v_collections FROM jsonb_each(v_sets) AS e;
  IF v_claims IS NOT NULL THEN
    v_collections := COALESCE(v_collections, '{}'::jsonb) || jsonb_build_object('Cl', jsonb_build_object('a', v_claims));
  END IF;

  RETURN NULLIF(
    CASE WHEN v_values = '{}'::jsonb THEN '{}'::jsonb ELSE jsonb_build_object('v', v_values) END
    || CASE WHEN v_collections IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('c', v_collections) END,
    '{}'::jsonb);
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_scope_deltas(JSONB) IS
  'Issue #1725: the scope deltas of every Current hop folded into one delta, as ScopeDelta.ApplyTo applies them in '
  'order; a single delta is returned unchanged. NULL when no Current hop carries a scope.';

-- EnvelopeMetadata.ToSlim() in SQL, plus the route id. Only the hop list is reduced, under the key the writer used
-- (Hops, or hops in the Dapper store's form): the slim hop keeps the first hop's ts, co, ca, st and tp and the scope
-- folded over every Current hop. The dispatch context (dc) and receptor invocations (rin) are dropped, as ToSlim drops
-- them. Every other top-level key is kept as written, so each store's reader finds its own keys (the message id, the
-- Dapper store's correlation_id, causation_id and pri, the ephemeral expiry and TTL). Plain assignments rather than a
-- query, for the reason _wh_route_text gives.
-- <docs>messaging/message-envelopes#stored-form</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataRouteCorpusTests.cs:SlimForm_InSql_ReadsLikeToSlim_ForEveryCorpusEntryAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:OutboxChain_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DirectAppend_KeysTheSlimFormDoesNotReduce_AreKeptAsWrittenAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_slim_event_metadata(p_full JSONB, p_route_id UUID)
RETURNS JSONB AS $$
DECLARE
  v_out JSONB := COALESCE(p_full, '{}'::jsonb) - 'dc' - 'rin';
  v_key TEXT := CASE WHEN p_full ? 'Hops' THEN 'Hops' WHEN p_full ? 'hops' THEN 'hops' END;
  v_hops JSONB;
  v_first JSONB;
  v_hop JSONB;
  v_scope JSONB;
BEGIN
  IF v_key IS NOT NULL THEN
    v_hops := p_full -> v_key;
    IF jsonb_typeof(v_hops) = 'array' AND jsonb_array_length(v_hops) > 0 THEN
      v_first := v_hops -> 0;
      v_hop := jsonb_strip_nulls(jsonb_build_object(
        'ts', v_first -> 'ts', 'co', v_first -> 'co', 'ca', v_first -> 'ca', 'st', v_first -> 'st', 'tp', v_first -> 'tp'));
      v_scope := __SCHEMA__._wh_fold_scope_deltas(v_hops);
      IF v_scope IS NOT NULL THEN
        v_hop := v_hop || jsonb_build_object('sc', v_scope);
      END IF;
      v_out := v_out || jsonb_build_object(v_key, jsonb_build_array(v_hop));
    END IF;
  END IF;
  IF p_route_id IS NOT NULL THEN
    v_out := v_out || jsonb_build_object('rid', p_route_id);
  END IF;
  RETURN v_out;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_slim_event_metadata(JSONB, UUID) IS
  'Issue #1725: the slim stored form of a full metadata document (EnvelopeMetadata.ToSlim): the hop list (Hops, or '
  'hops) reduced to one hop with the first hop''s ts, co, ca, st and tp and the scope folded over every Current hop; '
  'dc and rin dropped; every other key kept as written; rid added.';

-- ===========================================================================================
-- The write paths, copied whole from their last word in 271 with the 285 delta
-- ===========================================================================================

-- _emit_event_store_chain: last word 271. 285 reads the settings once, splits the metadata it already built into the
-- full form and the row's form, stamps the route id, and stages the full form under Linked.

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
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
      oe.flags
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
    RETURNING event_id, stream_id, version
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
      -- 216 (#1492): audit records are never digested.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
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
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725).';


-- _emit_event_store_chain_for_inbox: last word 271, with the same 285 delta.

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:InboxAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamOrderSqlTests.cs:TwoClaimsOfOneStream_TheHeadsClaimCommitsLast_VersionsFollowTheProducerOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:InboxStoredAuditRecord_LeavesTheOriginsDigestLaneUntouched_WhileADomainEventFoldsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:TerminalEvent_OnTheInbox_SealsTheStream_AndLaterRowsAreRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:InboxAppend_ToASealedStream_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_Refuse_DeadLettersTheRow_AndStoresTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:InboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesTheRequestAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
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
  v_sealed_refused UUID[];                 -- 266: the rows a sealed stream refused
  v_any_sealed BOOLEAN;                    -- 271: a candidate's stream is sealed
  v_any_archived BOOLEAN;                  -- 271: a candidate's stream is archived (and not sealed)
  v_archived_refused UUID[];               -- 271: the rows an archived stream refused
  v_write_form TEXT;                       -- 285: Full, Slim or Linked
  v_full_for TEXT[];                       -- 285: the type names and namespaces kept full
  v_keep_all BOOLEAN;                      -- 285: debug mode or the Full form keeps every row full
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
    RETURNING event_id, stream_id, version
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
      -- 216 (#1492): audit records are never digested; a received one no longer lands in its origin's lane.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
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
  'Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732). 266: dead-letters each received event a sealed stream refuses and stores the rest; a terminal event seals its stream in the same statement (#1733). 271: and each received event an archived stream refuses, by its type''s AppendToArchived policy, queuing the stream''s restore under AutoRestore (#1738). 285: each row keeps the slim form unless kept full, carries its route id, and under the Linked form stages its full form (#1725).';


-- wh_append_event: last word 271, with the same 285 delta. The signature is the same, and the overloads are cleared
-- first because this name has had more than one arity (README rule 5).
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');

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
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:DirectAppend_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:DirectAppend_AutoRestore_RaisesTheRestoreState_AndStoresNothingAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveAppendPolicyTests.cs:AppendAsync_ToAnArchivedStream_Refuse_ThrowsStreamArchivedExceptionAsync</tests>
-- <docs>data/event-store#stored-metadata</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DirectAppend_DefaultForm_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:EFCoreEventStore_Append_StoresTheSlimRow_AndStagesTheFullFormAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataWriteSqlTests.cs:DirectAppend_KeysTheSlimFormDoesNotReduce_AreKeptAsWrittenAsync</tests>
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
  v_ends_stream BOOLEAN;                   -- 266: this event seals its stream
  v_closed RECORD;                         -- 271: the stream's row when it is sealed or archived
  v_policy TEXT;                           -- 271: the AppendToArchived policy of an archived stream's type
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
    SELECT COALESCE(MAX(p.append_to_archived), 'Refuse') INTO v_policy
    FROM __SCHEMA__.wh_stream_type_policies p
    WHERE p.stream_type = v_closed.stream_type;
    RAISE EXCEPTION USING
      ERRCODE = CASE WHEN v_policy = 'AutoRestore' THEN 'WHA02' ELSE 'WHA01' END,
      MESSAGE = __SCHEMA__._wh_stream_archived_refusal_text(p_stream_id, v_closed.last_version, v_policy),
      HINT = 'Restore the stream (IArchiveStore.RestoreAsync) before appending to it.';
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
    ELSE __SCHEMA__._wh_slim_event_metadata(v_full, v_route_id)
  END;

  -- A separate statement: its snapshot is taken after the lock was granted.
  INSERT INTO __SCHEMA__.wh_event_store (
    event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at, flags
  )
  SELECT
    p_event_id,
    p_stream_id,
    p_aggregate_id,
    p_aggregate_type,
    p_event_type,
    p_scope,
    COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id), 0) + 1,
    p_created_at,
    COALESCE(p_flags, 0)
  RETURNING version, flags INTO v_version, v_flags;

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
  IF COALESCE(v_flags, 0) & c_flag_ephemeral = 0
     AND COALESCE(v_flags, 0) & c_flag_at_most_once = 0
     AND split_part(p_event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) THEN
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    VALUES (
      __EMPTY_UUID__::uuid,  -- NOSONAR S1192: the local-origin sentinel recurs per function; PL/pgSQL has no file-level constants
      COALESCE(p_scope ->> 't', ''),
      p_event_type,
      p_stream_id,
      hashtextextended(p_event_id::text, 0),
      hashtextextended(p_event_id::text, 1),
      1,
      p_created_at
    )
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at;
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
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), 0) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555). 225: folds the stored event into wh_stream_digests as the emit chain does, unless it is ephemeral, at-most-once or of a digest-exempt type (#1600). 228: stores the event''s kind bits (p_flags) and refuses, raising SQLSTATE WH133, an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, #1546). 260: keeps the stream''s wh_stream_lifecycle row (#1732). 266: refuses an append to a sealed stream, raising SQLSTATE WHS01 (StreamSealedException), and a terminal event seals its stream (#1733). 267: a new stream starts at version 1, as on the outbox and inbox paths; an existing stream continues from its highest version (#1913). 271: refuses an append to an archived stream, raising SQLSTATE WHA01 (Refuse) or WHA02 (AutoRestore) by its type''s AppendToArchived policy (StreamArchivedException, #1738). 285: the row keeps the slim form unless kept full, carries its route id, and under the Linked form the metadata as written is staged (#1725).';
