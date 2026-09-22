-- Migration: 142_ReconcileAdoptsDottedRegistryKey
-- Date: 2026-09-07
-- Description: reconcile_perspective_registry adopts a row keyed in the previous display-string form
--              instead of inserting a duplicate beside it (issue #697).
--
--   The EF registration generator wrote wh_perspective_registry.clr_type_name from a Roslyn display
--   string, which renders a nested model as Outer.Model. The runtime side (the retention
--   declaration built at startup) looks the row up by the CLR form, Outer+Model, so
--   sync_perspective_retention matched zero rows for every nested model and row retention was
--   silently un-enrolled. The generator now writes the CLR form; existing registries still hold
--   the dotted key. reconcile_perspective_registry keys on (clr_type_name, service_name) and
--   inserted a new row when the key was unknown, which would leave the old row stale with the
--   enrollment on neither. No data migration can rewrite the old keys on its own: SQL cannot tell
--   a nesting dot from a namespace dot. The generator can, and the reconcile runs with the
--   generator's output in hand, so the adoption happens there: when the CLR key is not found, the
--   row for the same (table_name, service_name) is renamed to the CLR key and reported as
--   'renamed_key'. Body reproduced verbatim from 030 with that branch added (rule 5).
--
-- Dependencies: 030 (reconcile_perspective_registry), 101/103 (sync_perspective_retention keys on clr_type_name)
-- Objects: reconcile_perspective_registry

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
  v_adopted BOOLEAN;
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

    -- 142: adopt a row written under the previous key form (issue #697). The registry used to be
    -- keyed by a display-string rendering of a nested model (Outer.Model); the canonical key is
    -- the CLR form (Outer+Model), which is what the runtime looks up. A nesting dot cannot be told
    -- from a namespace dot by SQL alone, so the adoption keys on the one thing both forms share:
    -- the table this service registered for the model. The row keeps its enrollment columns.
    v_adopted := FALSE;
    IF NOT FOUND THEN
      SELECT
        pr.table_name,
        pr.schema_hash
      INTO v_existing
      FROM __SCHEMA__.wh_perspective_registry pr
      WHERE pr.table_name = v_perspective.table_name
        AND pr.service_name = v_perspective.service_name
        AND pr.clr_type_name <> v_perspective.clr_type_name;
      IF FOUND THEN
        UPDATE __SCHEMA__.wh_perspective_registry
        SET clr_type_name = v_perspective.clr_type_name,
            updated_at = NOW()
        WHERE __SCHEMA__.wh_perspective_registry.table_name = v_perspective.table_name
          AND __SCHEMA__.wh_perspective_registry.service_name = v_perspective.service_name;
        v_adopted := TRUE;
      END IF;
    END IF;

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
      ELSIF v_adopted THEN
        -- 142: same table, same service, a key written in the previous form: renamed in place
        v_action := 'renamed_key';
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
