-- Migration: 029_ProcessWorkBatch.sql
-- Date: 2025-12-28
-- Description: Orchestrator function that coordinates all work batch processing.
--              Calls all decomposed functions in dependency order and returns aggregated results.
--              Uses log_event() function for tracking idempotent event conflicts.
-- Dependencies: 009-028 (foundation, completion, failure, storage, cleanup, claiming functions, and error tracking)

-- Drop old monolithic version from migration 007 (different signature)
DROP FUNCTION IF EXISTS process_work_batch CASCADE;

CREATE OR REPLACE FUNCTION process_work_batch(
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
  p_stale_threshold_seconds INTEGER DEFAULT 600
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
  perspective_name VARCHAR(200),
  sequence_number BIGINT
) AS $$
DECLARE
  v_lease_expiry TIMESTAMPTZ;
  v_stale_cutoff TIMESTAMPTZ;
  v_rank INTEGER;
  v_count INTEGER;
  v_completed_events JSONB;
  v_completion RECORD;

  -- Arrays to track successfully stored events (for Phase 4.6 and 4.7 filtering)
  v_stored_outbox_events UUID[] := '{}';
  v_stored_inbox_events UUID[] := '{}';

  -- Conflict tracking for logging
  v_outbox_conflict_count INTEGER := 0;
  v_outbox_conflict_types TEXT[];
  v_inbox_conflict_count INTEGER := 0;
  v_inbox_conflict_types TEXT[];
BEGIN
  -- Calculate lease expiry and stale cutoff
  v_lease_expiry := p_now + (p_lease_duration_seconds || ' seconds')::INTERVAL;
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

  -- ========================================
  -- Phase 1: Foundation (Heartbeat & Cleanup)
  -- ========================================

  -- Register heartbeat and get rank
  PERFORM register_instance_heartbeat(
    p_instance_id,
    p_service_name,
    p_host_name,
    p_process_id,
    p_metadata,
    p_now,
    v_lease_expiry
  );

  -- Cleanup stale instances
  PERFORM cleanup_stale_instances(v_stale_cutoff);

  -- Calculate rank
  SELECT cir.instance_rank, cir.active_instance_count INTO v_rank, v_count
  FROM calculate_instance_rank(p_instance_id, v_stale_cutoff) AS cir;

  -- Cleanup completed streams
  PERFORM cleanup_completed_streams(p_now);

  -- ========================================
  -- Phase 2: Completions
  -- ========================================

  -- Process outbox completions
  PERFORM process_outbox_completions(p_outbox_completions, p_now, (p_flags & 4) != 0);

  -- Process inbox completions
  PERFORM process_inbox_completions(p_inbox_completions, p_now, (p_flags & 4) != 0);

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
  FROM process_perspective_event_completions(
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
        'StreamId', tcp.stream_id,
        'PerspectiveName', tcp.perspective_name
      )
    )
    FROM temp_completed_perspectives tcp
  );

  IF v_completed_events IS NOT NULL THEN
    PERFORM update_perspective_checkpoints(v_completed_events, (p_flags & 4) != 0);
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

  -- Process perspective checkpoint completions (direct completion reports from perspective runners)
  IF jsonb_array_length(p_perspective_completions) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'StreamId')::UUID as stream_id,
        elem->>'PerspectiveName' as perspective_name,
        (elem->>'LastEventId')::UUID as last_event_id,
        (elem->>'Status')::SMALLINT as status
      FROM jsonb_array_elements(p_perspective_completions) as elem
    LOOP
      PERFORM complete_perspective_checkpoint_work(
        v_completion.stream_id,
        v_completion.perspective_name,
        v_completion.last_event_id,
        v_completion.status,
        NULL
      );
    END LOOP;
  END IF;

  -- ========================================
  -- Phase 3: Failures
  -- ========================================

  -- Process outbox failures
  PERFORM process_outbox_failures(p_outbox_failures, p_now);

  -- Process inbox failures
  PERFORM process_inbox_failures(p_inbox_failures, p_now);

  -- Process perspective event failures
  PERFORM process_perspective_event_failures(p_perspective_event_failures, p_now);

  -- Process perspective checkpoint failures (direct failure reports from perspective runners)
  IF jsonb_array_length(p_perspective_failures) > 0 THEN
    FOR v_completion IN
      SELECT
        (elem->>'StreamId')::UUID as stream_id,
        elem->>'PerspectiveName' as perspective_name,
        (elem->>'LastEventId')::UUID as last_event_id,
        (elem->>'Status')::SMALLINT as status,
        elem->>'Error' as error_message
      FROM jsonb_array_elements(p_perspective_failures) as elem
    LOOP
      PERFORM complete_perspective_checkpoint_work(
        v_completion.stream_id,
        v_completion.perspective_name,
        v_completion.last_event_id,
        v_completion.status,
        v_completion.error_message
      );
    END LOOP;
  END IF;

  -- ========================================
  -- Phase 4: Storage (New Work)
  -- ========================================

  -- Store new outbox messages and track
  INSERT INTO temp_new_outbox (message_id, stream_id)
  SELECT som.message_id, som.stream_id
  FROM store_outbox_messages(
    p_new_outbox_messages,
    p_instance_id,
    v_lease_expiry,
    p_now,
    p_partition_count
  ) AS som
  WHERE som.was_newly_created = true;

  -- Store new inbox messages and track
  INSERT INTO temp_new_inbox (message_id, stream_id)
  SELECT sim.message_id, sim.stream_id
  FROM store_inbox_messages(
    p_new_inbox_messages,
    p_instance_id,
    v_lease_expiry,
    p_now,
    p_partition_count
  ) AS sim
  WHERE sim.was_newly_created = true;

  -- Store new perspective events and track
  INSERT INTO temp_new_perspective_events (event_work_id, stream_id, perspective_name)
  SELECT spe.event_work_id, spe.stream_id, spe.perspective_name
  FROM store_perspective_events(
    p_new_perspective_events,
    p_instance_id,
    v_lease_expiry,
    p_now
  ) AS spe
  WHERE spe.was_newly_created = true;

  -- ========================================
  -- Phase 4.5: Event Storage
  -- ========================================
  -- Store events from newly created outbox/inbox messages to wh_event_store
  -- with sequential versioning and optimistic concurrency control.
  -- This is the authoritative event storage - all events flow through process_work_batch.
  -- Uses array tracking to capture successfully stored events for Phase 4.6/4.7 filtering.

  -- Phase 4.5A: Store events from outbox messages with tracking
  WITH outbox_events AS (
    SELECT
      o.message_id,
      o.stream_id,
      o.event_type,
      o.event_data,
      o.metadata,
      o.scope,
      o.created_at,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.created_at) as row_num
    FROM wh_outbox o
    WHERE o.message_id IN (SELECT message_id FROM temp_new_outbox)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  outbox_base_versions AS (
    SELECT
      oe.stream_id,
      oe.message_id,
      oe.event_type,
      oe.event_data,
      oe.metadata,
      oe.scope,
      oe.created_at,
      oe.row_num,
      COALESCE(
        (SELECT MAX(version) FROM wh_event_store WHERE wh_event_store.stream_id = oe.stream_id),
        0
      ) as base_version
    FROM outbox_events oe
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
      sequence_number,
      version,
      created_at
    )
    SELECT
      bv.message_id as event_id,
      bv.stream_id,
      bv.stream_id as aggregate_id,
      SPLIT_PART(normalize_event_type(bv.event_type), ',', 1) as aggregate_type,
      normalize_event_type(bv.event_type),
      -- Extract just the Payload from the envelope for event_data
      (bv.event_data::jsonb -> 'Payload') as event_data,
      -- Build EnvelopeMetadata structure (MessageId + Hops) for metadata
      jsonb_build_object(
        'MessageId', bv.event_data::jsonb -> 'MessageId',
        'Hops', COALESCE(bv.event_data::jsonb -> 'Hops', '[]'::jsonb)
      ) as metadata,
      bv.scope,
      nextval('wh_event_sequence'),
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
      bv.event_type
    FROM outbox_base_versions bv
    WHERE NOT EXISTS (
      SELECT 1 FROM stored_events se WHERE se.event_id = bv.message_id
    )
  )
  SELECT
    array_agg(se.event_id),
    (SELECT COUNT(*) FROM conflict_events),
    (SELECT array_agg(DISTINCT ce.event_type) FROM conflict_events ce)
  INTO v_stored_outbox_events, v_outbox_conflict_count, v_outbox_conflict_types
  FROM stored_events se;

  -- Ensure array is never NULL
  v_stored_outbox_events := COALESCE(v_stored_outbox_events, '{}');

  -- Log warnings for idempotent conflicts (if any)
  IF v_outbox_conflict_count > 0 THEN
    PERFORM log_event(
      2,  -- Warning level
      'process_work_batch',
      format('Event already exists (idempotent): %s outbox events skipped', v_outbox_conflict_count),
      NULL,  -- No specific event_id (multiple)
      NULL,  -- No specific message_id
      NULL,  -- No specific event_type
      jsonb_build_object(
        'phase', '4.5A',
        'source', 'outbox',
        'skipped_count', v_outbox_conflict_count,
        'event_types', v_outbox_conflict_types
      )
    );
  END IF;

  -- Phase 4.5B: Store events from inbox messages with tracking
  WITH inbox_events AS (
    SELECT
      i.message_id,
      i.stream_id,
      i.event_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.received_at,
      ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at) as row_num
    FROM wh_inbox i
    WHERE i.message_id IN (SELECT message_id FROM temp_new_inbox)
      AND i.is_event = true
      AND i.stream_id IS NOT NULL
  ),
  inbox_base_versions AS (
    SELECT
      ie.stream_id,
      ie.message_id,
      ie.event_type,
      ie.event_data,
      ie.metadata,
      ie.scope,
      ie.received_at,
      ie.row_num,
      COALESCE(
        (SELECT MAX(version) FROM wh_event_store WHERE wh_event_store.stream_id = ie.stream_id),
        0
      ) as base_version
    FROM inbox_events ie
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
      sequence_number,
      version,
      created_at
    )
    SELECT
      bv.message_id as event_id,
      bv.stream_id,
      bv.stream_id as aggregate_id,
      SPLIT_PART(normalize_event_type(bv.event_type), ',', 1) as aggregate_type,
      normalize_event_type(bv.event_type),
      -- Extract just the Payload from the envelope for event_data
      (bv.event_data::jsonb -> 'Payload') as event_data,
      -- Build EnvelopeMetadata structure (MessageId + Hops) for metadata
      jsonb_build_object(
        'MessageId', bv.event_data::jsonb -> 'MessageId',
        'Hops', COALESCE(bv.event_data::jsonb -> 'Hops', '[]'::jsonb)
      ) as metadata,
      bv.scope,
      nextval('wh_event_sequence'),
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
      bv.event_type
    FROM inbox_base_versions bv
    WHERE NOT EXISTS (
      SELECT 1 FROM stored_events se WHERE se.event_id = bv.message_id
    )
  )
  SELECT
    array_agg(se.event_id),
    (SELECT COUNT(*) FROM conflict_events),
    (SELECT array_agg(DISTINCT ce.event_type) FROM conflict_events ce)
  INTO v_stored_inbox_events, v_inbox_conflict_count, v_inbox_conflict_types
  FROM stored_events se;

  -- Ensure array is never NULL
  v_stored_inbox_events := COALESCE(v_stored_inbox_events, '{}');

  -- Log warnings for idempotent conflicts (if any)
  IF v_inbox_conflict_count > 0 THEN
    PERFORM log_event(
      2,  -- Warning level
      'process_work_batch',
      format('Event already exists (idempotent): %s inbox events skipped', v_inbox_conflict_count),
      NULL,  -- No specific event_id (multiple)
      NULL,  -- No specific message_id
      NULL,  -- No specific event_type
      jsonb_build_object(
        'phase', '4.5B',
        'source', 'inbox',
        'skipped_count', v_inbox_conflict_count,
        'event_types', v_inbox_conflict_types
      )
    );
  END IF;

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
    sequence_number,
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
    es.sequence_number,
    1 as status,  -- Stored flag
    0 as attempts,
    p_now as created_at,
    p_instance_id as instance_id,  -- Immediate lease to current instance
    v_lease_expiry as lease_expiry
  FROM wh_event_store es
  INNER JOIN wh_message_associations ma
    ON (
      -- Strategy 1: Exact match (fastest, try first)
      es.event_type = ma.message_type
      OR
      -- Strategy 2: Fuzzy match on "TypeName, AssemblyName" portion
      (
        CASE
          WHEN POSITION(', Version=' IN es.event_type) > 0
            THEN SUBSTRING(es.event_type FROM 1 FOR POSITION(', Version=' IN es.event_type) - 1)
          WHEN POSITION(', Culture=' IN es.event_type) > 0
            THEN SUBSTRING(es.event_type FROM 1 FOR POSITION(', Culture=' IN es.event_type) - 1)
          WHEN POSITION(', PublicKeyToken=' IN es.event_type) > 0
            THEN SUBSTRING(es.event_type FROM 1 FOR POSITION(', PublicKeyToken=' IN es.event_type) - 1)
          ELSE es.event_type
        END
        =
        CASE
          WHEN POSITION(', Version=' IN ma.message_type) > 0
            THEN SUBSTRING(ma.message_type FROM 1 FOR POSITION(', Version=' IN ma.message_type) - 1)
          WHEN POSITION(', Culture=' IN ma.message_type) > 0
            THEN SUBSTRING(ma.message_type FROM 1 FOR POSITION(', Culture=' IN ma.message_type) - 1)
          WHEN POSITION(', PublicKeyToken=' IN ma.message_type) > 0
            THEN SUBSTRING(ma.message_type FROM 1 FOR POSITION(', PublicKeyToken=' IN ma.message_type) - 1)
          ELSE ma.message_type
        END
      )
    )
    AND ma.association_type = 'perspective'
  WHERE es.event_id = ANY(v_stored_outbox_events || v_stored_inbox_events)
    AND NOT EXISTS (
      SELECT 1 FROM wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;  -- Idempotency

  -- ========================================
  -- Phase 4.7: Auto-Create Perspective Checkpoints
  -- ========================================
  -- When events are stored, automatically create checkpoint rows for any streams
  -- that have events matching perspective associations but don't have checkpoints yet.
  -- Uses fuzzy type matching to handle different .NET type name formats.
  -- Only processes events successfully stored in Phase 4.5 (tracked via arrays).
  INSERT INTO wh_perspective_checkpoints (
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
    ON (
      -- Strategy 1: Exact match (fastest, try first)
      es.event_type = ma.message_type
      OR
      -- Strategy 2: Fuzzy match on "TypeName, AssemblyName" portion
      -- Ignores Version, Culture, PublicKeyToken differences
      (
        -- Extract core identifier from event_type (up to first ", Version=" if present)
        CASE
          WHEN POSITION(', Version=' IN es.event_type) > 0
            THEN SUBSTRING(es.event_type FROM 1 FOR POSITION(', Version=' IN es.event_type) - 1)
          WHEN POSITION(', Culture=' IN es.event_type) > 0
            THEN SUBSTRING(es.event_type FROM 1 FOR POSITION(', Culture=' IN es.event_type) - 1)
          WHEN POSITION(', PublicKeyToken=' IN es.event_type) > 0
            THEN SUBSTRING(es.event_type FROM 1 FOR POSITION(', PublicKeyToken=' IN es.event_type) - 1)
          ELSE es.event_type
        END
        =
        -- Extract core identifier from message_type
        CASE
          WHEN POSITION(', Version=' IN ma.message_type) > 0
            THEN SUBSTRING(ma.message_type FROM 1 FOR POSITION(', Version=' IN ma.message_type) - 1)
          WHEN POSITION(', Culture=' IN ma.message_type) > 0
            THEN SUBSTRING(ma.message_type FROM 1 FOR POSITION(', Culture=' IN ma.message_type) - 1)
          WHEN POSITION(', PublicKeyToken=' IN ma.message_type) > 0
            THEN SUBSTRING(ma.message_type FROM 1 FOR POSITION(', PublicKeyToken=' IN ma.message_type) - 1)
          ELSE ma.message_type
        END
      )
    )
    AND ma.association_type = 'perspective'
  WHERE es.event_id = ANY(v_stored_outbox_events || v_stored_inbox_events)
    AND NOT EXISTS (
      SELECT 1 FROM wh_perspective_checkpoints pc_check
      WHERE pc_check.stream_id = es.stream_id
        AND pc_check.perspective_name = ma.target_name
    )
  ON CONFLICT DO NOTHING;  -- Idempotency - relies on primary key (stream_id, perspective_name)

  -- ========================================
  -- Phase 5: Claiming (Orphaned Work)
  -- ========================================

  -- Claim orphaned outbox and track
  INSERT INTO temp_orphaned_outbox (message_id, stream_id)
  SELECT coo.message_id, coo.stream_id
  FROM claim_orphaned_outbox(
    p_instance_id,
    v_rank,
    v_count,
    v_lease_expiry,
    p_now,
    p_partition_count
  ) AS coo;

  -- Claim orphaned inbox and track
  INSERT INTO temp_orphaned_inbox (message_id, stream_id)
  SELECT coi.message_id, coi.stream_id
  FROM claim_orphaned_inbox(
    p_instance_id,
    v_rank,
    v_count,
    v_lease_expiry,
    p_now,
    p_partition_count
  ) AS coi;

  -- Claim orphaned receptor work and track
  INSERT INTO temp_orphaned_receptor (processing_id, stream_id)
  SELECT cor.processing_id, cor.stream_id
  FROM claim_orphaned_receptor_work(
    p_instance_id,
    v_rank,
    v_count,
    v_lease_expiry,
    p_now
  ) AS cor;

  -- Claim orphaned perspective events and track
  INSERT INTO temp_orphaned_perspective_events (event_work_id, stream_id, perspective_name)
  SELECT cope.event_work_id, cope.stream_id, cope.perspective_name
  FROM claim_orphaned_perspective_events(
    p_instance_id,
    v_lease_expiry,
    p_now
  ) AS cope;

  -- ========================================
  -- Phase 6: Lease Renewals
  -- ========================================

  -- Renew outbox leases
  UPDATE wh_outbox
  SET lease_expiry = v_lease_expiry
  WHERE instance_id = p_instance_id
    AND message_id = ANY(
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_outbox_lease_ids) as elem
    );

  -- Renew inbox leases
  UPDATE wh_inbox
  SET lease_expiry = v_lease_expiry
  WHERE instance_id = p_instance_id
    AND message_id = ANY(
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_inbox_lease_ids) as elem
    );

  -- Renew perspective event leases
  UPDATE wh_perspective_events
  SET lease_expiry = v_lease_expiry
  WHERE instance_id = p_instance_id
    AND event_work_id = ANY(
      SELECT (elem::TEXT)::UUID
      FROM jsonb_array_elements_text(p_renew_perspective_event_lease_ids) as elem
    );

  -- ========================================
  -- Phase 7: Return Results
  -- ========================================

  -- Return outbox work
  RETURN QUERY
  SELECT
    v_rank as instance_rank,
    v_count as active_instance_count,
    'outbox'::VARCHAR(20) as source,
    o.message_id as work_id,
    o.stream_id as work_stream_id,
    o.partition_number,
    o.destination as destination,
    o.event_type as message_type,
    o.envelope_type as envelope_type,
    o.event_data::TEXT as message_data,
    o.metadata,
    o.status,
    o.attempts,
    CASE WHEN temp_new.message_id IS NOT NULL THEN true ELSE false END as is_newly_stored,
    CASE WHEN temp_orphaned.message_id IS NOT NULL THEN true ELSE false END as is_orphaned,
    NULL::TEXT as error,
    NULL::INTEGER as failure_reason,
    NULL::VARCHAR(200) as perspective_name,
    NULL::BIGINT as sequence_number
  FROM wh_outbox o
  LEFT JOIN temp_new_outbox temp_new ON o.message_id = temp_new.message_id
  LEFT JOIN temp_orphaned_outbox temp_orphaned ON o.message_id = temp_orphaned.message_id
  WHERE (temp_new.message_id IS NOT NULL OR temp_orphaned.message_id IS NOT NULL)
    AND o.instance_id = p_instance_id
    AND o.lease_expiry > p_now
    AND o.processed_at IS NULL;

  -- Return inbox work
  RETURN QUERY
  SELECT
    v_rank as instance_rank,
    v_count as active_instance_count,
    'inbox'::VARCHAR(20) as source,
    i.message_id as work_id,
    i.stream_id as work_stream_id,
    i.partition_number,
    i.handler_name as destination,
    i.event_type as message_type,
    NULL::VARCHAR(500) as envelope_type,
    i.event_data::TEXT as message_data,
    i.metadata,
    i.status,
    i.attempts,
    CASE WHEN temp_new.message_id IS NOT NULL THEN true ELSE false END as is_newly_stored,
    CASE WHEN temp_orphaned.message_id IS NOT NULL THEN true ELSE false END as is_orphaned,
    NULL::TEXT as error,
    NULL::INTEGER as failure_reason,
    NULL::VARCHAR(200) as perspective_name,
    NULL::BIGINT as sequence_number
  FROM wh_inbox i
  LEFT JOIN temp_new_inbox temp_new ON i.message_id = temp_new.message_id
  LEFT JOIN temp_orphaned_inbox temp_orphaned ON i.message_id = temp_orphaned.message_id
  WHERE (temp_new.message_id IS NOT NULL OR temp_orphaned.message_id IS NOT NULL)
    AND i.instance_id = p_instance_id
    AND i.lease_expiry > p_now
    AND i.processed_at IS NULL;

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
    NULL::VARCHAR(200) as perspective_name,
    NULL::BIGINT as sequence_number
  FROM wh_receptor_processing rp
  LEFT JOIN temp_orphaned_receptor temp_orphaned ON rp.id = temp_orphaned.processing_id
  WHERE rp.instance_id = p_instance_id
    AND rp.lease_expiry > p_now
    AND rp.completed_at IS NULL;

  -- Return perspective work
  RETURN QUERY
  SELECT
    v_rank as instance_rank,
    v_count as active_instance_count,
    'perspective'::VARCHAR(20) as source,
    pe.event_work_id as work_id,
    pe.stream_id as work_stream_id,
    NULL::INTEGER as partition_number,  -- Perspectives don't use partition-based load balancing
    NULL::VARCHAR(200) as destination,
    NULL::VARCHAR(500) as message_type,  -- Event type comes from wh_event_store
    NULL::VARCHAR(500) as envelope_type, -- Event envelope type comes from wh_event_store
    NULL::TEXT as message_data,          -- Event data comes from wh_event_store
    NULL::JSONB as metadata,             -- Event metadata comes from wh_event_store
    pe.status,
    pe.attempts,
    CASE WHEN temp_new.event_work_id IS NOT NULL THEN true ELSE false END as is_newly_stored,
    CASE WHEN temp_orphaned.event_work_id IS NOT NULL THEN true ELSE false END as is_orphaned,
    NULL::TEXT as error,
    NULL::INTEGER as failure_reason,
    pe.perspective_name,
    pe.sequence_number
  FROM wh_perspective_events pe
  LEFT JOIN temp_new_perspective_events temp_new ON pe.event_work_id = temp_new.event_work_id
  LEFT JOIN temp_orphaned_perspective_events temp_orphaned ON pe.event_work_id = temp_orphaned.event_work_id
  WHERE pe.instance_id = p_instance_id
    AND pe.lease_expiry > p_now
    AND pe.processed_at IS NULL
  ORDER BY pe.stream_id, pe.perspective_name, pe.sequence_number;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION process_work_batch IS
'Orchestrator function that coordinates all work batch processing. Registers heartbeat, processes completions/failures, stores new work, claims orphaned work, renews leases, and returns aggregated work batch. All operations occur in a single transaction for atomicity.';
