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
--
-- Column aliases inside the function use v_* / o_* prefixes to avoid PL/pgSQL
-- variable-vs-column ambiguity errors (42702) against the table's column names.

SELECT __SCHEMA__.drop_all_overloads('reconcile_message_type_registry');

CREATE OR REPLACE FUNCTION __SCHEMA__.reconcile_message_type_registry(
  p_entries JSONB
)
RETURNS TABLE (
  o_action VARCHAR,
  o_pinned_id UUID,
  o_clr_type_name VARCHAR,
  o_stored_clr_type_name VARCHAR
) AS $$
DECLARE
  v_clr VARCHAR;
  v_pinned TEXT;
  v_kind VARCHAR;
  v_stored_clr VARCHAR;
  v_action VARCHAR;
BEGIN
  FOR v_clr, v_pinned, v_kind IN
    SELECT
      entry->>'ClrTypeName',
      NULLIF(entry->>'PinnedId', ''),
      entry->>'Kind'
    FROM jsonb_array_elements(p_entries) AS entry
  LOOP
    v_stored_clr := NULL;

    IF v_pinned IS NOT NULL THEN
      -- Pinned entry: lookup by pinned_id
      SELECT r.clr_type_name
      INTO v_stored_clr
      FROM __SCHEMA__.wh_message_type_registry r
      WHERE r.pinned_id = v_pinned::uuid;

      IF FOUND THEN
        IF v_stored_clr = v_clr THEN
          -- Match: touch updated_at (and kind in case it changed)
          UPDATE __SCHEMA__.wh_message_type_registry r
          SET kind = v_kind, updated_at = NOW()
          WHERE r.pinned_id = v_pinned::uuid;
          v_action := 'updated';
        ELSIF replace(v_clr, '+', '.') = v_stored_clr THEN
          -- Same pinned type, but the stored value is the legacy '.'-nested spelling of what the catalog
          -- now reports as the CLR '+'-nested form (Type.FullName). This is a nesting-separator
          -- NORMALIZATION, not a rename, so migrate it in place. Drop any pre-existing '+' row belonging
          -- to a different identity first so renaming the clr_type_name primary key cannot collide.
          -- (Data migration 063 normalizes rows it can derive a '+' oracle for; this reaches the rest —
          -- e.g. perspective types with no message associations — using the compile-time catalog.)
          DELETE FROM __SCHEMA__.wh_message_type_registry
            WHERE clr_type_name = v_clr AND pinned_id IS DISTINCT FROM v_pinned::uuid;
          UPDATE __SCHEMA__.wh_message_type_registry r
          SET clr_type_name = v_clr, kind = v_kind, updated_at = NOW()
          WHERE r.pinned_id = v_pinned::uuid;
          v_action := 'updated';
        ELSE
          -- Genuine drift (rename): do not overwrite
          v_action := 'drift_detected';
        END IF;
      ELSE
        -- No row for this pinned_id. Insert; handle conflict with an existing
        -- unpinned row that has the same clr_type_name by adopting it.
        INSERT INTO __SCHEMA__.wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
        VALUES (v_clr, v_pinned::uuid, v_kind, NOW())
        ON CONFLICT (clr_type_name) DO UPDATE
          SET pinned_id = EXCLUDED.pinned_id,
              kind = EXCLUDED.kind,
              updated_at = NOW()
          WHERE __SCHEMA__.wh_message_type_registry.pinned_id IS NULL;
        v_action := 'inserted';
      END IF;
    ELSE
      -- Unpinned entry: upsert by clr_type_name. First drop any legacy '.'-nested encoding of this
      -- same type (unpinned only) so the canonical '+'-form doesn't leave an orphan duplicate beside it.
      DELETE FROM __SCHEMA__.wh_message_type_registry
        WHERE clr_type_name = replace(v_clr, '+', '.')
          AND replace(v_clr, '+', '.') <> v_clr
          AND pinned_id IS NULL;
      INSERT INTO __SCHEMA__.wh_message_type_registry (clr_type_name, kind, updated_at)
      VALUES (v_clr, v_kind, NOW())
      ON CONFLICT (clr_type_name) DO UPDATE
        SET kind = EXCLUDED.kind, updated_at = NOW();
      v_action := 'inserted';
    END IF;

    o_action := v_action::VARCHAR;
    o_pinned_id := CASE WHEN v_pinned IS NULL THEN NULL::uuid ELSE v_pinned::uuid END;
    o_clr_type_name := v_clr::VARCHAR;
    o_stored_clr_type_name := v_stored_clr::VARCHAR;
    RETURN NEXT;
  END LOOP;

  RETURN;
END;
$$ LANGUAGE plpgsql;

GRANT EXECUTE ON FUNCTION __SCHEMA__.reconcile_message_type_registry(JSONB) TO PUBLIC;
