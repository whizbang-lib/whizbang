-- Migration 040: reconcile_message_type_registry() function
--
-- Companion to 039_CreateMessageTypeRegistryTable. Consolidates the upsert +
-- drift-detection logic for wh_message_type_registry into a single PL/pgSQL
-- function so both Dapper and EFCore populators call the same code path (same
-- pattern as reconcile_perspective_registry from migration 030).
--
-- Input: p_entries JSONB array of
--   [{
--     "ClrTypeName": "Namespace.Type, Assembly",
--     "PinnedId":    "guid-or-null",
--     "Kind":        "event" | "command" | "perspective"
--   }, ...]
--
-- Returns: one row per input entry with:
--   action              — 'inserted' | 'updated' | 'drift_detected'
--   pinned_id           — entry's pinned_id (may be NULL for unpinned)
--   clr_type_name       — entry's current CLR type name
--   stored_clr_type_name— registry's existing CLR type name (for drift rows, the old value)
--
-- Drift semantics: when a pinned_id exists with a different clr_type_name than
-- the code currently reports, the registry row is NOT overwritten. The caller
-- logs a warning and the IEventTypeRenameTool handles reconciliation.

SELECT __SCHEMA__.drop_all_overloads('reconcile_message_type_registry');

CREATE OR REPLACE FUNCTION __SCHEMA__.reconcile_message_type_registry(
  p_entries JSONB
)
RETURNS TABLE (
  action VARCHAR,
  pinned_id UUID,
  clr_type_name VARCHAR,
  stored_clr_type_name VARCHAR
) AS $$
DECLARE
  v_entry RECORD;
  v_existing RECORD;
  v_action VARCHAR;
  v_stored_clr_type_name VARCHAR;
BEGIN
  FOR v_entry IN
    SELECT
      entry->>'ClrTypeName' AS clr_type_name,
      NULLIF(entry->>'PinnedId', '') AS pinned_id_text,
      entry->>'Kind' AS kind
    FROM jsonb_array_elements(p_entries) AS entry
  LOOP
    v_stored_clr_type_name := NULL;

    IF v_entry.pinned_id_text IS NOT NULL THEN
      -- Pinned entry: lookup by pinned_id
      SELECT r.clr_type_name
      INTO v_existing
      FROM __SCHEMA__.wh_message_type_registry r
      WHERE r.pinned_id = v_entry.pinned_id_text::uuid;

      IF FOUND THEN
        v_stored_clr_type_name := v_existing.clr_type_name;

        IF v_existing.clr_type_name = v_entry.clr_type_name THEN
          -- Match: touch updated_at (and kind in case it changed)
          UPDATE __SCHEMA__.wh_message_type_registry
          SET kind = v_entry.kind, updated_at = NOW()
          WHERE __SCHEMA__.wh_message_type_registry.pinned_id = v_entry.pinned_id_text::uuid;
          v_action := 'updated';
        ELSE
          -- Drift: do not overwrite
          v_action := 'drift_detected';
        END IF;
      ELSE
        -- No row for this pinned_id. Insert; handle conflict with an existing
        -- unpinned row that has the same clr_type_name by adopting it.
        INSERT INTO __SCHEMA__.wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
        VALUES (v_entry.clr_type_name, v_entry.pinned_id_text::uuid, v_entry.kind, NOW())
        ON CONFLICT (clr_type_name) DO UPDATE
          SET pinned_id = EXCLUDED.pinned_id,
              kind = EXCLUDED.kind,
              updated_at = NOW()
          WHERE __SCHEMA__.wh_message_type_registry.pinned_id IS NULL;
        v_action := 'inserted';
      END IF;
    ELSE
      -- Unpinned entry: upsert by clr_type_name
      INSERT INTO __SCHEMA__.wh_message_type_registry (clr_type_name, kind, updated_at)
      VALUES (v_entry.clr_type_name, v_entry.kind, NOW())
      ON CONFLICT (clr_type_name) DO UPDATE
        SET kind = EXCLUDED.kind, updated_at = NOW();
      v_action := 'inserted';
    END IF;

    RETURN QUERY SELECT
      v_action::VARCHAR,
      CASE WHEN v_entry.pinned_id_text IS NULL THEN NULL::uuid ELSE v_entry.pinned_id_text::uuid END,
      v_entry.clr_type_name::VARCHAR,
      v_stored_clr_type_name::VARCHAR;
  END LOOP;

  RETURN;
END;
$$ LANGUAGE plpgsql;

GRANT EXECUTE ON FUNCTION __SCHEMA__.reconcile_message_type_registry(JSONB) TO PUBLIC;
