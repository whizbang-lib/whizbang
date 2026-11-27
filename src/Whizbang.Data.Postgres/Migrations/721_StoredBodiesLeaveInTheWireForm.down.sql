-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Down migration: 721_StoredBodiesLeaveInTheWireForm.down.sql
-- Restores wh_trace_metadata to its last word before 721 (537) and drops the wire-form functions.
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- wh_trace_metadata: its last word before 721 (537_StoredMetadataLeavesOutWhatTheRowHolds.sql).
SELECT __SCHEMA__.drop_all_overloads('wh_trace_metadata');
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

SELECT __SCHEMA__.drop_all_overloads('wh_event_body_wire_form');
SELECT __SCHEMA__.drop_all_overloads('_wh_unpack_vector');
