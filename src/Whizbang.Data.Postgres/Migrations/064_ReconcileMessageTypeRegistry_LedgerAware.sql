-- Migration 064: reconcile_message_type_registry() becomes ledger-aware
--
-- Supersedes the drift branch of 040. When a pinned_id's stored clr_type_name differs from the
-- code's current name, 040 always reported 'drift_detected' and left the row alone — so a rename
-- left the registry permanently stale until the (destructive, manual) IEventTypeRenameTool was run.
--
-- The rename platform's committed ledger (.whizbang/pinned-type-ledger.json) now records each type's
-- FORMER names. The catalog carries them to this function in each entry's "FormerNames" array. This
-- version adds an ACKNOWLEDGED-RENAME branch: when the stored (old) name is a recorded former name,
-- the rename was reviewed + committed, so update the registry row old -> new in place. Non-destructive
-- to event data (perspective/message read models are not keyed by clr_type_name); the ledger remains
-- the sole history. Unacknowledged drift (old name NOT in FormerNames) still reports 'drift_detected'
-- and is left untouched — an accidental/ungoverned rename must be recorded in the ledger first.
--
-- Backward compatible both ways during a rolling deploy:
--   * old populator + new function — payload has no "FormerNames" => empty => behaves exactly like 040.
--   * new populator + old function — extra "FormerNames" field is ignored => 'drift_detected' (safe).
--
-- Input: p_entries JSONB array of
--   [{ "ClrTypeName": "...", "PinnedId": "guid-or-null", "Kind": "...", "FormerNames": ["old", ...] }, ...]
-- Returns: action ('inserted' | 'updated' | 'renamed' | 'drift_detected'), pinned_id, clr_type_name,
--          stored_clr_type_name.

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
  v_former_names TEXT[];
  v_stored_clr VARCHAR;
  v_action VARCHAR;
BEGIN
  FOR v_clr, v_pinned, v_kind, v_former_names IN
    SELECT
      entry->>'ClrTypeName',
      NULLIF(entry->>'PinnedId', ''),
      entry->>'Kind',
      CASE WHEN jsonb_typeof(entry->'FormerNames') = 'array'
           THEN ARRAY(SELECT jsonb_array_elements_text(entry->'FormerNames'))
           ELSE ARRAY[]::text[] END
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
          -- Same pinned type, stored value is the legacy '.'-nested spelling of the catalog's CLR
          -- '+'-nested form. A nesting-separator NORMALIZATION, not a rename — migrate in place.
          -- Drop any pre-existing '+' row of a different identity first so the rename can't collide.
          DELETE FROM __SCHEMA__.wh_message_type_registry
            WHERE clr_type_name = v_clr AND pinned_id IS DISTINCT FROM v_pinned::uuid;
          UPDATE __SCHEMA__.wh_message_type_registry r
          SET clr_type_name = v_clr, kind = v_kind, updated_at = NOW()
          WHERE r.pinned_id = v_pinned::uuid;
          v_action := 'updated';
        ELSIF v_stored_clr = ANY(v_former_names) THEN
          -- ACKNOWLEDGED RENAME: the stored (old) name is a recorded former name in the committed
          -- ledger, so the rename was reviewed + accepted. Rewrite the registry row old -> new in
          -- place. Drop any pre-existing row already holding the new name under a different identity
          -- first so the clr_type_name unique key cannot collide (mirrors the normalization branch).
          DELETE FROM __SCHEMA__.wh_message_type_registry
            WHERE clr_type_name = v_clr AND pinned_id IS DISTINCT FROM v_pinned::uuid;
          UPDATE __SCHEMA__.wh_message_type_registry r
          SET clr_type_name = v_clr, kind = v_kind, updated_at = NOW()
          WHERE r.pinned_id = v_pinned::uuid;
          v_action := 'renamed';
        ELSE
          -- Unacknowledged drift (rename not recorded in the ledger): do not overwrite.
          v_action := 'drift_detected';
        END IF;
      ELSE
        -- No row for this pinned_id. Insert; adopt an existing unpinned row of the same name.
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
      -- Unpinned entry: upsert by clr_type_name. Drop any legacy '.'-nested encoding of this same
      -- type (unpinned only) so the canonical '+'-form doesn't leave an orphan duplicate beside it.
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
