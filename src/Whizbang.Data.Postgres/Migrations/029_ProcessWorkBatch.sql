-- Migration: 029_ProcessWorkBatch.sql
-- Date: 2025-12-28
-- Description: Creates process_work_batch orchestrator function.
--              This is the single authoritative creation of process_work_batch.
--              (Migration 007 removed per pre-v1.0 consolidation rule)
--              Calls all decomposed functions in dependency order and returns aggregated results.
--              Uses log_event() function for tracking idempotent event conflicts.
-- Dependencies: 009-028 (foundation, completion, failure, storage, cleanup, claiming functions, and error tracking)

SELECT __SCHEMA__.drop_all_overloads('process_work_batch');

CREATE OR REPLACE FUNCTION __SCHEMA__.process_work_batch(
  -- Instance identification
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_metadata JSONB,

  -- Timing parameters
  p_now TIMESTAMPTZ,
  p_lease_duration_seconds INTEGER DEFAULT 300,

  -- Partitioning
  p_partition_count INTEGER DEFAULT 10000,

  -- Completions
  p_outbox_completions JSONB DEFAULT '[]'::JSONB,
  p_inbox_completions JSONB DEFAULT '[]'::JSONB,
  p_perspective_event_completions JSONB DEFAULT '[]'::JSONB,
  p_perspective_completions JSONB DEFAULT '[]'::JSONB,  -- Direct checkpoint completions (StreamId, PerspectiveName, LastEventId, Status)

  -- Failures
  p_outbox_failures JSONB DEFAULT '[]'::JSONB,
  p_inbox_failures JSONB DEFAULT '[]'::JSONB,
  p_perspective_event_failures JSONB DEFAULT '[]'::JSONB,
  p_perspective_failures JSONB DEFAULT '[]'::JSONB,  -- Direct checkpoint failures (StreamId, PerspectiveName, LastEventId, Status, Error)

  -- Storage (new work)
  p_new_outbox_messages JSONB DEFAULT '[]'::JSONB,
  p_new_inbox_messages JSONB DEFAULT '[]'::JSONB,
  p_new_perspective_events JSONB DEFAULT '[]'::JSONB,

  -- Lease renewals
  p_renew_outbox_lease_ids JSONB DEFAULT '[]'::JSONB,
  p_renew_inbox_lease_ids JSONB DEFAULT '[]'::JSONB,
  p_renew_perspective_event_lease_ids JSONB DEFAULT '[]'::JSONB,

  -- Flags
  p_flags INTEGER DEFAULT 0,

  -- Thresholds
  p_stale_threshold_seconds INTEGER DEFAULT 30,

  -- Sync inquiries (for perspective sync awaiter)
  p_sync_inquiries JSONB DEFAULT '[]'::JSONB,

  -- Maximum streams to return per batch (configurable, default 300)
  p_max_streams INTEGER DEFAULT 300
) RETURNS TABLE(
  -- Heartbeat results
  instance_rank INTEGER,
  active_instance_count INTEGER,

  -- Work results (unified format)
  source VARCHAR(20),           -- 'outbox', 'inbox', 'receptor', 'perspective'
  work_id UUID,                 -- message_id or event_work_id or processing_id
  work_stream_id UUID,          -- Renamed from stream_id to avoid PL/pgSQL ambiguity
  partition_number INTEGER,     -- Partition assignment for load balancing
  destination VARCHAR(200),     -- Topic name (outbox) or handler name (inbox)
  message_type VARCHAR(500),    -- For outbox/inbox
  envelope_type VARCHAR(500),   -- Assembly-qualified name of envelope type (for outbox only)
  message_data TEXT,
  metadata JSONB,
  status INTEGER,               -- MessageProcessingStatus flags
  attempts INTEGER,
  is_newly_stored BOOLEAN,
  is_orphaned BOOLEAN,

  -- Error tracking (for failed storage operations)
  error TEXT,                   -- Error message (NULL if no error)
  failure_reason INTEGER,       -- MessageFailureReason enum value (NULL if no failure)

  -- Perspective-specific fields (NULL for non-perspective work)
  perspective_name VARCHAR(200)
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  -- Stream lease is only refreshed when the existing expiry is within one-third
  -- of p_lease_duration_seconds from now. Renewing every tick on every owned
  -- stream generated one dead tuple per stream per tick in production (JDX dev
  -- slot 3: 1.8B lifetime updates on 5,790 rows) and dominated WAL pressure.
  -- Orphan-claim SLA is unchanged because streams still expire at lease_expiry.
  v_refresh_threshold TIMESTAMPTZ;
  v_stale_cutoff TIMESTAMPTZ;
  v_rank INTEGER;
  v_count INTEGER;
  v_completed_events JSONB;
  v_completion RECORD;

  -- Batch limits: derived from p_max_streams parameter (no longer read from wh_settings)
  v_max_work_items INTEGER;
  v_max_work_items_per_stream INTEGER;

  -- Arrays to track successfully stored events (for Phase 4.6 and 4.7 filtering)
  v_stored_outbox_events UUID[] := '{}';
  v_stored_inbox_events UUID[] := '{}';

  -- Conflict tracking for logging
  v_outbox_conflict_count INTEGER := 0;
  v_outbox_conflict_types TEXT[];
  v_inbox_conflict_count INTEGER := 0;
  v_inbox_conflict_types TEXT[];

  -- Acknowledgement counts for completion tracking
  v_ack_counts JSONB;

  -- Boolean flags to avoid re-querying for ack count placement
  v_has_outbox_work BOOLEAN := false;
  v_has_inbox_work BOOLEAN := false;

  -- Rewind debounce: how long to hold back rewind-pending streams (seconds)
  v_rewind_debounce_seconds INTEGER;
  v_rewind_max_debounce_seconds INTEGER;

  -- Two-tier budget split
  v_tier1_budget_percent INTEGER;
  v_tier1_max INTEGER;

  -- JSON field-name constants. These mirror the C# contract names used when serializing
  -- work-batch DTOs; centralizing them keeps ->> lookups consistent across the function
  -- and satisfies sonar plsql:S1192 (duplicate-literal) without altering behaviour.
  c_field_stream_id CONSTANT TEXT := 'StreamId';
  c_field_perspective_name CONSTANT TEXT := 'PerspectiveName';
  c_field_message_id CONSTANT TEXT := 'MessageId';
  c_field_hops CONSTANT TEXT := 'Hops';
  c_field_event_ids CONSTANT TEXT := 'EventIds';
  c_field_event_type_filter CONSTANT TEXT := 'EventTypeFilter';
  c_source_outbox CONSTANT TEXT := 'outbox';
  c_source_inbox CONSTANT TEXT := 'inbox';
  c_source_perspective CONSTANT TEXT := 'perspective';
  c_interval_one_hour CONSTANT TEXT := '1 hour';
BEGIN
  -- Set batch limits from p_max_streams parameter (unified budget for total and per-stream)
  v_max_work_items := p_max_streams;
  v_max_work_items_per_stream := p_max_streams;

  -- Read remaining settings in a single query (pivoted)
  SELECT
    COALESCE(MAX(CASE WHEN setting_key = 'rewind_debounce_seconds' THEN setting_value::INTEGER END), 5),
    COALESCE(MAX(CASE WHEN setting_key = 'rewind_max_debounce_seconds' THEN setting_value::INTEGER END), 30),
    COALESCE(MAX(CASE WHEN setting_key = 'tier1_budget_percent' THEN setting_value::INTEGER END), 70)
  INTO v_rewind_debounce_seconds, v_rewind_max_debounce_seconds, v_tier1_budget_percent
  FROM wh_settings
  WHERE setting_key IN ('rewind_debounce_seconds', 'rewind_max_debounce_seconds', 'tier1_budget_percent');

  -- Calculate tier 1 budget cap (Tier 2 gets the remainder + any unused Tier 1 slots)
  v_tier1_max := (v_max_work_items * v_tier1_budget_percent) / 100;

  -- Calculate lease expiry, refresh threshold, and stale cutoff
  v_lease_expiry := p_now + (p_lease_duration_seconds || ' seconds')::INTERVAL;
  v_refresh_threshold := p_now + ((p_lease_duration_seconds / 3) || ' seconds')::INTERVAL;
  v_stale_cutoff := p_now - (p_stale_threshold_seconds || ' seconds')::INTERVAL;

  -- Create temporary tables for tracking work
  CREATE TEMP TABLE IF NOT EXISTS temp_completed_perspectives (
    stream_id UUID,
    perspective_name VARCHAR(200),
    PRIMARY KEY (stream_id, perspective_name)
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_new_outbox (
    message_id UUID PRIMARY KEY,
    stream_id UUID
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_new_inbox (
    message_id UUID PRIMARY KEY,
    stream_id UUID
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_new_perspective_events (
    event_work_id UUID PRIMARY KEY,
    stream_id UUID,
    perspective_name VARCHAR(200)
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_orphaned_outbox (
    message_id UUID PRIMARY KEY,
    stream_id UUID
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_orphaned_inbox (
    message_id UUID PRIMARY KEY,
    stream_id UUID
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_orphaned_receptor (
    processing_id UUID PRIMARY KEY,
    stream_id UUID
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_orphaned_perspective_events (
    event_work_id UUID PRIMARY KEY,
    stream_id UUID,
    perspective_name VARCHAR(200)
  ) ON COMMIT DROP;

  CREATE TEMP TABLE IF NOT EXISTS temp_sync_results (
    inquiry_id UUID PRIMARY KEY,
    stream_id UUID,
    pending_count INTEGER,
    processed_count INTEGER,
    pending_event_ids UUID[],
    processed_event_ids UUID[]
  ) ON COMMIT DROP;

  -- ========================================
  -- Phase 1: Foundation (Heartbeat & Cleanup)
  -- ========================================

  -- Register heartbeat and get rank
  PERFORM __SCHEMA__.register_instance_heartbeat(
    p_instance_id,
    p_service_name,
    p_host_name,
    p_process_id,
    p_metadata,
    p_now,
    v_lease_expiry
  );

  -- Cleanup stale instances
  PERFORM __SCHEMA__.cleanup_stale_instances(v_stale_cutoff);

  -- Calculate rank
  SELECT cir.instance_rank, cir.active_instance_count INTO v_rank, v_count
  FROM __SCHEMA__.calculate_instance_rank(p_instance_id, v_stale_cutoff) AS cir;

  -- Cleanup completed streams (only when completions were processed)
  IF jsonb_array_length(p_outbox_completions) > 0
    OR jsonb_array_length(p_inbox_completions) > 0
    OR jsonb_array_length(p_perspective_event_completions) > 0 THEN
    PERFORM __SCHEMA__.cleanup_completed_streams(p_now);
  END IF;

  -- ========================================
  -- Phase 2: Completions
  -- ========================================

  -- Process outbox completions
  IF jsonb_array_length(p_outbox_completions) > 0 THEN
    PERFORM __SCHEMA__.process_outbox_completions(p_outbox_completions, p_now, (p_flags & 4) != 0);
  END IF;

  -- Process inbox completions
  IF jsonb_array_length(p_inbox_completions) > 0 THEN
    PERFORM __SCHEMA__.process_inbox_completions(p_inbox_completions, p_now, (p_flags & 4) != 0);
  END IF;

  -- Process perspective event completions: CRITICAL ORDER
  -- 1. Mark events as processed (set processed_at and status)
  -- 2. Collect stream/perspective pairs for checkpoint updates
  -- 3. Update checkpoints WHILE events still exist
  -- 4. Delete processed events (ephemeral pattern)

  -- Step 1 & 2: Mark as processed and collect completion info
  -- Use debug mode temporarily to prevent deletion
  INSERT INTO temp_completed_perspectives (stream_id, perspective_name)
  SELECT DISTINCT
    pec.stream_id,
    pec.perspective_name
  FROM __SCHEMA__.process_perspective_event_completions(
    p_perspective_event_completions,
    p_now,
    TRUE  -- Always use debug mode initially to retain events for checkpoint update
  ) AS pec
  WHERE pec.stream_id IS NOT NULL
    AND pec.perspective_name IS NOT NULL
  ON CONFLICT DO NOTHING;

  -- Step 3: Update perspective checkpoints BEFORE deleting events
  v_completed_events := (
    SELECT jsonb_agg(
      jsonb_build_object(
        c_field_stream_id, tcp.stream_id,
        c_field_perspective_name, tcp.perspective_name
      )
    )
    FROM temp_completed_perspectives tcp
  );

  IF v_completed_events IS NOT NULL THEN
    PERFORM __SCHEMA__.update_perspective_cursors(v_completed_events, (p_flags & 4) != 0);
  END IF;

  -- Step 4: Delete processed events (if not in debug mode)
  -- Now safe to delete since checkpoints are already updated
  IF (p_flags & 4) = 0 THEN
    DELETE FROM wh_perspective_events pe
    WHERE pe.processed_at IS NOT NULL
      AND (pe.stream_id, pe.perspective_name) IN (
        SELECT tcp.stream_id, tcp.perspective_name
        FROM temp_completed_perspectives tcp
      );
  END IF;

  -- Debug mode cleanup: Purge completed messages older than 1 hour to prevent table bloat
  -- This gives developers time to inspect recent completions while keeping tables bounded
  IF (p_flags & 4) != 0 THEN
    DELETE FROM wh_outbox WHERE processed_at IS NOT NULL AND processed_at < p_now - (c_interval_one_hour)::INTERVAL;
    DELETE FROM wh_inbox WHERE processed_at IS NOT NULL AND processed_at < p_now - (c_interval_one_hour)::INTERVAL;
    DELETE FROM wh_perspective_events WHERE processed_at IS NOT NULL AND processed_at < p_now - (c_interval_one_hour)::INTERVAL;
  END IF;

  -- Process perspective checkpoint completions (direct completion reports from perspective runners)
  IF jsonb_array_length(p_perspective_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>c_field_stream_id)::UUID as stream_id,
        elem->>c_field_perspective_name as perspective_name,
        (elem->>'LastEventId')::UUID as last_event_id,
        elem->'ProcessedEventIds' as processed_event_ids_json,
        (elem->>'Status')::SMALLINT as status
      FROM jsonb_array_elements(p_perspective_completions) as elem
    LOOP
      IF v_completion.last_event_id != '00000000-0000-0000-0000-000000000000'::UUID THEN
        PERFORM __SCHEMA__.complete_perspective_cursor_work(
          v_completion.stream_id,
          v_completion.perspective_name,
          v_completion.last_event_id,
          COALESCE(v_completion.processed_event_ids_json, '[]'::JSONB),
          v_completion.status,
          NULL::TEXT
        );
      END IF;
    END LOOP;
  END IF;

  -- ========================================
  -- Phase 3: Failures
  -- ========================================

  -- Process outbox failures
  IF jsonb_array_length(p_outbox_failures) > 0 THEN
    PERFORM __SCHEMA__.process_outbox_failures(p_outbox_failures, p_now);
  END IF;

  -- Process inbox failures
  IF jsonb_array_length(p_inbox_failures) > 0 THEN
    PERFORM __SCHEMA__.process_inbox_failures(p_inbox_failures, p_now);
  END IF;

  -- Process perspective event failures
  IF jsonb_array_length(p_perspective_event_failures) > 0 THEN
    PERFORM __SCHEMA__.process_perspective_event_failures(p_perspective_event_failures, p_now);
  END IF;

  -- Process perspective checkpoint failures (direct failure reports from perspective runners)
  IF jsonb_array_length(p_perspective_failures) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>c_field_stream_id)::UUID as stream_id,
        elem->>c_field_perspective_name as perspective_name,
        (elem->>'LastEventId')::UUID as last_event_id,
        elem->'ProcessedEventIds' as processed_event_ids_json,
        (elem->>'Status')::SMALLINT as status,
        elem->>'Error' as error_message
      FROM jsonb_array_elements(p_perspective_failures) as elem
    LOOP
      IF v_completion.last_event_id != '00000000-0000-0000-0000-000000000000'::UUID THEN
        PERFORM __SCHEMA__.complete_perspective_cursor_work(
          v_completion.stream_id,
          v_completion.perspective_name,
          v_completion.last_event_id,
          COALESCE(v_completion.processed_event_ids_json, '[]'::JSONB),
          v_completion.status,
          v_completion.error_message
        );
      END IF;
    END LOOP;
  END IF;

  -- ========================================
  -- Phase 2.5: Calculate Acknowledgement Counts
  -- ========================================
  -- Count how many completions/failures were processed
  -- These counts are returned in metadata to C# for acknowledgement tracking

  v_ack_counts := jsonb_build_object(
    'outbox_completions_processed', jsonb_array_length(COALESCE(p_outbox_completions, '[]'::JSONB)),
    'outbox_failures_processed', jsonb_array_length(COALESCE(p_outbox_failures, '[]'::JSONB)),
    'inbox_completions_processed', jsonb_array_length(COALESCE(p_inbox_completions, '[]'::JSONB)),
    'inbox_failures_processed', jsonb_array_length(COALESCE(p_inbox_failures, '[]'::JSONB)),
    'perspective_completions_processed', jsonb_array_length(COALESCE(p_perspective_completions, '[]'::JSONB)),
    'perspective_failures_processed', jsonb_array_length(COALESCE(p_perspective_failures, '[]'::JSONB)),
    'outbox_lease_renewals_processed', jsonb_array_length(COALESCE(p_renew_outbox_lease_ids, '[]'::JSONB)),
    'inbox_lease_renewals_processed', jsonb_array_length(COALESCE(p_renew_inbox_lease_ids, '[]'::JSONB))
  );

  -- ========================================
  -- Phase 2.6: Sync Inquiries
  -- ========================================
  -- Process sync inquiries to check if perspectives have processed specific events.
  -- Used by PerspectiveSyncAwaiter to implement read-your-writes consistency.
  --
  -- Two modes:
  -- 1. Explicit EventIds mode: Check if specific events have been processed
  -- 2. Discovery mode (DiscoverPendingFromOutbox=true): Find events of specified types
  --    from wh_event_store that haven't been processed by the perspective yet

  IF jsonb_array_length(COALESCE(p_sync_inquiries, '[]'::JSONB)) > 0 THEN
    INSERT INTO temp_sync_results (inquiry_id, stream_id, pending_count, processed_count, pending_event_ids, processed_event_ids)
    SELECT
      inquiry_id,
      stream_id,
      pending_count,
      processed_count,
      pending_event_ids,
      processed_event_ids
    FROM (
      SELECT
        (inquiry->>'InquiryId')::UUID as inquiry_id,
        (inquiry->>c_field_stream_id)::UUID as stream_id,
        -- Count events that exist in event store but not processed by perspective
        COUNT(es.event_id) FILTER (WHERE pe.processed_at IS NULL)::INTEGER as pending_count,
        COUNT(es.event_id) FILTER (WHERE pe.processed_at IS NOT NULL)::INTEGER as processed_count,
        CASE
          WHEN (inquiry->>'IncludePendingEventIds')::BOOLEAN = true
          THEN ARRAY_AGG(es.event_id) FILTER (WHERE pe.processed_at IS NULL)
          ELSE NULL
        END as pending_event_ids,
        -- Return processed event IDs when IncludeProcessedEventIds is true
        -- Also returns discovered event IDs when DiscoverPendingFromOutbox is true
        CASE
          WHEN (inquiry->>'IncludeProcessedEventIds')::BOOLEAN = true
          THEN ARRAY_AGG(es.event_id) FILTER (WHERE pe.processed_at IS NOT NULL)
          ELSE NULL
        END as processed_event_ids
      FROM jsonb_array_elements(p_sync_inquiries) as inquiry
      -- Start from event store to discover ALL events (processed or not)
      -- This is the key change: we query wh_event_store first, then LEFT JOIN to perspective_events
      LEFT JOIN wh_event_store es
        ON es.stream_id = (inquiry->>c_field_stream_id)::UUID
        AND (
          -- If EventIds is provided, filter to only those events
          (inquiry->c_field_event_ids) IS NULL
          OR jsonb_array_length(inquiry->c_field_event_ids) = 0
          OR es.event_id = ANY(
            ARRAY(SELECT (jsonb_array_elements_text(inquiry->c_field_event_ids))::UUID)
          )
        )
        AND (
          -- If EventTypeFilter is provided, filter by event type
          (inquiry->c_field_event_type_filter) IS NULL
          OR jsonb_array_length(inquiry->c_field_event_type_filter) = 0
          OR es.event_type = ANY(
            ARRAY(SELECT jsonb_array_elements_text(inquiry->c_field_event_type_filter))
          )
        )
      -- LEFT JOIN to perspective_events to check which events have been processed
      LEFT JOIN wh_perspective_events pe
        ON pe.event_id = es.event_id
        AND pe.perspective_name = inquiry->>c_field_perspective_name
      WHERE
        -- When DiscoverPendingFromOutbox is true, we require events to exist in event store
        -- When false (explicit EventIds mode), we allow the old behavior
        CASE
          WHEN (inquiry->>'DiscoverPendingFromOutbox')::BOOLEAN = true THEN
            es.event_id IS NOT NULL  -- Require events to exist
          ELSE
            true  -- Allow empty results for backwards compatibility
        END
      GROUP BY inquiry->>'InquiryId', inquiry->>c_field_stream_id, inquiry->>'IncludePendingEventIds', inquiry->>'IncludeProcessedEventIds'
    ) subq;
  END IF;

  -- ========================================
  -- Phase 4: Storage (New Work)
  -- ========================================

  -- Store new outbox messages and track
  IF jsonb_array_length(p_new_outbox_messages) > 0 THEN
    INSERT INTO temp_new_outbox (message_id, stream_id)
    SELECT som.message_id, som.stream_id
    FROM __SCHEMA__.store_outbox_messages(
      p_new_outbox_messages,
      p_instance_id,
      v_lease_expiry,
      p_now,
      p_partition_count
    ) AS som
    WHERE som.was_newly_created = true;

    -- DIAGNOSTIC: Log how many new outbox messages were stored
    IF (p_flags & 4) != 0 THEN
      RAISE DEBUG '[process_work_batch] Stored % new outbox messages (instance_id=%)',
        (SELECT COUNT(*) FROM temp_new_outbox), p_instance_id;
    END IF;
  END IF;

  -- Store new inbox messages and track
  IF jsonb_array_length(p_new_inbox_messages) > 0 THEN
    INSERT INTO temp_new_inbox (message_id, stream_id)
    SELECT sim.message_id, sim.stream_id
    FROM __SCHEMA__.store_inbox_messages(
      p_new_inbox_messages,
      p_instance_id,
      v_lease_expiry,
      p_now,
      p_partition_count
    ) AS sim
    WHERE sim.was_newly_created = true;

    -- DIAGNOSTIC: Log how many new inbox messages were tracked
    IF (p_flags & 4) != 0 THEN
      RAISE DEBUG '[Phase 4] Stored % new inbox messages to temp_new_inbox (instance_id=%)',
        (SELECT COUNT(*) FROM temp_new_inbox), p_instance_id;
    END IF;
  END IF;

  -- Store new perspective events and track
  IF jsonb_array_length(p_new_perspective_events) > 0 THEN
    INSERT INTO temp_new_perspective_events (event_work_id, stream_id, perspective_name)
    SELECT spe.event_work_id, spe.stream_id, spe.perspective_name
    FROM __SCHEMA__.store_perspective_events(
      p_new_perspective_events,
      p_instance_id,
      v_lease_expiry,
      p_now
    ) AS spe
    WHERE spe.was_newly_created = true;
  END IF;

  -- ========================================
  -- Phase 5: Claiming (Orphaned Work)
  -- ========================================
  -- MOVED BEFORE Phase 4.5 so that claimed inbox events get stored to wh_event_store
  -- and have perspective events/cursors created in Phase 4.5B/4.6/4.7 (self-healing).
  -- This enables the "drop and walk away" inbox pattern where TransportConsumerWorker
  -- only INSERTs into wh_inbox, and the next tick's claiming + event storage handles the rest.

  -- Claim orphaned outbox and track
  -- v_stale_cutoff (computed above from p_stale_threshold_seconds) is passed so the claim
  -- treats non-heartbeating instances as abandoned; their wh_active_streams leases no
  -- longer block cross-instance claims. See migration 025 comment for the full rationale.
  INSERT INTO temp_orphaned_outbox (message_id, stream_id)
  SELECT coo.message_id, coo.stream_id
  FROM __SCHEMA__.claim_orphaned_outbox(
    p_instance_id,
    v_rank,
    v_count,
    v_lease_expiry,
    p_now,
    p_partition_count,
    v_stale_cutoff
  ) AS coo;

  -- Claim orphaned inbox and track (skip when SkipInboxClaiming flag is set — bit 6 = 64)
  IF (p_flags & 64) = 0 THEN
    INSERT INTO temp_orphaned_inbox (message_id, stream_id)
    SELECT coi.message_id, coi.stream_id
    FROM __SCHEMA__.claim_orphaned_inbox(
      p_instance_id,
      v_rank,
      v_count,
      v_lease_expiry,
      p_now,
      p_partition_count,
      v_stale_cutoff
    ) AS coi;
  END IF;

  -- wh_active_streams refresh for every stream that touched this tick (orphan claims
  -- + new outbox + new inbox) happens in a single batched, sorted UPSERT below. This
  -- replaces the previous pattern of several small UPDATEs inside process_work_batch
  -- AND inside store_outbox_messages / store_inbox_messages, which could deadlock
  -- when two concurrent ticks held overlapping subsets of rows in different orders.
  --
  -- Design: one statement, deterministic stream_id-sorted row-lock acquisition via the
  -- preceding SELECT … FOR UPDATE fence, instance claims ownership for streams that
  -- appear in an orphan claim or new outbox, refreshes lease only for new-inbox-only
  -- streams. See plans/we-need-to-double-quiet-fern.md.
  CREATE TEMP TABLE IF NOT EXISTS temp_stream_refresh (
    stream_id UUID PRIMARY KEY,
    claim_owner BOOLEAN NOT NULL
  ) ON COMMIT DROP;
  TRUNCATE temp_stream_refresh;

  INSERT INTO temp_stream_refresh (stream_id, claim_owner)
  SELECT sa.stream_id, bool_or(sa.claim_owner)
  FROM (
    SELECT stream_id, true  AS claim_owner FROM temp_orphaned_outbox WHERE stream_id IS NOT NULL
    UNION ALL
    SELECT stream_id, true                    FROM temp_orphaned_inbox  WHERE stream_id IS NOT NULL
    UNION ALL
    SELECT stream_id, true                    FROM temp_new_outbox      WHERE stream_id IS NOT NULL
    UNION ALL
    SELECT stream_id, false                   FROM temp_new_inbox       WHERE stream_id IS NOT NULL
  ) sa
  GROUP BY sa.stream_id;

  IF EXISTS (SELECT 1 FROM temp_stream_refresh) THEN
    -- Deadlock-safety fence: pre-acquire row locks on existing wh_active_streams rows
    -- in stream_id-sorted order. Two concurrent ticks with overlapping refresh sets
    -- serialize here without cycling.
    PERFORM 1
    FROM __SCHEMA__.wh_active_streams
    WHERE stream_id IN (SELECT stream_id FROM temp_stream_refresh)
    ORDER BY stream_id
    FOR UPDATE;

    -- One batched, sorted UPSERT. assigned_instance_id semantics:
    --   claim_owner = true  → this instance claims (orphan claim or new outbox).
    --   claim_owner = false → preserve existing ownership (new-inbox-only stream).
    -- lease_expiry is always bumped to GREATEST(existing, this tick's).
    INSERT INTO __SCHEMA__.wh_active_streams
      (stream_id, assigned_instance_id, lease_expiry, partition_number, last_activity_at)
    SELECT
      tsr.stream_id,
      CASE WHEN tsr.claim_owner THEN p_instance_id ELSE NULL END,
      v_lease_expiry,
      __SCHEMA__.compute_partition(tsr.stream_id, p_partition_count),
      p_now
    FROM temp_stream_refresh tsr
    ORDER BY tsr.stream_id
    ON CONFLICT ON CONSTRAINT wh_active_streams_pkey DO UPDATE SET
      assigned_instance_id = COALESCE(EXCLUDED.assigned_instance_id, __SCHEMA__.wh_active_streams.assigned_instance_id),
      lease_expiry         = GREATEST(__SCHEMA__.wh_active_streams.lease_expiry, EXCLUDED.lease_expiry),
      last_activity_at     = EXCLUDED.last_activity_at;
  END IF;

  -- Claim orphaned receptor work and track
  INSERT INTO temp_orphaned_receptor (processing_id, stream_id)
  SELECT cor.processing_id, cor.stream_id
  FROM __SCHEMA__.claim_orphaned_receptor_work(
    p_instance_id,
    v_rank,
    v_count,
    v_lease_expiry,
    p_now
  ) AS cor;

  -- Claim orphaned perspective events and track (full-stream capture with message budget)
  INSERT INTO temp_orphaned_perspective_events (event_work_id, stream_id, perspective_name)
  SELECT cope.event_work_id, cope.stream_id, cope.perspective_name
  FROM __SCHEMA__.claim_orphaned_perspective_events(
    p_instance_id,
    v_lease_expiry,
    p_now,
    v_max_work_items  -- Pass message budget (overridden by p_max_perspective_streams if set)
  ) AS cope;

  -- ========================================
  -- Phase 4.5: Event Storage
  -- ========================================
  -- Store events from newly created AND newly claimed outbox/inbox messages to wh_event_store
  -- with sequential versioning and optimistic concurrency control.
  -- This is the authoritative event storage - all events flow through process_work_batch.
  -- Uses array tracking to capture successfully stored events for Phase 4.6/4.7 filtering.
  -- Includes orphaned messages for self-healing (crash recovery + inbox drop-and-walk-away).

  -- Phase 4.5A: Store events from outbox messages with tracking
  WITH outbox_events AS (
    SELECT
      o.message_id,
      o.stream_id,
      o.message_type,
      o.event_data,
      o.metadata,
      o.scope,
      o.created_at,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.created_at) as row_num
    FROM wh_outbox o
    WHERE o.message_id IN (
        SELECT message_id FROM temp_new_outbox
        UNION ALL
        SELECT message_id FROM temp_orphaned_outbox
      )
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  outbox_stream_versions AS (
    -- Single aggregate scan for all streams instead of correlated subquery per row
    SELECT es.stream_id, MAX(es.version) as max_version
    FROM wh_event_store es
    WHERE es.stream_id IN (SELECT DISTINCT stream_id FROM outbox_events)
    GROUP BY es.stream_id
  ),
  outbox_base_versions AS (
    SELECT
      oe.stream_id,
      oe.message_id,
      oe.message_type,
      oe.event_data,
      oe.metadata,
      oe.scope,
      oe.created_at,
      oe.row_num,
      COALESCE(sv.max_version, 0) as base_version
    FROM outbox_events oe
    LEFT JOIN outbox_stream_versions sv ON sv.stream_id = oe.stream_id
  ),
  stored_events AS (
    INSERT INTO wh_event_store (
      event_id,
      stream_id,
      aggregate_id,
      aggregate_type,
      event_type,
      event_data,
      metadata,
      scope,
      version,
      created_at
    )
    SELECT
      bv.message_id as event_id,
      bv.stream_id,
      bv.stream_id as aggregate_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(bv.message_type), ',', 1) as aggregate_type,
      __SCHEMA__.normalize_event_type(bv.message_type),
      -- Extract just the Payload from the envelope for event_data
      -- Handle short names (p), PascalCase (Payload), and camelCase (payload)
      COALESCE(bv.event_data::jsonb -> 'p', bv.event_data::jsonb -> 'Payload', bv.event_data::jsonb -> 'payload') as event_data,
      -- Build EnvelopeMetadata structure (PascalCase keys for System.Text.Json compatibility)
      -- Handle short names (id, h), PascalCase, and camelCase input from serialization
      jsonb_build_object(
        c_field_message_id, COALESCE(bv.event_data::jsonb -> 'id', bv.event_data::jsonb -> c_field_message_id, bv.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(bv.event_data::jsonb -> 'h', bv.event_data::jsonb -> c_field_hops, bv.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) as metadata,
      bv.scope,
      bv.base_version + bv.row_num as version,
      p_now
    FROM outbox_base_versions bv
    ON CONFLICT (event_id) DO NOTHING
    RETURNING event_id, event_type
  ),
  conflict_events AS (
    -- Find events that conflicted (were skipped due to idempotency)
    SELECT
      bv.message_id,
      bv.message_type
    FROM outbox_base_versions bv
    WHERE NOT EXISTS (
      SELECT 1 FROM stored_events se WHERE se.event_id = bv.message_id
    )
  )
  SELECT
    array_agg(se.event_id),
    (SELECT COUNT(*) FROM conflict_events),
    (SELECT array_agg(DISTINCT ce.message_type) FROM conflict_events ce)
  INTO v_stored_outbox_events, v_outbox_conflict_count, v_outbox_conflict_types
  FROM stored_events se;

  -- Ensure array is never NULL
  v_stored_outbox_events := COALESCE(v_stored_outbox_events, '{}');

  -- Log warnings for idempotent conflicts (if any)
  -- TODO: Implement log_event() function for tracking idempotent conflicts
  -- IF v_outbox_conflict_count > 0 THEN
  --   PERFORM __SCHEMA__.log_event(
  --     2,  -- Warning level
  --     'process_work_batch',
  --     format('Event already exists (idempotent): %s outbox events skipped', v_outbox_conflict_count),
  --     NULL,  -- No specific event_id (multiple)
  --     NULL,  -- No specific message_id
  --     NULL,  -- No specific event_type
  --     jsonb_build_object(
  --       'phase', '4.5A',
  --       'source', 'outbox',
  --       'skipped_count', v_outbox_conflict_count,
  --       'event_types', v_outbox_conflict_types
  --     )
  --   );
  -- END IF;

  -- Phase 4.5B: Store events from inbox messages with tracking
  IF (p_flags & 4) != 0 THEN
    RAISE DEBUG '[Phase 4.5B] Checking inbox events from temp_new_inbox';
    RAISE DEBUG '[Phase 4.5B] Total temp_new_inbox count: %', (SELECT COUNT(*) FROM temp_new_inbox);
    RAISE DEBUG '[Phase 4.5B] Inbox events matching criteria (is_event=true AND stream_id IS NOT NULL): %',
      (SELECT COUNT(*) FROM wh_inbox i
       WHERE i.message_id IN (SELECT message_id FROM temp_new_inbox)
         AND i.is_event = true
         AND i.stream_id IS NOT NULL);
  END IF;

  WITH inbox_events AS (
    SELECT
      i.message_id,
      i.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.received_at,
      ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at) as row_num
    FROM wh_inbox i
    WHERE i.message_id IN (
        SELECT message_id FROM temp_new_inbox
        UNION ALL
        SELECT message_id FROM temp_orphaned_inbox
      )
      AND i.is_event = true
      AND i.stream_id IS NOT NULL
  ),
  inbox_stream_versions AS (
    -- Single aggregate scan for all streams instead of correlated subquery per row
    SELECT es.stream_id, MAX(es.version) as max_version
    FROM wh_event_store es
    WHERE es.stream_id IN (SELECT DISTINCT stream_id FROM inbox_events)
    GROUP BY es.stream_id
  ),
  inbox_base_versions AS (
    SELECT
      ie.stream_id,
      ie.message_id,
      ie.message_type,
      ie.event_data,
      ie.metadata,
      ie.scope,
      ie.received_at,
      ie.row_num,
      COALESCE(sv.max_version, 0) as base_version
    FROM inbox_events ie
    LEFT JOIN inbox_stream_versions sv ON sv.stream_id = ie.stream_id
  ),
  stored_events AS (
    INSERT INTO wh_event_store (
      event_id,
      stream_id,
      aggregate_id,
      aggregate_type,
      event_type,
      event_data,
      metadata,
      scope,
      version,
      created_at
    )
    SELECT
      bv.message_id as event_id,
      bv.stream_id,
      bv.stream_id as aggregate_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(bv.message_type), ',', 1) as aggregate_type,
      __SCHEMA__.normalize_event_type(bv.message_type),
      -- Extract just the Payload from the envelope for event_data
      -- Handle short names (p), PascalCase (Payload), and camelCase (payload)
      COALESCE(bv.event_data::jsonb -> 'p', bv.event_data::jsonb -> 'Payload', bv.event_data::jsonb -> 'payload') as event_data,
      -- Build EnvelopeMetadata structure (PascalCase keys for System.Text.Json compatibility)
      -- Handle short names (id, h), PascalCase, and camelCase input from serialization
      jsonb_build_object(
        c_field_message_id, COALESCE(bv.event_data::jsonb -> 'id', bv.event_data::jsonb -> c_field_message_id, bv.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(bv.event_data::jsonb -> 'h', bv.event_data::jsonb -> c_field_hops, bv.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) as metadata,
      bv.scope,
      bv.base_version + bv.row_num as version,
      p_now
    FROM inbox_base_versions bv
    ON CONFLICT (event_id) DO NOTHING
    RETURNING event_id, event_type
  ),
  conflict_events AS (
    -- Find events that conflicted (were skipped due to idempotency)
    SELECT
      bv.message_id,
      bv.message_type
    FROM inbox_base_versions bv
    WHERE NOT EXISTS (
      SELECT 1 FROM stored_events se WHERE se.event_id = bv.message_id
    )
  )
  SELECT
    array_agg(se.event_id),
    (SELECT COUNT(*) FROM conflict_events),
    (SELECT array_agg(DISTINCT ce.message_type) FROM conflict_events ce)
  INTO v_stored_inbox_events, v_inbox_conflict_count, v_inbox_conflict_types
  FROM stored_events se;

  -- Ensure array is never NULL
  v_stored_inbox_events := COALESCE(v_stored_inbox_events, '{}');

  -- DIAGNOSTIC: Log storage results
  IF (p_flags & 4) != 0 THEN
    RAISE DEBUG '[Phase 4.5B] Stored % inbox events to wh_event_store', array_length(v_stored_inbox_events, 1);
    RAISE DEBUG '[Phase 4.5B] Conflict count: %', v_inbox_conflict_count;
  END IF;

  -- Log warnings for idempotent conflicts (if any)
  -- TODO: Implement log_event() function for tracking idempotent conflicts
  -- IF v_inbox_conflict_count > 0 THEN
  --   PERFORM __SCHEMA__.log_event(
  --     2,  -- Warning level
  --     'process_work_batch',
  --     format('Event already exists (idempotent): %s inbox events skipped', v_inbox_conflict_count),
  --     NULL,  -- No specific event_id (multiple)
  --     NULL,  -- No specific message_id
  --     NULL,  -- No specific event_type
  --     jsonb_build_object(
  --       'phase', '4.5B',
  --       'source', 'inbox',
  --       'skipped_count', v_inbox_conflict_count,
  --       'event_types', v_inbox_conflict_types
  --     )
  --   );
  -- END IF;

  -- ========================================
  -- Phase 4.6: Auto-Create Perspective Events
  -- ========================================
  -- When events are stored, automatically create perspective event work items for any events
  -- that match perspective associations. This ensures perspectives get notified of relevant events.
  -- Uses fuzzy type matching to handle different .NET type name formats.
  -- Only processes events successfully stored in Phase 4.5 (tracked via arrays).
  INSERT INTO wh_perspective_events (
    event_work_id,
    stream_id,
    perspective_name,
    event_id,
    status,
    attempts,
    created_at,
    instance_id,
    lease_expiry
  )
  SELECT DISTINCT
    gen_random_uuid() as event_work_id,
    es.stream_id,
    ma.target_name as perspective_name,
    es.event_id,
    1 as status,  -- Stored flag
    0 as attempts,
    p_now as created_at,
    p_instance_id as instance_id,  -- Immediate lease to current instance
    v_lease_expiry as lease_expiry
  FROM wh_event_store es
  INNER JOIN wh_message_associations ma
    ON es.event_type = ma.normalized_message_type  -- Pre-computed; es.event_type already normalized at Phase 4.5 storage
    AND ma.association_type = c_source_perspective
  WHERE es.event_id = ANY(v_stored_outbox_events || v_stored_inbox_events)
    AND NOT EXISTS (
      SELECT 1 FROM wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;  -- Idempotency

  -- ========================================
  -- Phase 4.6B: Out-of-order detection for auto-created perspective events
  -- ========================================
  -- Mirrors store_perspective_events (migration 022) rewind detection.
  -- If a newly-inserted perspective event has event_id < cursor's last_event_id,
  -- the runner already read past this event from wh_event_store and needs a rewind.
  -- UUID7 comparison works because UUID7 encodes timestamp in the most significant bits.
  UPDATE wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN ooo.min_event_id
        WHEN ooo.min_event_id < pc.rewind_trigger_event_id THEN ooo.min_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,  -- Sliding window: reset on every late event
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)  -- Max cap anchor: set once, preserved on re-flag
  FROM (
    SELECT DISTINCT ON (pe.stream_id, pe.perspective_name)
      pe.stream_id,
      pe.perspective_name,
      pe.event_id as min_event_id
    FROM wh_perspective_events pe
    INNER JOIN wh_perspective_cursors pc2
      ON pc2.stream_id = pe.stream_id
      AND pc2.perspective_name = pe.perspective_name
      AND pc2.last_event_id IS NOT NULL
      AND pe.event_id < pc2.last_event_id
    WHERE pe.event_id = ANY(v_stored_outbox_events || v_stored_inbox_events)
      AND pe.processed_at IS NULL
    ORDER BY pe.stream_id, pe.perspective_name, pe.event_id
  ) ooo
  WHERE pc.stream_id = ooo.stream_id
    AND pc.perspective_name = ooo.perspective_name;

  -- ========================================
  -- Phase 4.7: Auto-Create Perspective Checkpoints
  -- ========================================
  -- When events are stored, automatically create checkpoint rows for any streams
  -- that have events matching perspective associations but don't have checkpoints yet.
  -- Uses normalize_event_type for consistent type matching.
  -- Only processes events successfully stored in Phase 4.5 (tracked via arrays).
  INSERT INTO __SCHEMA__.wh_perspective_cursors (
    stream_id,
    perspective_name,
    last_event_id,
    status
  )
  SELECT DISTINCT
    es.stream_id,
    ma.target_name,  -- perspective_name
    NULL::uuid,      -- last_event_id = NULL (not processed yet)
    0                -- status = 0 (PerspectiveProcessingStatus.None)
  FROM wh_event_store es
  INNER JOIN wh_message_associations ma
    ON es.event_type = ma.normalized_message_type  -- Pre-computed; es.event_type already normalized at Phase 4.5 storage
    AND ma.association_type = c_source_perspective
  WHERE es.event_id = ANY(v_stored_outbox_events || v_stored_inbox_events)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_cursors pc_check
      WHERE pc_check.stream_id = es.stream_id
        AND pc_check.perspective_name = ma.target_name
    )
  ON CONFLICT DO NOTHING;  -- Idempotency - relies on primary key (stream_id, perspective_name)

  -- ========================================
  -- Phase 6: Lease Renewals
  -- ========================================

  -- Renew outbox leases and capture renewed rows into temp table for Phase 7 return
  WITH renewed AS (
    UPDATE wh_outbox
    SET lease_expiry = v_lease_expiry
    WHERE instance_id = p_instance_id
      AND message_id = ANY(
        SELECT (elem::TEXT)::UUID
        FROM jsonb_array_elements_text(p_renew_outbox_lease_ids) as elem
      )
    RETURNING message_id, stream_id
  )
  INSERT INTO temp_orphaned_outbox (message_id, stream_id)
  SELECT message_id, stream_id FROM renewed
  ON CONFLICT DO NOTHING;

  -- Renew inbox leases and capture renewed rows into temp table for Phase 7 return
  WITH renewed AS (
    UPDATE wh_inbox
    SET lease_expiry = v_lease_expiry
    WHERE instance_id = p_instance_id
      AND message_id = ANY(
        SELECT (elem::TEXT)::UUID
        FROM jsonb_array_elements_text(p_renew_inbox_lease_ids) as elem
      )
    RETURNING message_id, stream_id
  )
  INSERT INTO temp_orphaned_inbox (message_id, stream_id)
  SELECT message_id, stream_id FROM renewed
  ON CONFLICT DO NOTHING;

  -- Renew perspective event leases
  UPDATE wh_perspective_events
  SET lease_expiry = v_lease_expiry
  WHERE instance_id = p_instance_id
    AND event_work_id = ANY(
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_perspective_event_lease_ids) as elem
    );

  -- Renew active stream ownership only for streams whose lease is nearing expiry.
  -- Keeps stream stickiness alive as long as the instance is heartbeating,
  -- without generating one dead tuple per owned stream per tick. Streams
  -- refreshed in the current tick are left alone for another ~2/3 of the
  -- lease window. Orphan-claim SLA is unchanged — streams still expire at
  -- lease_expiry, and cross-instance claims still gate on now() > lease_expiry.
  UPDATE __SCHEMA__.wh_active_streams
  SET lease_expiry = v_lease_expiry
  WHERE assigned_instance_id = p_instance_id
    AND lease_expiry < v_refresh_threshold;

  -- ========================================
  -- Phase 7: Return Results
  -- ========================================

  -- v_has_outbox_work / v_has_inbox_work are set AFTER each RETURN QUERY block
  -- via GET DIAGNOSTICS (zero cost — uses already-materialized data).
  -- This replaces the expensive pre-computation EXISTS checks that duplicated
  -- the same complex WHERE clauses from the RETURN QUERY blocks.

  -- DIAGNOSTIC: Log counts before returning results
  IF (p_flags & 4) != 0 THEN
    RAISE DEBUG '[process_work_batch] About to return results: temp_new_outbox=%', (SELECT COUNT(*) FROM temp_new_outbox);
    RAISE DEBUG '[process_work_batch] Checking wh_outbox: total_in_temp_new=%, matching_instance_id=%',
      (SELECT COUNT(*) FROM wh_outbox o INNER JOIN temp_new_outbox t ON o.message_id = t.message_id),
      (SELECT COUNT(*) FROM wh_outbox o INNER JOIN temp_new_outbox t ON o.message_id = t.message_id WHERE o.instance_id = p_instance_id);
    RAISE DEBUG '[process_work_batch] Instance check: p_instance_id=%, first_outbox_instance_id=%',
      p_instance_id,
      (SELECT o.instance_id FROM wh_outbox o INNER JOIN temp_new_outbox t ON o.message_id = t.message_id LIMIT 1);
  END IF;

  -- Return outbox work (first row includes acknowledgement counts)
  -- Uses per-stream ranking to prevent a single busy stream from starving others,
  -- then applies a global LIMIT to prevent hot loops.
  RETURN QUERY
  WITH eligible_outbox AS (
    SELECT
      o.*,
      temp_new.message_id as new_message_id,
      temp_orphaned.message_id as orphaned_message_id,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.created_at) as stream_rank
    FROM wh_outbox o
    LEFT JOIN temp_new_outbox temp_new ON o.message_id = temp_new.message_id
    LEFT JOIN temp_orphaned_outbox temp_orphaned ON o.message_id = temp_orphaned.message_id
    WHERE o.instance_id = p_instance_id
      AND o.lease_expiry > p_now
      AND o.processed_at IS NULL
      AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
      AND NOT EXISTS (
        SELECT 1 FROM wh_outbox blocked
        WHERE blocked.stream_id = o.stream_id AND blocked.stream_id IS NOT NULL
          AND blocked.processed_at IS NULL AND blocked.created_at < o.created_at
          AND blocked.scheduled_for IS NOT NULL AND blocked.scheduled_for > p_now
      )
  ),
  ordered_outbox AS (
    SELECT e.*,
      ROW_NUMBER() OVER (ORDER BY e.created_at) as row_num
    FROM eligible_outbox e
    WHERE e.stream_rank <= v_max_work_items_per_stream
    LIMIT v_max_work_items
  )
  SELECT
    v_rank as instance_rank,
    v_count as active_instance_count,
    c_source_outbox::VARCHAR(20) as source,
    o.message_id as work_id,
    o.stream_id as work_stream_id,
    o.partition_number,
    o.destination as destination,
    o.message_type as message_type,
    o.envelope_type as envelope_type,
    o.event_data::TEXT as message_data,
    -- CRITICAL: First row includes acknowledgement counts in metadata
    CASE WHEN o.row_num = 1 THEN COALESCE(o.metadata, '{}'::JSONB) || v_ack_counts ELSE o.metadata END as metadata,
    o.status,
    o.attempts,
    CASE WHEN o.new_message_id IS NOT NULL THEN true ELSE false END as is_newly_stored,
    CASE WHEN o.orphaned_message_id IS NOT NULL THEN true ELSE false END as is_orphaned,
    NULL::TEXT as error,
    NULL::INTEGER as failure_reason,
    NULL::VARCHAR(200) as perspective_name
  FROM ordered_outbox o;

  GET DIAGNOSTICS v_count = ROW_COUNT;
  v_has_outbox_work := v_count > 0;

  -- Return inbox work (first row includes acknowledgement counts if no outbox work)
  -- Same per-stream ranking + global limit as outbox.
  RETURN QUERY
  WITH eligible_inbox AS (
    SELECT
      i.*,
      temp_new.message_id as new_message_id,
      temp_orphaned.message_id as orphaned_message_id,
      ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at) as stream_rank
    FROM wh_inbox i
    LEFT JOIN temp_new_inbox temp_new ON i.message_id = temp_new.message_id
    LEFT JOIN temp_orphaned_inbox temp_orphaned ON i.message_id = temp_orphaned.message_id
    WHERE i.instance_id = p_instance_id
      AND i.lease_expiry > p_now
      AND i.processed_at IS NULL
      AND (i.scheduled_for IS NULL OR i.scheduled_for <= p_now)
      AND NOT EXISTS (
        SELECT 1 FROM wh_inbox blocked
        WHERE blocked.stream_id = i.stream_id AND blocked.stream_id IS NOT NULL
          AND blocked.processed_at IS NULL AND blocked.received_at < i.received_at
          AND blocked.scheduled_for IS NOT NULL AND blocked.scheduled_for > p_now
      )
  ),
  ordered_inbox AS (
    SELECT e.*,
      ROW_NUMBER() OVER (ORDER BY e.received_at) as row_num
    FROM eligible_inbox e
    WHERE e.stream_rank <= v_max_work_items_per_stream
    LIMIT v_max_work_items
  )
  SELECT
    v_rank as instance_rank,
    v_count as active_instance_count,
    c_source_inbox::VARCHAR(20) as source,
    i.message_id as work_id,
    i.stream_id as work_stream_id,
    i.partition_number,
    i.handler_name as destination,
    i.message_type as message_type,
    NULL::VARCHAR(500) as envelope_type,
    i.event_data::TEXT as message_data,
    -- CRITICAL: First row includes ack counts if no outbox work
    CASE WHEN i.row_num = 1 AND NOT v_has_outbox_work
      THEN COALESCE(i.metadata, '{}'::JSONB) || v_ack_counts
      ELSE i.metadata END as metadata,
    i.status,
    i.attempts,
    CASE WHEN i.new_message_id IS NOT NULL THEN true ELSE false END as is_newly_stored,
    CASE WHEN i.orphaned_message_id IS NOT NULL THEN true ELSE false END as is_orphaned,
    NULL::TEXT as error,
    NULL::INTEGER as failure_reason,
    NULL::VARCHAR(200) as perspective_name
  FROM ordered_inbox i;

  GET DIAGNOSTICS v_count = ROW_COUNT;
  v_has_inbox_work := v_count > 0;

  -- Return receptor work
  RETURN QUERY
  SELECT
    v_rank as instance_rank,
    v_count as active_instance_count,
    'receptor'::VARCHAR(20) as source,
    rp.id as work_id,
    rp.stream_id as work_stream_id,
    rp.partition_number,
    NULL::VARCHAR(200) as destination,
    NULL::VARCHAR(500) as message_type,
    NULL::VARCHAR(500) as envelope_type,
    NULL::TEXT as message_data,
    NULL::JSONB as metadata,
    rp.status::INTEGER,
    rp.attempts,
    false as is_newly_stored,  -- Receptor work created out-of-band
    CASE WHEN temp_orphaned.processing_id IS NOT NULL THEN true ELSE false END as is_orphaned,
    NULL::TEXT as error,
    NULL::INTEGER as failure_reason,
    NULL::VARCHAR(200) as perspective_name
  FROM wh_receptor_processing rp
  LEFT JOIN temp_orphaned_receptor temp_orphaned ON rp.id = temp_orphaned.processing_id
  WHERE rp.instance_id = p_instance_id
    AND rp.lease_expiry > p_now
    AND rp.completed_at IS NULL;

  -- Return perspective work: one row per distinct stream (source='perspective_stream').
  -- The C# PerspectiveWorker determines perspectives from event types using its registry,
  -- then calls get_stream_events to batch-fetch the actual event data in a single SQL round-trip.
  RETURN QUERY
    WITH eligible_perspective AS (
      SELECT
        pe.*,
        temp_new.event_work_id as new_event_work_id,
        temp_orphaned.event_work_id as orphaned_event_work_id,
        ROW_NUMBER() OVER (PARTITION BY pe.stream_id, pe.perspective_name ORDER BY pe.event_id) as stream_rank,
        COUNT(*) OVER (PARTITION BY pe.stream_id, pe.perspective_name) as stream_pending_count
      FROM wh_perspective_events pe
      LEFT JOIN temp_new_perspective_events temp_new ON pe.event_work_id = temp_new.event_work_id
      LEFT JOIN temp_orphaned_perspective_events temp_orphaned ON pe.event_work_id = temp_orphaned.event_work_id
      LEFT JOIN __SCHEMA__.wh_perspective_cursors pc
        ON pe.stream_id = pc.stream_id
        AND pe.perspective_name = pc.perspective_name
      WHERE pe.instance_id = p_instance_id
        AND pe.lease_expiry > p_now
        AND pe.processed_at IS NULL
        AND (pc.stream_lock_instance_id IS NULL
             OR pc.stream_lock_expiry <= p_now
             OR pc.stream_lock_instance_id = p_instance_id)
        AND NOT (
          (pc.status & 32) = 32
          AND pc.rewind_flagged_at IS NOT NULL
          AND pc.rewind_flagged_at + (v_rewind_debounce_seconds || ' seconds')::INTERVAL > p_now
          AND (pc.rewind_first_flagged_at IS NULL
               OR pc.rewind_first_flagged_at + (v_rewind_max_debounce_seconds || ' seconds')::INTERVAL > p_now)
        )
    ),
    tier1_limited AS (
      -- Tier 1: small streams (pending <= per-stream cap). Within the tier, order
      -- by pending_count ASC so the smallest stream is served first — matches the
      -- fairness contract (small streams never starve behind larger ones).
      SELECT e.*
      FROM eligible_perspective e
      WHERE e.stream_pending_count <= v_max_work_items_per_stream
      ORDER BY e.stream_pending_count, e.stream_id, e.perspective_name, e.event_id
      LIMIT v_tier1_max
    ),
    tier1_used AS (
      SELECT COUNT(*) as cnt FROM tier1_limited
    ),
    tier2_limited AS (
      -- Tier 2: large streams (pending > per-stream cap). Within the tier, order
      -- by pending_count ASC so that when Tier 2 is itself large enough to fill the
      -- remaining budget, smaller-of-the-large streams make progress first.
      SELECT e.*
      FROM eligible_perspective e
      WHERE e.stream_pending_count > v_max_work_items_per_stream
        AND e.stream_rank <= v_max_work_items_per_stream
      ORDER BY e.stream_pending_count, e.stream_id, e.perspective_name, e.event_id
      LIMIT v_max_work_items - (SELECT cnt FROM tier1_used)
    ),
    ordered_perspective AS (
      SELECT combined.*,
        -- Preserve tier ordering (Tier 1 before Tier 2) AND within-tier ordering
        -- (smaller pending_count first) via the ROW_NUMBER window.
        ROW_NUMBER() OVER (ORDER BY combined.tier, combined.stream_pending_count, combined.stream_id, combined.perspective_name, combined.event_id) as row_num
      FROM (
        SELECT t1.*, 1 as tier FROM tier1_limited t1
        UNION ALL
        SELECT t2.*, 2 as tier FROM tier2_limited t2
      ) combined
    ),
    distinct_streams AS (
      -- Preserve the tier-derived row_num (Tier 1 streams come before Tier 2) by
      -- collapsing to distinct stream_ids on the MIN row_num per stream. The final
      -- ORDER BY min_row_num surfaces Tier 1 streams first, which matches the
      -- fairness contract documented in plans/twotier-*.md.
      SELECT stream_id, MIN(row_num) AS min_row_num
      FROM ordered_perspective
      WHERE row_num <= v_max_work_items
      GROUP BY stream_id
    )
    SELECT
      v_rank as instance_rank,
      v_count as active_instance_count,
      'perspective_stream'::VARCHAR(20) as source,
      NULL::UUID as work_id,
      ds.stream_id as work_stream_id,
      NULL::INTEGER as partition_number,
      NULL::VARCHAR(200) as destination,
      NULL::VARCHAR(500) as message_type,
      NULL::VARCHAR(500) as envelope_type,
      NULL::TEXT as message_data,
      CASE WHEN ROW_NUMBER() OVER (ORDER BY ds.min_row_num) = 1 AND NOT v_has_outbox_work AND NOT v_has_inbox_work
        THEN v_ack_counts
        ELSE NULL::JSONB END as metadata,
      0::INTEGER as status,
      0::INTEGER as attempts,
      false as is_newly_stored,
      false as is_orphaned,
      NULL::TEXT as error,
      NULL::INTEGER as failure_reason,
      NULL::VARCHAR(200) as perspective_name
    FROM distinct_streams ds
    ORDER BY ds.min_row_num;

  -- Return sync inquiry results
  RETURN QUERY
  SELECT
    NULL::INTEGER as instance_rank,
    NULL::INTEGER as active_instance_count,
    'sync_result'::VARCHAR(20) as source,
    sr.inquiry_id as work_id,
    sr.stream_id as work_stream_id,  -- Include StreamId from inquiry
    sr.pending_count as partition_number,  -- Reuse partition_number column for pending_count
    NULL::VARCHAR(200) as destination,
    NULL::VARCHAR(500) as message_type,
    NULL::VARCHAR(500) as envelope_type,
    -- Encode pending_event_ids as JSON array in message_data
    CASE
      WHEN sr.pending_event_ids IS NOT NULL
      THEN (SELECT jsonb_agg(id)::TEXT FROM UNNEST(sr.pending_event_ids) as id)
      ELSE NULL
    END as message_data,
    -- Encode processed_event_ids as JSON array in metadata (for explicit event tracking)
    CASE
      WHEN sr.processed_event_ids IS NOT NULL
      THEN jsonb_build_object('processed_event_ids', (SELECT jsonb_agg(id) FROM UNNEST(sr.processed_event_ids) as id))
      ELSE NULL
    END as metadata,
    sr.processed_count as status,  -- Reuse status column for processed_count
    NULL::INTEGER as attempts,
    false as is_newly_stored,
    false as is_orphaned,
    NULL::TEXT as error,
    NULL::INTEGER as failure_reason,
    NULL::VARCHAR(200) as perspective_name
  FROM temp_sync_results sr;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_work_batch IS
'Orchestrator function that coordinates all work batch processing (v2 - decomposed architecture). Returns acknowledgement counts in first result row metadata for C# completion tracking. Registers heartbeat, processes completions/failures, stores new work, claims orphaned work, renews leases, and returns aggregated work batch. All operations occur in a single transaction for atomicity.

Decomposition (migrations 009-029):

Foundation (Layer 0):
  - 009: create_message_association_registry
  - 010: register_instance_heartbeat
  - 011: cleanup_stale_instances
  - 012: calculate_instance_rank

Completions (Layer 1):
  - 013: process_outbox_completions
  - 014: process_inbox_completions
  - 015: process_perspective_event_completions
  - 016: update_perspective_cursors

Failures (Layer 2):
  - 017: process_outbox_failures
  - 018: process_inbox_failures
  - 019: process_perspective_event_failures

Storage (Layer 3):
  - 020: store_outbox_messages
  - 021: store_inbox_messages
  - 022: store_perspective_events
  - 023: cleanup_completed_streams

Claiming (Layer 4):
  - 024: claim_orphaned_outbox
  - 025: claim_orphaned_inbox
  - 026: claim_orphaned_receptor_work
  - 027: claim_orphaned_perspective_events

Error Tracking (Layer 5):
  - 028: event_storage_error_tracking (wh_log table, wh_settings table, log_event function)

Assembly (Layer 6):
  - 029: process_work_batch (orchestrator)

Benefits:
- Single responsibility per function
- Easier testing and debugging
- Better performance analysis
- Clearer dependency graph
- Maintainable codebase';


-- ============================================================================
-- claim_work — focused replacement for the claim portion of process_work_batch.
-- Phase A of work-pump decomposition. Empty-call short-circuit: when all queues
-- are empty, return immediately without invoking any claim_orphaned_* function,
-- cutting the structural ~17 ms idle floor of process_work_batch to ≤ 1 ms.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('claim_work');

CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_max_streams INTEGER DEFAULT 1000,
  p_partition_count INTEGER DEFAULT 10000,
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS TABLE(
  source VARCHAR(20),           -- 'outbox' | 'inbox' | 'receptor' | 'perspective'
  work_id UUID,
  work_stream_id UUID,
  partition_number INTEGER,
  destination VARCHAR(200),
  message_type VARCHAR(500),
  envelope_type VARCHAR(500),
  message_data TEXT,
  metadata JSONB,
  status INTEGER,
  attempts INTEGER,
  is_newly_stored BOOLEAN,
  is_orphaned BOOLEAN,
  perspective_name VARCHAR(200)
) AS $$
DECLARE
  v_has_any_work BOOLEAN;
BEGIN
  -- Empty-call short-circuit: cheap indexed EXISTS lookups on partial indexes.
  -- Each LIMIT 1 against an existing partial index is sub-millisecond when buffer-cached.
  -- Note: wh_receptor_processing uses completed_at (not processed_at) for the "is done" semantic.
  v_has_any_work := EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_perspective_events WHERE processed_at IS NULL LIMIT 1)
                 OR EXISTS (SELECT 1 FROM __SCHEMA__.wh_receptor_processing WHERE completed_at IS NULL LIMIT 1);

  IF NOT v_has_any_work THEN
    RETURN;  -- empty result set; orphan-claim sub-functions never invoked
  END IF;

  -- Non-empty path: claim outbox work and return it.
  -- Inbox / receptor / perspective claim + return land in subsequent TDD cycles.
  DECLARE
    v_now TIMESTAMPTZ := NOW();
    v_lease_expiry TIMESTAMPTZ := v_now + (p_lease_seconds || ' seconds')::INTERVAL;
    v_stale_cutoff TIMESTAMPTZ := v_now - INTERVAL '30 seconds';
    v_rank INTEGER;
    v_count INTEGER;
  BEGIN
    SELECT instance_rank, active_instance_count INTO v_rank, v_count
    FROM __SCHEMA__.calculate_instance_rank(p_instance_id, v_stale_cutoff);

    -- Claim orphaned / unowned work across all categories for this instance.
    PERFORM __SCHEMA__.claim_orphaned_outbox(
      p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff
    );
    PERFORM __SCHEMA__.claim_orphaned_inbox(
      p_instance_id, v_rank, v_count, v_lease_expiry, v_now, p_partition_count, v_stale_cutoff
    );
    PERFORM __SCHEMA__.claim_orphaned_perspective_events(
      p_instance_id, v_lease_expiry, v_now, p_max_streams
    );

    -- Return outbox work owned by this instance.
    -- Per-stream rank prevents one busy stream from starving others; global LIMIT bounds the batch.
    RETURN QUERY
    WITH eligible_outbox AS (
      SELECT
        o.*,
        ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.created_at) AS stream_rank
      FROM __SCHEMA__.wh_outbox o
      WHERE o.instance_id = p_instance_id
        AND o.lease_expiry > v_now
        AND o.processed_at IS NULL
        AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now)
    ),
    ordered_outbox AS (
      SELECT eo.*, ROW_NUMBER() OVER (ORDER BY eo.created_at) AS row_num
      FROM eligible_outbox eo
      ORDER BY eo.created_at
      LIMIT p_max_streams
    )
    SELECT
      'outbox'::VARCHAR(20)         AS source,
      oo.message_id                 AS work_id,
      oo.stream_id                  AS work_stream_id,
      oo.partition_number,
      oo.destination::VARCHAR(200),
      oo.message_type::VARCHAR(500),
      oo.envelope_type::VARCHAR(500),
      oo.event_data::TEXT           AS message_data,
      oo.metadata,
      oo.status,
      oo.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name
    FROM ordered_outbox oo;

    -- Return inbox work owned by this instance. Inbox uses handler_name (cast to destination)
    -- and received_at (cast to created_at). envelope_type is NULL for inbox.
    RETURN QUERY
    WITH eligible_inbox AS (
      SELECT
        i.*,
        ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at) AS stream_rank
      FROM __SCHEMA__.wh_inbox i
      WHERE i.instance_id = p_instance_id
        AND i.lease_expiry > v_now
        AND i.processed_at IS NULL
    ),
    ordered_inbox AS (
      SELECT ei.*, ROW_NUMBER() OVER (ORDER BY ei.received_at) AS row_num
      FROM eligible_inbox ei
      ORDER BY ei.received_at
      LIMIT p_max_streams
    )
    SELECT
      'inbox'::VARCHAR(20)          AS source,
      oi.message_id                 AS work_id,
      oi.stream_id                  AS work_stream_id,
      oi.partition_number,
      oi.handler_name::VARCHAR(200) AS destination,
      oi.message_type::VARCHAR(500),
      NULL::VARCHAR(500)            AS envelope_type,
      oi.event_data::TEXT           AS message_data,
      oi.metadata,
      oi.status,
      oi.attempts,
      false                         AS is_newly_stored,
      false                         AS is_orphaned,
      NULL::VARCHAR(200)            AS perspective_name
    FROM ordered_inbox oi;

    -- Return perspective work as one row per distinct stream owned by this instance.
    -- Two-tier fairness ranking lands in a future TDD cycle; this is the basic shape.
    RETURN QUERY
    SELECT DISTINCT
      'perspective_stream'::VARCHAR(20) AS source,
      NULL::UUID                        AS work_id,
      pe.stream_id                      AS work_stream_id,
      NULL::INTEGER                     AS partition_number,
      NULL::VARCHAR(200)                AS destination,
      NULL::VARCHAR(500)                AS message_type,
      NULL::VARCHAR(500)                AS envelope_type,
      NULL::TEXT                        AS message_data,
      NULL::JSONB                       AS metadata,
      0::INTEGER                        AS status,
      0::INTEGER                        AS attempts,
      false                             AS is_newly_stored,
      false                             AS is_orphaned,
      NULL::VARCHAR(200)                AS perspective_name
    FROM __SCHEMA__.wh_perspective_events pe
    WHERE pe.instance_id = p_instance_id
      AND pe.lease_expiry > v_now
      AND pe.processed_at IS NULL
    LIMIT p_max_streams;

    -- Drain-mode hint: if this instance has more eligible work than fits in a single
    -- batch, RAISE NOTICE so the C# claim worker skips its wait and re-polls immediately.
    -- Survives pgbouncer (protocol message, not a session-state thing).
    DECLARE
      v_pending INTEGER;
    BEGIN
      SELECT
        (SELECT COUNT(*) FROM __SCHEMA__.wh_outbox o
           WHERE o.instance_id = p_instance_id AND o.lease_expiry > v_now AND o.processed_at IS NULL
             AND (o.scheduled_for IS NULL OR o.scheduled_for <= v_now))
        + (SELECT COUNT(*) FROM __SCHEMA__.wh_inbox i
           WHERE i.instance_id = p_instance_id AND i.lease_expiry > v_now AND i.processed_at IS NULL)
        + (SELECT COUNT(DISTINCT pe.stream_id) FROM __SCHEMA__.wh_perspective_events pe
           WHERE pe.instance_id = p_instance_id AND pe.lease_expiry > v_now AND pe.processed_at IS NULL)
      INTO v_pending;

      IF v_pending > p_max_streams THEN
        RAISE NOTICE 'whizbang.has_more=true';
      END IF;
    END;
  END;

  RETURN;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.claim_work IS
'Phase A focused claim function replacing the claim portion of process_work_batch. Returns work for the calling instance with an empty-call short-circuit that skips orphan-claim scans when all queues are empty (drops idle floor from ~17 ms to ≤ 1 ms). Real claim logic lands in subsequent TDD cycles; old process_work_batch remains the active production path until Phase C migrates IWorkCoordinator callers.';


-- ============================================================================
-- commit_handler_result — atomic transactional bundle for inbox handler completion.
-- Combines: inbox completion + emitted new outbox/inbox messages, in one transaction.
-- This is the only true transactional unit in the work-pump decomposition; everything
-- else in the new function family is independently committable.
-- Phase A of work-pump decomposition.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('commit_handler_result');

CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_result(
  p_request JSONB
) RETURNS VOID AS $$
DECLARE
  v_instance_id UUID := (p_request ->> 'instance_id')::UUID;
  v_now TIMESTAMPTZ := NOW();
  v_lease_expiry TIMESTAMPTZ := v_now + INTERVAL '300 seconds';
  v_partition_count INTEGER := COALESCE((p_request ->> 'partition_count')::INTEGER, 10000);
  v_inbox_completion JSONB := p_request -> 'inbox_completion';
  v_new_outbox JSONB := COALESCE(p_request -> 'new_outbox_messages', '[]'::JSONB);
  v_new_inbox JSONB := COALESCE(p_request -> 'new_inbox_messages', '[]'::JSONB);
  v_outbox_inserted INTEGER := 0;
  v_inbox_inserted INTEGER := 0;
BEGIN
  -- 1. Mark inbox completion (if present). process_inbox_completions takes a JSONB array;
  --    wrap the single completion in an array.
  IF v_inbox_completion IS NOT NULL AND v_inbox_completion::TEXT != 'null' THEN
    PERFORM __SCHEMA__.process_inbox_completions(
      jsonb_build_array(v_inbox_completion),
      v_now,
      FALSE  -- debug_mode off
    );
  END IF;

  -- 2. Store new outbox messages emitted by the handler.
  IF jsonb_array_length(v_new_outbox) > 0 THEN
    PERFORM __SCHEMA__.store_outbox_messages(
      v_new_outbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
    v_outbox_inserted := jsonb_array_length(v_new_outbox);
  END IF;

  -- 3. Store new inbox messages emitted by the handler (rare path).
  IF jsonb_array_length(v_new_inbox) > 0 THEN
    PERFORM __SCHEMA__.store_inbox_messages(
      v_new_inbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
    v_inbox_inserted := jsonb_array_length(v_new_inbox);
  END IF;

  -- 4. NOTIFY signal types — one per category that received new rows.
  --    pg_notify deduplicates (channel, payload) within the same transaction at COMMIT,
  --    so 10 000 inserts → one delivered notification per category. Free.
  IF v_outbox_inserted > 0 THEN
    PERFORM pg_notify('wh_work', 'outbox');
  END IF;
  IF v_inbox_inserted > 0 THEN
    PERFORM pg_notify('wh_work', 'inbox');
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.commit_handler_result IS
'Atomic transactional bundle for inbox handler completion. Marks the inbox completion AND stores the new outbox/inbox messages the handler emitted in one transaction; if any step fails the whole bundle rolls back. Emits pg_notify(''wh_work'', category) per category that received new rows, dedup at COMMIT means burst inserts collapse to one delivered notification. Phase A of work-pump decomposition.';


-- ============================================================================
-- commit_handler_batch — SAVEPOINT-per-handler batched commit. The throughput
-- multiplier: N handler results in one round-trip, one fsync at the outer commit,
-- with per-handler success/failure isolation. PL/pgSQL BEGIN..EXCEPTION blocks
-- create implicit subtransactions (savepoints), so a failing handler rolls back
-- ONLY its own effects; siblings are unaffected.
-- Phase A of work-pump decomposition.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('commit_handler_batch');

CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_batch(
  p_results JSONB
) RETURNS TABLE(
  handler_id UUID,
  success BOOLEAN,
  error_message TEXT
) AS $$
DECLARE
  r RECORD;
  v_handler_id UUID;
BEGIN
  IF jsonb_array_length(p_results) = 0 THEN
    RETURN;
  END IF;

  FOR r IN
    SELECT elem
    FROM jsonb_array_elements(p_results) AS elem
  LOOP
    v_handler_id := (r.elem ->> 'handler_id')::UUID;

    BEGIN
      -- Implicit SAVEPOINT scope: any exception inside this BEGIN..EXCEPTION block
      -- rolls back ONLY this iteration's writes, then control jumps to the EXCEPTION
      -- branch and the loop continues with the next handler.
      PERFORM __SCHEMA__.commit_handler_result(r.elem);
      RETURN QUERY SELECT v_handler_id AS handler_id, TRUE AS success, NULL::TEXT AS error_message;
    EXCEPTION WHEN OTHERS THEN
      RETURN QUERY SELECT v_handler_id AS handler_id, FALSE AS success, SQLERRM::TEXT AS error_message;
    END;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.commit_handler_batch IS
'SAVEPOINT-per-handler batched commit. Accepts an array of handler-result bundles; runs each through commit_handler_result inside its own implicit subtransaction. A failing handler rolls back only its own effects; siblings commit normally. Returns per-handler (handler_id, success, error_message) for the C# flusher to ack successes and re-queue failures. Single fsync at outer commit covers all successful handlers — the throughput multiplier vs single-handler-per-call.';


-- ============================================================================
-- complete_outbox_published — fire-and-forget batched mark-as-processed for outbox
-- rows after the transport publish succeeds. Coalesced by the C# flush worker.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('complete_outbox_published');

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_outbox_published(
  p_ids UUID[]
) RETURNS INTEGER AS $$
DECLARE
  v_updated INTEGER;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  UPDATE __SCHEMA__.wh_outbox
  SET processed_at = NOW(),
      status = status | 4  -- Published flag (additive bit set)
  WHERE message_id = ANY(p_ids)
    AND processed_at IS NULL;

  GET DIAGNOSTICS v_updated = ROW_COUNT;
  RETURN v_updated;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_outbox_published IS
'Marks outbox rows as processed after successful transport publish. Fire-and-forget; unknown ids silently ignored (idempotent). Coalesced + batched by the C# OutboxCompletionFlushWorker. Returns rows-affected for ack tracking.';


-- ============================================================================
-- record_heartbeat — decoupled heartbeat function. Separated from claim_work so
-- the heartbeat timer can fire on its own cadence (5 s default) independent of
-- polling. Sub-millisecond UPSERT.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('record_heartbeat');

CREATE OR REPLACE FUNCTION __SCHEMA__.record_heartbeat(
  p_instance_id UUID,
  p_service_name TEXT,
  p_host_name TEXT,
  p_process_id INTEGER,
  p_metadata JSONB DEFAULT '{}'::JSONB
) RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_service_instances
    (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
  VALUES
    (p_instance_id, p_service_name, p_host_name, p_process_id, NOW(), NOW(), p_metadata)
  ON CONFLICT (instance_id) DO UPDATE SET
    last_heartbeat_at = NOW(),
    metadata = EXCLUDED.metadata;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.record_heartbeat IS
'Decoupled heartbeat UPSERT. Inserts a new wh_service_instances row on first call, updates last_heartbeat_at on subsequent calls. Called by C# HeartbeatWorker on its own timer (5 s default), independent of polling cadence. Sub-millisecond cost.';


-- ============================================================================
-- complete_perspective — batched perspective completion. Combines event-row
-- deletion (status flag = 1 means "completed/processed") with cursor advancement
-- in a single call. Coalesced flush from C# PerspectiveCompletionFlushWorker.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('complete_perspective');

CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective(
  p_cursors JSONB,        -- [{StreamId, PerspectiveName}] for cursor advancement
  p_event_work_ids UUID[] -- event_work_id rows to mark complete (deleted in production mode)
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_completions JSONB;
BEGIN
  -- Build the JSONB array of completions from the work-id list. StatusFlags = 1 = Completed.
  IF p_event_work_ids IS NOT NULL AND array_length(p_event_work_ids, 1) IS NOT NULL THEN
    SELECT jsonb_agg(jsonb_build_object('EventWorkId', wid, 'StatusFlags', 1))
    INTO v_completions
    FROM unnest(p_event_work_ids) AS wid;

    PERFORM __SCHEMA__.process_perspective_event_completions(
      COALESCE(v_completions, '[]'::JSONB),
      v_now,
      FALSE  -- debug_mode off → DELETE rows
    );
  END IF;

  -- Advance cursors for the (StreamId, PerspectiveName) pairs in p_cursors.
  IF p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0 THEN
    PERFORM __SCHEMA__.update_perspective_cursors(p_cursors, FALSE);
  END IF;

  -- NOTIFY for downstream wakeups (e.g., perspective-sync awaiters watching for cursor advancement).
  IF (p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0)
     OR (p_event_work_ids IS NOT NULL AND array_length(p_event_work_ids, 1) IS NOT NULL) THEN
    PERFORM pg_notify('wh_work', 'perspective');
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.complete_perspective IS
'Batched perspective completion. Marks event-work rows complete (DELETEs them in production mode) and advances cursors in a single round-trip. Coalesced flush from C# PerspectiveCompletionFlushWorker. Emits pg_notify(''wh_work'', ''perspective'') so peers know cursors moved.';


-- ============================================================================
-- report_failures — category-aware batched failure reporter. Routes to the
-- correct underlying process_*_failures sub-function based on p_category.
-- Coalesced flush from C# FailureFlushWorker.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('report_failures');

CREATE OR REPLACE FUNCTION __SCHEMA__.report_failures(
  p_category TEXT,
  p_failures JSONB
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
BEGIN
  IF p_failures IS NULL OR jsonb_array_length(p_failures) = 0 THEN
    RETURN;
  END IF;

  CASE p_category
    WHEN 'outbox' THEN
      PERFORM __SCHEMA__.process_outbox_failures(p_failures, v_now);
    WHEN 'inbox' THEN
      PERFORM __SCHEMA__.process_inbox_failures(p_failures, v_now);
    WHEN 'perspective_event' THEN
      PERFORM __SCHEMA__.process_perspective_event_failures(p_failures, v_now);
    ELSE
      RAISE EXCEPTION 'report_failures: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.report_failures IS
'Category-aware batched failure reporter. Dispatches to process_outbox_failures / process_inbox_failures / process_perspective_event_failures based on p_category. Coalesced flush from C# FailureFlushWorker. Raises on unknown category.';


-- ============================================================================
-- renew_leases — per-category batched lease extension. Called by C# LeaseRenewalWorker
-- when in-flight items are within lease/3 of expiry. Coalesced flush.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('renew_leases');

CREATE OR REPLACE FUNCTION __SCHEMA__.renew_leases(
  p_category TEXT,
  p_ids UUID[],
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS INTEGER AS $$
DECLARE
  v_new_expiry TIMESTAMPTZ := NOW() + (p_lease_seconds || ' seconds')::INTERVAL;
  v_updated INTEGER;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  CASE p_category
    WHEN 'outbox' THEN
      UPDATE __SCHEMA__.wh_outbox
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
    WHEN 'inbox' THEN
      UPDATE __SCHEMA__.wh_inbox
        SET lease_expiry = v_new_expiry
        WHERE message_id = ANY(p_ids)
          AND processed_at IS NULL;
    WHEN 'perspective_event' THEN
      UPDATE __SCHEMA__.wh_perspective_events
        SET lease_expiry = v_new_expiry
        WHERE event_work_id = ANY(p_ids)
          AND processed_at IS NULL;
    ELSE
      RAISE EXCEPTION 'renew_leases: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;

  GET DIAGNOSTICS v_updated = ROW_COUNT;
  RETURN v_updated;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.renew_leases IS
'Batched lease extension per category. UPDATEs lease_expiry to NOW() + p_lease_seconds for the supplied ids in the chosen category table, only for rows that are not yet processed. Returns rows-affected. Called by C# LeaseRenewalWorker when in-flight items approach lease/3 from expiry.';


-- ============================================================================
-- flush_completions — composite single-round-trip flusher. Combines the per-category
-- completion functions into one call when the C# flusher has multiple categories
-- buffered. Single fsync at outer commit covers all sub-operations.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('flush_completions');

CREATE OR REPLACE FUNCTION __SCHEMA__.flush_completions(
  p_outbox_ids UUID[],
  p_perspective_cursors JSONB,
  p_perspective_event_work_ids UUID[],
  p_failures JSONB           -- [{Category: 'outbox'|'inbox'|'perspective_event', Items: [...]}]
) RETURNS VOID AS $$
DECLARE
  v_failure_group RECORD;
BEGIN
  IF p_outbox_ids IS NOT NULL AND array_length(p_outbox_ids, 1) IS NOT NULL THEN
    PERFORM __SCHEMA__.complete_outbox_published(p_outbox_ids);
  END IF;

  IF (p_perspective_cursors IS NOT NULL AND jsonb_array_length(p_perspective_cursors) > 0)
     OR (p_perspective_event_work_ids IS NOT NULL AND array_length(p_perspective_event_work_ids, 1) IS NOT NULL) THEN
    PERFORM __SCHEMA__.complete_perspective(
      COALESCE(p_perspective_cursors, '[]'::JSONB),
      COALESCE(p_perspective_event_work_ids, ARRAY[]::UUID[])
    );
  END IF;

  IF p_failures IS NOT NULL AND jsonb_array_length(p_failures) > 0 THEN
    FOR v_failure_group IN
      SELECT
        elem->>'Category' AS category,
        elem->'Items' AS items
      FROM jsonb_array_elements(p_failures) AS elem
    LOOP
      PERFORM __SCHEMA__.report_failures(v_failure_group.category, v_failure_group.items);
    END LOOP;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.flush_completions IS
'Composite single-round-trip flusher. Called by C# flush worker when it has multiple completion categories buffered. Combines complete_outbox_published + complete_perspective + report_failures (per-category) into one call. Single fsync at outer commit covers all sub-operations — the latency-and-throughput optimization for high-volume flush ticks.';


-- ============================================================================
-- resolve_sync_inquiries — read-only PerspectiveSyncAwaiter path. Reports pending
-- vs processed event counts per (stream, perspective) pair so the awaiter can
-- wait for cursor advancement. No writes; safe to call without a transaction.
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('resolve_sync_inquiries');

CREATE OR REPLACE FUNCTION __SCHEMA__.resolve_sync_inquiries(
  p_inquiries JSONB
) RETURNS TABLE(
  inquiry_id UUID,
  stream_id UUID,
  pending_count INTEGER,
  processed_count INTEGER,
  pending_event_ids UUID[],
  processed_event_ids UUID[]
) AS $$
BEGIN
  IF p_inquiries IS NULL OR jsonb_array_length(p_inquiries) = 0 THEN
    RETURN;
  END IF;

  RETURN QUERY
  SELECT
    (inquiry->>'InquiryId')::UUID,
    (inquiry->>'StreamId')::UUID,
    COUNT(es.event_id) FILTER (WHERE pe.processed_at IS NULL)::INTEGER AS pending_count,
    COUNT(es.event_id) FILTER (WHERE pe.processed_at IS NOT NULL)::INTEGER AS processed_count,
    CASE
      WHEN (inquiry->>'IncludePendingEventIds')::BOOLEAN = true
      THEN ARRAY_AGG(es.event_id) FILTER (WHERE pe.processed_at IS NULL)
      ELSE NULL
    END AS pending_event_ids,
    CASE
      WHEN (inquiry->>'IncludeProcessedEventIds')::BOOLEAN = true
      THEN ARRAY_AGG(es.event_id) FILTER (WHERE pe.processed_at IS NOT NULL)
      ELSE NULL
    END AS processed_event_ids
  FROM jsonb_array_elements(p_inquiries) AS inquiry
  LEFT JOIN __SCHEMA__.wh_event_store es
    ON es.stream_id = (inquiry->>'StreamId')::UUID
    AND (
      (inquiry->'EventIds') IS NULL
      OR jsonb_array_length(inquiry->'EventIds') = 0
      OR es.event_id = ANY(ARRAY(SELECT (jsonb_array_elements_text(inquiry->'EventIds'))::UUID))
    )
    AND (
      (inquiry->'EventTypeFilter') IS NULL
      OR jsonb_array_length(inquiry->'EventTypeFilter') = 0
      OR es.event_type = ANY(ARRAY(SELECT jsonb_array_elements_text(inquiry->'EventTypeFilter')))
    )
  LEFT JOIN __SCHEMA__.wh_perspective_events pe
    ON pe.event_id = es.event_id
    AND pe.perspective_name = inquiry->>'PerspectiveName'
  WHERE
    CASE
      WHEN (inquiry->>'DiscoverPendingFromOutbox')::BOOLEAN = true
        THEN es.event_id IS NOT NULL
      ELSE true
    END
  GROUP BY
    inquiry->>'InquiryId',
    inquiry->>'StreamId',
    inquiry->>'IncludePendingEventIds',
    inquiry->>'IncludeProcessedEventIds';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.resolve_sync_inquiries IS
'PerspectiveSyncAwaiter read-only path. For each inquiry, returns pending vs processed event counts (and optionally the event ID lists) by joining wh_event_store to wh_perspective_events. Filters by stream, optional event-id list, optional event-type-filter, optional perspective. No writes.';
