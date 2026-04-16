-- Migration: 030_ReconcilePerspectiveRegistry
-- Description: Creates reconcile_perspective_registry() function for CLR type → table name tracking
-- Date: 2026-02-20
--
-- This migration creates the reconciliation function for perspective registry.
-- The wh_perspective_registry table is created via PerspectiveRegistrySchema.cs.
--
-- The reconciliation function is called during startup to sync perspective metadata
-- from source generators with the database. This enables:
-- - Schema drift detection (comparing schema_hash)
-- - Auto-migration when table names change (ALTER TABLE RENAME)
-- - CLR type → table name tracking across deployments

-- ============================================================================
-- reconcile_perspective_registry Function
-- ============================================================================
-- Reconciliation function called during startup to sync perspective metadata from C# code to database
-- Performs upsert (INSERT...ON CONFLICT UPDATE) and can rename tables when CLR type changes table name
--
-- Parameters:
--   p_perspectives JSONB - Array of perspective objects with structure:
--     [
--       {
--         "ClrTypeName": "Fully.Qualified.TypeName, AssemblyName",
--         "TableName": "wh_per_order",
--         "SchemaJson": {"columns":[...],"indexes":[...]},
--         "SchemaHash": "64-char-sha256-hex-lowercase",
--         "ServiceName": "MyApp.Api"
--       }
--     ]
--
-- Returns: TABLE with reconciliation results
--   action VARCHAR - 'inserted', 'updated', 'renamed', 'drift_detected'
--   clr_type_name VARCHAR - The CLR type name
--   old_table_name VARCHAR - Previous table name (for renames) or NULL
--   new_table_name VARCHAR - Current/new table name
--   old_schema_hash VARCHAR - Previous schema hash (for drift detection) or NULL
--   new_schema_hash VARCHAR - Current schema hash

SELECT __SCHEMA__.drop_all_overloads('reconcile_perspective_registry');

CREATE OR REPLACE FUNCTION __SCHEMA__.reconcile_perspective_registry(
  p_perspectives JSONB,
  p_service_name VARCHAR DEFAULT NULL
)
RETURNS TABLE (
  action VARCHAR,
  clr_type_name VARCHAR,
  old_table_name VARCHAR,
  new_table_name VARCHAR,
  old_schema_hash VARCHAR,
  new_schema_hash VARCHAR
) AS $$
DECLARE
  v_perspective RECORD;
  v_existing RECORD;
  v_action VARCHAR;
  v_old_table_name VARCHAR;
  v_old_schema_hash VARCHAR;
BEGIN
  -- Process each perspective in the array
  FOR v_perspective IN
    SELECT
      assoc->>'ClrTypeName' AS clr_type_name,
      assoc->>'TableName' AS table_name,
      (assoc->'SchemaJson')::JSONB AS schema_json,
      assoc->>'SchemaHash' AS schema_hash,
      COALESCE(assoc->>'ServiceName', p_service_name) AS service_name
    FROM jsonb_array_elements(p_perspectives) AS assoc
  LOOP
    -- Check if this CLR type already exists in the registry
    SELECT
      pr.table_name,
      pr.schema_hash
    INTO v_existing
    FROM __SCHEMA__.wh_perspective_registry pr
    WHERE pr.clr_type_name = v_perspective.clr_type_name
      AND pr.service_name = v_perspective.service_name;

    IF FOUND THEN
      -- CLR type exists in registry
      v_old_table_name := v_existing.table_name;
      v_old_schema_hash := v_existing.schema_hash;

      -- Check if table name changed
      IF v_existing.table_name != v_perspective.table_name THEN
        -- Table name changed - execute ALTER TABLE RENAME
        BEGIN
          EXECUTE format(
            'ALTER TABLE IF EXISTS %I RENAME TO %I',
            v_existing.table_name,
            v_perspective.table_name
          );
          v_action := 'renamed';
        EXCEPTION WHEN OTHERS THEN
          -- If rename fails (e.g., table doesn't exist), just update registry
          v_action := 'updated';
        END;
      ELSIF v_existing.schema_hash != v_perspective.schema_hash THEN
        -- Schema changed but table name same - drift detected
        v_action := 'drift_detected';
      ELSE
        -- No changes, just update timestamp
        v_action := 'updated';
      END IF;

      -- Update the registry entry
      UPDATE __SCHEMA__.wh_perspective_registry
      SET
        table_name = v_perspective.table_name,
        schema_json = v_perspective.schema_json,
        schema_hash = v_perspective.schema_hash,
        updated_at = NOW()
      WHERE __SCHEMA__.wh_perspective_registry.clr_type_name = v_perspective.clr_type_name
        AND __SCHEMA__.wh_perspective_registry.service_name = v_perspective.service_name;

    ELSE
      -- New CLR type - insert into registry
      v_action := 'inserted';
      v_old_table_name := NULL;
      v_old_schema_hash := NULL;

      INSERT INTO __SCHEMA__.wh_perspective_registry (
        clr_type_name,
        table_name,
        schema_json,
        schema_hash,
        service_name,
        created_at,
        updated_at
      ) VALUES (
        v_perspective.clr_type_name,
        v_perspective.table_name,
        v_perspective.schema_json,
        v_perspective.schema_hash,
        v_perspective.service_name,
        NOW(),
        NOW()
      );
    END IF;

    -- Return the action for this perspective
    -- Explicit casts ensure RECORD column types match RETURNS TABLE exactly
    -- (jsonb ->> returns TEXT, but RETURNS TABLE expects VARCHAR)
    RETURN QUERY SELECT
      v_action::VARCHAR,
      v_perspective.clr_type_name::VARCHAR,
      v_old_table_name::VARCHAR,
      v_perspective.table_name::VARCHAR,
      v_old_schema_hash::VARCHAR,
      v_perspective.schema_hash::VARCHAR;
  END LOOP;

  RETURN;
END;
$$ LANGUAGE plpgsql;

-- Grant execute permission on function
GRANT EXECUTE ON FUNCTION __SCHEMA__.reconcile_perspective_registry(JSONB, VARCHAR) TO PUBLIC;
