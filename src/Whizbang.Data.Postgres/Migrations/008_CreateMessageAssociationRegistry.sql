-- Migration: 007_CreateRegisterMessageAssociationsFunction
-- Description: Creates register_message_associations() reconciliation function
-- Date: 2025-12-21
--
-- This migration creates the reconciliation function for message associations.
-- The wh_message_associations table is created via MessageAssociationsSchema.cs.
--
-- The reconciliation function is called during startup to sync associations from source generators
-- with the database, enabling the work coordinator to auto-create checkpoints when events arrive.

-- Ensure normalized_message_type column exists BEFORE function definition (idempotent)
-- Must precede CREATE FUNCTION because PL/pgSQL validates column references at creation time
ALTER TABLE __SCHEMA__.wh_message_associations ADD COLUMN IF NOT EXISTS normalized_message_type VARCHAR(500);

-- Index for JOIN performance in Phase 4.6/4.7 (uses normalized_message_type instead of function calls)
CREATE INDEX IF NOT EXISTS idx_message_associations_normalized_type
ON __SCHEMA__.wh_message_associations (normalized_message_type, association_type);

-- ============================================================================
-- register_message_associations Function
-- ============================================================================
-- Reconciliation function called during startup to sync associations from C# code to database.
-- Performs upsert (INSERT...ON CONFLICT UPDATE), deletes orphaned associations scoped to the
-- calling service, and cascade-cleans pending wh_perspective_events for removed (perspective,
-- event_type) pairs so stale work items don't sit in the queue forever.
--
-- Parameters:
--   p_associations JSONB - Array of association objects with structure:
--     [
--       {
--         "MessageType": "Fully.Qualified.TypeName",
--         "AssociationType": "perspective|handler|receptor",
--         "TargetName": "PerspectiveClassName",
--         "ServiceName": "AssemblyName"
--       }
--     ]
--   p_service_name VARCHAR - Assembly/service name. Orphan DELETE is scoped to this service
--     so one service's reconciliation can never wipe another service's rows when a schema is
--     shared. Required — callers that omit it would silently fall back to wiping everything.
--
-- Returns: TABLE with reconciliation statistics
--   inserted_count INT - Number of new associations inserted
--   updated_count INT - Number of existing associations updated
--   deleted_count INT - Number of orphaned associations deleted

SELECT __SCHEMA__.drop_all_overloads('register_message_associations');

CREATE OR REPLACE FUNCTION __SCHEMA__.register_message_associations(
  p_associations JSONB,
  p_service_name VARCHAR(500)
)
RETURNS TABLE (
  inserted_count INT,
  updated_count INT,
  deleted_count INT
) AS $$
DECLARE
  v_inserted_count INT := 0;
  v_updated_count INT := 0;
  v_deleted_count INT := 0;
BEGIN
  -- Create temporary table for incoming associations
  DROP TABLE IF EXISTS temp_associations;

  CREATE TEMP TABLE temp_associations (
    message_type VARCHAR(500),
    association_type VARCHAR(50),
    target_name VARCHAR(500),
    service_name VARCHAR(500)
  ) ON COMMIT DROP;

  -- Parse JSONB array into temp table
  INSERT INTO temp_associations (message_type, association_type, target_name, service_name)
  SELECT
    assoc->>'MessageType',
    assoc->>'AssociationType',
    assoc->>'TargetName',
    assoc->>'ServiceName'
  FROM jsonb_array_elements(p_associations) AS assoc;

  -- Insert new associations or update updated_at on conflict
  -- First count how many associations already exist (will be updated)
  SELECT COUNT(*) INTO v_updated_count
  FROM __SCHEMA__.wh_message_associations wma
  INNER JOIN temp_associations ta
    ON wma.message_type = ta.message_type
    AND wma.association_type = ta.association_type
    AND wma.target_name = ta.target_name
    AND wma.service_name = ta.service_name;

  -- Now perform the upsert (including pre-computed normalized_message_type for index-friendly JOINs)
  INSERT INTO __SCHEMA__.wh_message_associations (message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
  SELECT
    message_type,
    association_type,
    target_name,
    service_name,
    __SCHEMA__.normalize_event_type(message_type),
    NOW(),
    NOW()
  FROM temp_associations
  ON CONFLICT (message_type, association_type, target_name, service_name)
  DO UPDATE SET
    updated_at = NOW(),
    normalized_message_type = __SCHEMA__.normalize_event_type(EXCLUDED.message_type);

  -- Calculate inserted count (total - updated)
  SELECT COUNT(*) - v_updated_count INTO v_inserted_count FROM temp_associations;

  -- Capture-and-delete orphans scoped to p_service_name. Filtering by service_name is
  -- defense-in-depth: callers always pass their own service, but without this filter a shared
  -- schema would have each service wipe the others on its own startup. The CTE retains the
  -- deleted rows so we can cascade-clean the perspective-events work queue below.
  DROP TABLE IF EXISTS temp_deleted_associations;
  CREATE TEMP TABLE temp_deleted_associations (
    message_type VARCHAR(500),
    association_type VARCHAR(50),
    target_name VARCHAR(500),
    service_name VARCHAR(500),
    normalized_message_type VARCHAR(500)
  ) ON COMMIT DROP;

  WITH delete_result AS (
    DELETE FROM __SCHEMA__.wh_message_associations wma
    WHERE wma.service_name = p_service_name
      AND NOT EXISTS (
        SELECT 1
        FROM temp_associations ta
        WHERE ta.message_type = wma.message_type
          AND ta.association_type = wma.association_type
          AND ta.target_name = wma.target_name
          AND ta.service_name = wma.service_name
      )
    RETURNING wma.message_type, wma.association_type, wma.target_name, wma.service_name, wma.normalized_message_type
  )
  INSERT INTO temp_deleted_associations
  SELECT * FROM delete_result;

  SELECT COUNT(*) INTO v_deleted_count FROM temp_deleted_associations;

  -- Cascade: delete pending (status=0) wh_perspective_events rows whose (perspective, event_type)
  -- is no longer associated. status != 0 rows (in-progress, completed, failed) are preserved so
  -- audit/debug state is not disturbed. Joins through wh_event_store.event_type which is already
  -- stored in normalized form (see process_work_batch Phase 4.5).
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  USING wh_event_store es, temp_deleted_associations tda
  WHERE pe.event_id = es.event_id
    AND pe.status = 0
    AND tda.association_type = 'perspective'
    AND pe.perspective_name = tda.target_name
    AND es.event_type = tda.normalized_message_type;

  -- Return reconciliation statistics
  RETURN QUERY SELECT v_inserted_count, v_updated_count, v_deleted_count;
END;
$$ LANGUAGE plpgsql;

-- Grant execute permission on function
GRANT EXECUTE ON FUNCTION register_message_associations(JSONB, VARCHAR) TO PUBLIC;

-- Backfill normalized_message_type for existing rows (idempotent — safe to re-run)
UPDATE __SCHEMA__.wh_message_associations
SET normalized_message_type = __SCHEMA__.normalize_event_type(message_type)
WHERE normalized_message_type IS NULL;
