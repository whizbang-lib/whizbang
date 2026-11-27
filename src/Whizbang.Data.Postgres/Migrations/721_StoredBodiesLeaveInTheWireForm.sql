-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 721_StoredBodiesLeaveInTheWireForm.sql
-- Date: 2026-11-27
-- Description: A stored body that leaves the database for another service, or for code outside the framework, carries
--              its vectors as JSON numbers, never packed (#2626, V3 of the vector storage plan, #2333).
--
--              720 packs vectors in wh_event_body only. Every typed reader reads either form, but two reads hand the
--              stored body on as raw JSON: the redelivery selection (an integrity repair sends an origin's events to a
--              peer, and the audit rebuild re-records them; both go to the transport) and the trace store
--              (wh_trace_metadata, whose payload a consumer reads as a JsonElement). Both now read the body through
--              wh_event_body_wire_form, which writes each packed vector at the type's registered paths back as the
--              JSON numbers it was packed from (_wh_unpack_vector: each value's binary32 bits become a float4, whose
--              shortest text reads back to the same bits). A body with nothing packed is returned as it is.
--
--              The inventory of reads is derived by machine (PackedVectorExportInventoryTests): every function's latest
--              definition and every driver source that reads a stored body is classified, and an export that does not
--              go through the wire form fails.
--
--              Number 721: reserved for this change (720 to 729).
--
-- Reversal: reversible (the down file restores wh_trace_metadata's last word and drops the wire-form functions)
-- Dependencies: 720 (wh_event_vector_paths, the packed form), 537 (wh_trace_metadata)
-- Objects: _wh_unpack_vector, wh_event_body_wire_form, wh_trace_metadata
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('_wh_unpack_vector');
SELECT __SCHEMA__.drop_all_overloads('wh_event_body_wire_form');

-- One packed vector back in the wire form: a JSON string holding base64 of whole big-endian binary32 values becomes the
-- JSON array of those floats, each written as its float4's shortest text. Anything else (not a string, not base64, not
-- whole values, or a value that is not finite, which no packed body holds) is returned as it is.
-- <docs>data/event-store#what-leaves-the-database</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorExportSqlTests.cs:WireForm_WritesBackTheNumbersAPackedVectorHeld_BitExactAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorExportSqlTests.cs:WireForm_LeavesAValueThatIsNotAPackedVector_AsItIsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_unpack_vector(p_value JSONB)
RETURNS JSONB AS $$
DECLARE
  v_bytes BYTEA;
  v_array JSONB;
  v_finite BOOLEAN;
BEGIN
  IF jsonb_typeof(p_value) IS DISTINCT FROM 'string' THEN
    RETURN p_value;
  END IF;
  BEGIN
    v_bytes := decode(p_value #>> '{}', 'base64');
  EXCEPTION WHEN invalid_parameter_value THEN
    -- Not base64: a string that is not a packed vector.
    RETURN p_value;
  END;
  IF length(v_bytes) = 0 OR length(v_bytes) % 4 <> 0 THEN
    RETURN p_value;
  END IF;
  -- Each value from its bits (sign, exponent, fraction); every binary32 is exact in float8, so the cast is exact.
  SELECT jsonb_agg(to_jsonb(((CASE WHEN b.e = 0 THEN b.m * 2::float8 ^ -149 ELSE (8388608 + b.m) * 2::float8 ^ (b.e - 150) END)
                             * (1 - 2 * b.s))::float4) ORDER BY b.i) FILTER (WHERE b.e < 255),
         bool_and(b.e < 255)
    INTO v_array, v_finite
  FROM (
    SELECT i,
           get_byte(v_bytes, i * 4) >> 7 AS s,
           ((get_byte(v_bytes, i * 4) & 127) << 1) | (get_byte(v_bytes, i * 4 + 1) >> 7) AS e,
           (((get_byte(v_bytes, i * 4 + 1) & 127) << 16) | (get_byte(v_bytes, i * 4 + 2) << 8) | get_byte(v_bytes, i * 4 + 3))::float8 AS m
    FROM generate_series(0, length(v_bytes) / 4 - 1) AS i
  ) b;
  RETURN CASE WHEN v_finite THEN v_array ELSE p_value END;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_unpack_vector(JSONB) IS
  'Issue #2626: one packed vector (base64 of big-endian binary32 values) back as the JSON array of its floats; anything '
  'else is returned as it is.';

-- A stored body in the wire form: the vectors at the event type's registered paths that are packed are written back as
-- JSON numbers. A type with no paths, or a body with nothing packed, is returned as it is. Every read that sends a
-- stored body out of the database reads it through this.
-- <docs>data/event-store#what-leaves-the-database</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorExportSqlTests.cs:Redelivery_APackedEvent_IsSelectedInTheWireForm_WhichAPlainReaderReadsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorExportSqlTests.cs:WireForm_WritesBackTheNumbersAPackedVectorHeld_BitExactAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_event_body_wire_form(p_event_type TEXT, p_body JSONB)
RETURNS JSONB AS $$
DECLARE
  v_paths JSONB;
  v_path TEXT[];
  v_body JSONB := p_body;
BEGIN
  IF p_body IS NULL THEN
    RETURN NULL;
  END IF;
  SELECT p.paths INTO v_paths FROM __SCHEMA__.wh_event_vector_paths p WHERE p.event_type = p_event_type;
  IF v_paths IS NULL THEN
    RETURN p_body;
  END IF;
  FOR v_path IN SELECT ARRAY(SELECT jsonb_array_elements_text(x.path)) FROM jsonb_array_elements(v_paths) AS x(path) LOOP
    IF jsonb_typeof(v_body #> v_path) = 'string' THEN
      v_body := jsonb_set(v_body, v_path, __SCHEMA__._wh_unpack_vector(v_body #> v_path), false);
    END IF;
  END LOOP;
  RETURN v_body;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_event_body_wire_form(TEXT, JSONB) IS
  'Issue #2626: a stored body with its packed vectors (at the type''s wh_event_vector_paths) written back as JSON numbers: '
  'the form every read that sends a stored body out of the database returns.';

-- ── wh_trace_metadata: last word 537, copied whole, its payload in the wire form (721) ──
SELECT __SCHEMA__.drop_all_overloads('wh_trace_metadata');
-- <docs>data/event-store#what-leaves-the-database</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorExportSqlTests.cs:TraceStore_APackedEvent_IsReadInTheWireFormAsync</tests>
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
    -- 721 (#2626): the payload in the wire form, its packed vectors written back as JSON numbers.
    __SCHEMA__.wh_event_body_wire_form(es.event_type, eb.event_data),
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
  'id, and the source that answered (staged, trace or row). Read by the Postgres trace store. 537 (#2323): with the message id and the slim hop''s scope the row leaves out put back. 721 (#2626): the payload in the wire form (wh_event_body_wire_form), never packed.';
