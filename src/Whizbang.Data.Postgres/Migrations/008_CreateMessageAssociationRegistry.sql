-- Migration: 007_CreateRegisterMessageAssociationsFunction
-- Description: Creates register_message_associations() reconciliation function
-- Date: 2025-12-21
--
-- This migration creates the reconciliation function for message associations.
-- The wh_message_associations table is created via MessageAssociationsSchema.cs.
--
-- The reconciliation function is called during startup to sync associations from source generators
-- with the database, enabling the work coordinator to auto-create checkpoints when events arrive.

-- ============================================================================
-- register_message_associations Function
-- ============================================================================
-- Reconciliation function called during startup to sync associations from C# code to database
-- Performs upsert (INSERT...ON CONFLICT UPDATE) and deletes orphaned associations
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
--
-- Returns: TABLE with reconciliation statistics
--   inserted_count INT - Number of new associations inserted
--   updated_count INT - Number of existing associations updated
--   deleted_count INT - Number of orphaned associations deleted

CREATE OR REPLACE FUNCTION register_message_associations(
  p_associations JSONB
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
  FROM wh_message_associations wma
  INNER JOIN temp_associations ta
    ON wma.message_type = ta.message_type
    AND wma.association_type = ta.association_type
    AND wma.target_name = ta.target_name
    AND wma.service_name = ta.service_name;

  -- Now perform the upsert
  INSERT INTO wh_message_associations (message_type, association_type, target_name, service_name, created_at, updated_at)
  SELECT
    message_type,
    association_type,
    target_name,
    service_name,
    NOW(),
    NOW()
  FROM temp_associations
  ON CONFLICT (message_type, association_type, target_name, service_name)
  DO UPDATE SET
    updated_at = NOW();

  -- Calculate inserted count (total - updated)
  SELECT COUNT(*) - v_updated_count INTO v_inserted_count FROM temp_associations;

  -- Delete associations not in the incoming set (orphaned associations)
  WITH delete_result AS (
    DELETE FROM wh_message_associations wma
    WHERE NOT EXISTS (
      SELECT 1
      FROM temp_associations ta
      WHERE ta.message_type = wma.message_type
        AND ta.association_type = wma.association_type
        AND ta.target_name = wma.target_name
        AND ta.service_name = wma.service_name
    )
    RETURNING *
  )
  SELECT COUNT(*) INTO v_deleted_count FROM delete_result;

  -- Return reconciliation statistics
  RETURN QUERY SELECT v_inserted_count, v_updated_count, v_deleted_count;
END;
$$ LANGUAGE plpgsql;

-- Grant execute permission on function
GRANT EXECUTE ON FUNCTION register_message_associations(JSONB) TO PUBLIC;
