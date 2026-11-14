-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 444_HistoricalAuditRecordsBecomePointers.sql
-- Date: 2026-11-14
-- Description: Audit records written before the pointer default keep a full copy of the audited event; maintenance
--              converts them to the pointer form (#2326).
--
--              Since #1588 an EventAudited record carries OriginalBody only for a type declared
--              [AuditEvent(IncludeBody = true)] (or under AuditBodyDefault.Include); every other record points at the
--              audited event through OriginalEventId. Records written before that each carry the whole audited body, and
--              nothing converted them: about 2.4 GB of audit bodies on one long-running database, from records that sit
--              on the audited events' own streams, so a stream-level policy reaches few of them.
--
--              wh_audit_body_apply is one batch of the per-type history walker (380, operation audit-body) over the
--              EventAudited type: the type's stamped records in commit order, a window per transaction, resumable. For
--              each record in the window that still carries OriginalBody, it removes the key unless the record's
--              OriginalEventType is one of p_kept, the names of the types that keep their bodies (the step passes each
--              such type's simple and full name, the two forms the audit writers record). The record's other fields,
--              its pointer to the audited event among them, are untouched, so the stored record is exactly what the
--              writers store for it now. No event id, version or stream changes, so the stream digests are untouched.
--
--              wh_pending_reclamation reports the walk's remaining records as the convert_audit_record_bodies task.
--
--              Number 444: reserved for this change.
--
-- Reversal: lossy (the function goes and wh_pending_reclamation goes back to 442; the bodies removed are not restored,
--           the audited events still hold them)
-- Dependencies: 380 (wh_type_history_walks, _wh_type_history_window_is_mine, _wh_type_history_advance), 077
--               (wh_event_body), 442 (wh_pending_reclamation)
-- Objects: wh_audit_body_apply, wh_pending_reclamation
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

SELECT __SCHEMA__.drop_all_overloads('wh_audit_body_apply');

-- One batch of the audit body conversion: the window (p_after, p_through] of the EventAudited type's commit sequences.
-- <docs>fundamentals/events/system-events#audit-body</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs:Step_ConvertsAHistoricalRecordToThePointerForm_AndKeepsIncludeBodyTypesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs:Step_ConvertsInBoundedBatches_Resumes_AndReportsWhatIsLeftAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_audit_body_apply(
  p_event_type TEXT,
  p_after BIGINT,
  p_through BIGINT,
  p_scan_size INTEGER,
  p_rows_scanned BIGINT,
  p_kept TEXT[]
)
RETURNS TABLE(status TEXT, records_converted BIGINT, records_kept BIGINT, events_total BIGINT, events_matched BIGINT)
LANGUAGE plpgsql
-- Planned with the real type and window, so the window is read through the type's index (as the other walk applies).
SET plan_cache_mode = force_custom_plan
AS $$
DECLARE
  c_operation CONSTANT TEXT := 'audit-body';
  v_type TEXT := __SCHEMA__.normalize_event_type(p_event_type);
  v_kept_names TEXT[] := COALESCE(p_kept, ARRAY[]::TEXT[]);
  v_converted BIGINT := 0;
  v_kept BIGINT := 0;
  v_end RECORD;
BEGIN
  IF NOT __SCHEMA__._wh_type_history_window_is_mine(c_operation, v_type, p_after) THEN
    RETURN QUERY SELECT 'skipped (the window is no longer this call''s)'::TEXT, 0::BIGINT, 0::BIGINT,
      COALESCE((SELECT h.events_total FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0),
      COALESCE((SELECT h.events_matched FROM __SCHEMA__.wh_type_history_walks h WHERE h.operation = c_operation AND h.event_type = v_type), 0);
    RETURN;
  END IF;

  SELECT count(*) INTO v_kept
  FROM __SCHEMA__.wh_event_store es
  JOIN __SCHEMA__.wh_event_body b ON b.event_id = es.event_id
  WHERE es.event_type = v_type
    AND es.commit_sequence IS NOT NULL
    AND es.commit_sequence > COALESCE(p_after, -1)
    AND es.commit_sequence <= p_through
    AND jsonb_typeof(b.event_data) = 'object'
    AND b.event_data ? 'OriginalBody'
    AND COALESCE(b.event_data ->> 'OriginalEventType', '') = ANY(v_kept_names);

  UPDATE __SCHEMA__.wh_event_body b
  SET event_data = b.event_data - 'OriginalBody'
  FROM __SCHEMA__.wh_event_store es
  WHERE es.event_type = v_type
    AND es.commit_sequence IS NOT NULL
    AND es.commit_sequence > COALESCE(p_after, -1)
    AND es.commit_sequence <= p_through
    AND b.event_id = es.event_id
    AND jsonb_typeof(b.event_data) = 'object'
    AND b.event_data ? 'OriginalBody'
    AND NOT (COALESCE(b.event_data ->> 'OriginalEventType', '') = ANY(v_kept_names));
  GET DIAGNOSTICS v_converted = ROW_COUNT;

  SELECT * INTO v_end
  FROM __SCHEMA__._wh_type_history_advance(
    c_operation, v_type, p_after, p_through, p_scan_size, p_rows_scanned, 0, v_converted, 0, v_kept);

  RETURN QUERY SELECT v_end.status, v_converted, v_kept, v_end.events_total, v_end.events_matched;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_audit_body_apply(TEXT, BIGINT, BIGINT, INTEGER, BIGINT, TEXT[]) IS
  'Issue #2326: one batch of the audit body conversion (history walk operation audit-body): in the window (p_after, '
  'p_through] of the EventAudited type''s commit sequences, removes OriginalBody from each record whose '
  'OriginalEventType is not in p_kept (the types that keep their bodies), leaving the pointer form.';

-- ---------------------------------------------------------------------------------------------
-- wh_pending_reclamation, last word 442_EphemeralPointerPruneDrainsItsBacklog.sql, copied whole, with one more task:
-- convert_audit_record_bodies.
-- ---------------------------------------------------------------------------------------------
-- Counted (each predicate copied from the task, each served by the index named):
--   purge_completed_perspective_events  processed_at IS NOT NULL (idx_perspective_events_processed, 031)
--   purge_old_deduplication             first_seen_at past dedup_retention_days (idx_message_dedup_first_seen)
--   purge_instance_evictions            evicted_at past instance_eviction_retention_hours (idx_instance_evictions_evicted_at)
--   prune_collective_ordering_heads     updated_at past collective_ordering_head_retention_days
--                                       (idx_collective_ordering_heads_updated_at)
--   purge_idle_routes                   last_seen past route_idle_days (idx_routes_last_seen)
--   prune_ancient_ephemeral_pointers    442 (#2327): the pointers the opt-in prune would remove once due, under its own
--                                       rule (idx_event_store_ephemeral_reap, created_at past the horizon); nothing
--                                       while the prune is disabled or under debug_mode
--   convert_audit_record_bodies         444 (#2326): the audit records the body conversion has yet to read, from its
--                                       walk's stored progress (wh_type_history_walks, operation audit-body); 0 once
--                                       its walk is complete
--   convert_metadata_history            441 (#2322): the event rows the historical metadata conversion has yet to read,
--                                       past its cursor (the wh_event_body key); 0 once the conversion is complete,
--                                       nothing while it is skipped (debug mode, switched off, or the Full form)
-- Not counted, because no index serves the predicate: purge_completed_outbox and purge_completed_inbox (every
-- processed_at index is partial on IS NULL), purge_recovered_dead_letters, reap_consumed_ephemeral_bodies (the
-- per-type grace and the TTL in body metadata leave the ephemeral index unbounded: pointers whose bodies were already
-- reaped stay in it), reap_expired_perspective_rows and the perspective row caps (expires_at is not indexed), and the
-- ledger trims of reap_unbounded_ledgers other than the routes.
-- Under debug_mode the tasks that skip under it report nothing, since they would remove nothing.
-- <docs>operations/observability/metrics#pending-reclamation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingReclamationSqlTests.cs:EachTask_CountsWhatItWouldRemove_AndNothingElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingReclamationSqlTests.cs:ACountPastTheCap_StopsAtTheCap_AndSaysSoAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingReclamationSqlTests.cs:UnderDebugMode_TheTasksThatSkip_ReportNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataHistoryConversionSqlTests.cs:Remaining_CountsTheRowsPastTheCursor_AndReachesZeroAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs:Pending_CountsWhatThePruneWouldRemove_OnlyWhileItIsEnabledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs:Step_ConvertsInBoundedBatches_Resumes_AndReportsWhatIsLeftAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_pending_reclamation(p_cap INTEGER)
RETURNS TABLE(task_name TEXT, pending BIGINT, capped BOOLEAN)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
  v_limit BIGINT := GREATEST(p_cap, 1)::BIGINT + 1;
  v_debug_mode BOOLEAN;
  v_dedup_retention_days INTEGER;
  v_instance_eviction_retention_hours INTEGER;
  v_ordering_head_retention_days INTEGER;
  v_route_days INTEGER;
  v_rows BIGINT;
  v_prune_enabled BOOLEAN;   -- 442
  v_prune_horizon INTEGER;   -- 442
BEGIN
  -- Every setting read as the maintenance cycle reads it, with the same default, so a count is what the next cycle
  -- would remove.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'), 30)
    INTO v_dedup_retention_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'instance_eviction_retention_hours'), 24)
    INTO v_instance_eviction_retention_hours;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'collective_ordering_head_retention_days'), 7)
    INTO v_ordering_head_retention_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'route_idle_days'), 365)
    INTO v_route_days;

  IF NOT v_debug_mode THEN
    SELECT count(*) INTO v_rows FROM (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NOT NULL LIMIT v_limit) x;
    RETURN QUERY SELECT 'purge_completed_perspective_events'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;
  END IF;

  SELECT count(*) INTO v_rows FROM (
    SELECT 1 FROM __SCHEMA__.wh_message_deduplication
    WHERE first_seen_at < NOW() - (v_dedup_retention_days || ' days')::INTERVAL LIMIT v_limit) x;
  RETURN QUERY SELECT 'purge_old_deduplication'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;

  SELECT count(*) INTO v_rows FROM (
    SELECT 1 FROM __SCHEMA__.wh_instance_evictions
    WHERE evicted_at < NOW() - (v_instance_eviction_retention_hours * INTERVAL '1 hour') LIMIT v_limit) x;
  RETURN QUERY SELECT 'purge_instance_evictions'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;

  SELECT count(*) INTO v_rows FROM (
    SELECT 1 FROM __SCHEMA__.wh_collective_ordering_heads
    WHERE updated_at < NOW() - (v_ordering_head_retention_days * INTERVAL '1 day') LIMIT v_limit) x;
  RETURN QUERY SELECT 'prune_collective_ordering_heads'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;

  IF NOT v_debug_mode THEN
    SELECT count(*) INTO v_rows FROM (
      SELECT 1 FROM __SCHEMA__.wh_routes
      WHERE last_seen < NOW() - (v_route_days * INTERVAL '1 day') LIMIT v_limit) x;
    RETURN QUERY SELECT 'purge_idle_routes'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;
  END IF;

  -- 442 (#2327): what the ephemeral pointer prune would remove once due, read as the prune reads its settings.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_deep_maintenance_enabled'), FALSE)
    INTO v_prune_enabled;
  SELECT GREATEST(
    COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_retention_days'), 90),
    COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'), 30))
    INTO v_prune_horizon;
  IF v_prune_enabled AND NOT v_debug_mode THEN
    SELECT count(*) INTO v_rows FROM (
      SELECT 1 FROM __SCHEMA__.wh_event_store es
      WHERE (es.flags & 8) = 8
        AND es.created_at < NOW() - (v_prune_horizon * INTERVAL '1 day')
        AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
        AND NOT EXISTS (
          SELECT 1 FROM __SCHEMA__.wh_perspective_events pe WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
        AND es.version < (SELECT MAX(es2.version) FROM __SCHEMA__.wh_event_store es2 WHERE es2.stream_id = es.stream_id)
      LIMIT v_limit) x;
    RETURN QUERY SELECT 'prune_ancient_ephemeral_pointers'::TEXT, LEAST(v_rows, v_limit - 1), v_rows = v_limit;
  END IF;

  -- 444 (#2326): what the audit body conversion has left to read, from its walk's progress; nothing before it starts.
  RETURN QUERY SELECT 'convert_audit_record_bodies'::TEXT,
      CASE WHEN h.completed_at IS NULL THEN GREATEST(h.events_total - h.events_matched, 0) ELSE 0 END, FALSE
    FROM __SCHEMA__.wh_type_history_walks h
    WHERE h.operation = 'audit-body';

  -- 441 (#2322): what the historical metadata conversion has left to read.
  RETURN QUERY SELECT 'convert_metadata_history'::TEXT, r.remaining, r.capped
    FROM __SCHEMA__.wh_metadata_history_remaining(p_cap) r;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_pending_reclamation(INTEGER) IS
  'The rows each index-served maintenance task would remove now, one bounded count each (at most p_cap + 1 rows '
  'read); capped means at least p_cap (#2289). Read by the pending-reclamation sampler, never by the maintenance cycle.';
