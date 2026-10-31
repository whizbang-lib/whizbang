-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 264_DrainSaysWhichEventIsFirstInItsStream.sql
-- Date: 2026-10-30
-- Description: get_stream_events returns, per event, whether it is the first in its stream (#1878).
--
--              A perspective's Apply reads ApplyContext.Current.IsFirstEventInStream to set first-seen values,
--              because the model it is handed is never null and a row cannot say which event came first (a purge,
--              an eviction, a rebuild or a replay changes it). The fact comes from the event's own position, so the
--              live drain has to read it with the event. out_first_in_stream is that position test:
--
--                version 0                                       first (a stream numbered from 0)
--                version 1, and the stream holds no version 0    first (a stream numbered from 1)
--                anything else                                   not first
--
--              The version-0 lookup runs only for a version-1 row (CASE evaluates its arms in order), one probe of
--              the (stream_id, version) unique index per stream, and never for a stream's later events.
--
--              Nothing else changes: the body is the 139 definition copied whole (rule 5). The RETURNS TABLE shape
--              gains a column, so the prior overloads are dropped first.
-- Dependencies: 139 (get_stream_events, the last word before this file), 058 (unstamped gate), 059 (ownership gate),
--               072 (ephemeral body join)
-- Objects: get_stream_events
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('get_stream_events');

-- <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
-- <docs>fundamentals/work-coordinator/commit-sequence</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FirstEventInStreamReadTests.cs:GetStreamEvents_ReturnsWhetherEachEventIsTheFirstInItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/GetStreamEventsCommitSequencePropagationSqlTests.cs:GetStreamEvents_StampedReturnedUnstampedExcludedOnSameStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveFailureCounterSqlTests.cs:GetStreamEvents_SurfacesFailures_ForTheDeadLetterDecisionAsync</tests>
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
  out_attempts INTEGER,
  out_failures INTEGER,
  out_first_in_stream BOOLEAN
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  v_stamp_grace CONSTANT INTERVAL := INTERVAL '5 seconds';
  v_stamp_cutoff TIMESTAMPTZ;
BEGIN
  v_lease_expiry := p_now + (p_lease_seconds || ' seconds')::INTERVAL;
  v_stamp_cutoff := p_now - v_stamp_grace;

  -- Atomic claim+fetch (slice 25) + grace-windowed unstamped gate (mig 058) + live-owner gate
  -- (mig 059). A row is claimable only if its stream is NOT owned by a DIFFERENT live instance:
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
              WHERE sa.application_name = __INSTANCE_APPLICATION_NAME_PREFIX__ || ast.assigned_instance_id::text
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
    eb.event_data::TEXT,
    eb.metadata::TEXT,
    es.scope::TEXT,
    pe.event_work_id,
    pe.perspective_name,
    es.commit_sequence,
    pe.attempts,
    pe.failures,
    -- 264 (#1878): the event's position decides it, never a row. A version-1 event is first unless its stream also
    -- holds a version 0 (numbered from 0, before numbering was unified); the probe runs for version-1 rows only.
    CASE
      WHEN es.version = 0 THEN TRUE
      WHEN es.version = 1 THEN NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store e0
        WHERE e0.stream_id = es.stream_id AND e0.version = 0
      )
      ELSE FALSE
    END
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

COMMENT ON FUNCTION __SCHEMA__.get_stream_events(UUID, UUID[], TIMESTAMPTZ, INTEGER) IS
  'Atomic claim+fetch of pending perspective work for the given streams (slice 25): claims unleased '
  'or lapsed rows for the instance (bumping attempts), gates unstamped rows behind a grace window '
  '(058) and streams owned by another live instance (059), then returns the rows leased to the '
  'instance with their event bodies, commit sequence, attempts and failures (139), and whether each '
  'event is the first in its stream (264).';
