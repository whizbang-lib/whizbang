-- Migration: 063_NormalizeClrTypeNamesV2.sql
-- Date: 2026-07-04
-- Description: DATA (not schema) migration — normalize stored CLR type names to the canonical
--   '+'-nested form and record the result as a version in wh_settings.
--
--   Why a setting, not the migration-hash mechanism: every other migration is gated by
--   wh_schema_migrations.content_hash — a hash of the *DDL / object shape*. That answers "did the
--   schema change?", which is the wrong question for a data fix: the object shape does NOT change
--   here, so a hash can't tell whether the *rows* still need normalizing. Instead we gate on the
--   wh_settings row 'clr_type_name_format_version' (absent / <3 = needs normalizing, 3 = normalized).
--   That makes startup an O(1) flag read rather than a full-table scan, and gives a queryable "the data
--   is on format v3" fact that survives migration-hash churn.
--
--   What the legacy form looked wrong (both message AND perspective types):
--     * wh_message_type_registry.clr_type_name for NESTED types was written '.'-nested by the old
--       MessageTypeCatalogGenerator (C# display form) instead of the CLR '+'-nested form that
--       Type.FullName / TypeNameUtilities.BuildClrTypeName produce — inconsistent with the perspective
--       registry and undetectable/uncorrectable by reconcile_message_type_registry (pinned rows log
--       'drift_detected' and keep the stale value; unpinned rows insert a duplicate '+' row). This
--       covers MESSAGE types (v2) and PERSPECTIVE types like X+Projection (v3).
--     * aggregate_type written by the Dapper store as the bare simple name (typeof(T).Name) instead of
--       the CLR full name. (EF Core already wrote the full name, so that path is a no-op here.)
--
--   How the '.'->'+' conversion is reliable (no namespace-vs-nesting guessing): the '+'-nested form is
--   read from columns that already store it, and the dotted spelling of a known-good '+' name is exactly
--   replace(name,'+','.'), so a stale registry row is matched precisely. The oracle set unions every
--   column that stores a '+'-nested type name: event_store.event_type + outbox/inbox.message_type +
--   message_associations.message_type (message/command PAYLOAD types) and message_associations.target_name
--   (perspective TYPES — their name never appears in a payload column, only as the association target).
--   Guards: '%+%' restricts to nested names (non-nested names are identical in both forms); NOT LIKE '%[[%'
--   skips generic type names whose inner commas would break split_part.
--
-- Idempotent: gated on the version; re-running after v3 is a no-op RETURN.

DO $migrate$
DECLARE
  v_current_version INTEGER;
  v_agg_updated     BIGINT := 0;
  v_registry_upd    BIGINT := 0;
BEGIN
  -- Legacy installs have no row -> treat as version 1.
  -- wh_settings is created UNqualified in migration 028 (lives in the search_path schema, not
  -- __SCHEMA__), so it must be referenced bare here — exactly as 028/032 do — or a non-public
  -- __SCHEMA__ (e.g. 'inventory') resolves to a table that does not exist (42P01).
  SELECT setting_value::INTEGER
  INTO v_current_version
  FROM wh_settings
  WHERE setting_key = 'clr_type_name_format_version';

  v_current_version := COALESCE(v_current_version, 1);

  -- v3 adds perspective-TYPE registry normalization (via the association target_name oracle) on top of
  -- v2's message-type + aggregate_type normalization. Anything below 3 re-runs the full pass (idempotent).
  IF v_current_version >= 3 THEN
    RAISE NOTICE 'clr_type_name_format_version already % — CLR type names already normalized, skipping.', v_current_version;
    RETURN;
  END IF;

  -- 1. aggregate_type -> CLR full name, derived from the already-correct event_type.
  --    No-op on EF Core-written data; fixes bare-simple-name rows written by the Dapper store.
  UPDATE __SCHEMA__.wh_event_store
  SET aggregate_type = split_part(event_type, ',', 1)
  WHERE event_type NOT LIKE '%[[%'
    AND aggregate_type IS DISTINCT FROM split_part(event_type, ',', 1);
  GET DIAGNOSTICS v_agg_updated = ROW_COUNT;

  -- 2. wh_message_type_registry.clr_type_name : dotted-nested -> plus-nested, matched via the
  --    '+'-nested oracle set. Only NESTED names (containing '+') can differ between the two forms.
  WITH plus_forms AS (
    SELECT DISTINCT split_part(event_type, ',', 1) AS clr
      FROM __SCHEMA__.wh_event_store
      WHERE event_type LIKE '%+%' AND event_type NOT LIKE '%[[%'
    UNION
    SELECT DISTINCT split_part(message_type, ',', 1)
      FROM __SCHEMA__.wh_outbox
      WHERE message_type LIKE '%+%' AND message_type NOT LIKE '%[[%'
    UNION
    SELECT DISTINCT split_part(message_type, ',', 1)
      FROM __SCHEMA__.wh_inbox
      WHERE message_type LIKE '%+%' AND message_type NOT LIKE '%[[%'
    UNION
    SELECT DISTINCT split_part(message_type, ',', 1)
      FROM __SCHEMA__.wh_message_associations
      WHERE message_type LIKE '%+%' AND message_type NOT LIKE '%[[%'
    UNION
    -- Perspective TYPES (e.g. Domain.X+Projection): their '+'-nested name never appears in an
    -- event/message column, only as the association TARGET (written via TypeNameUtilities.BuildClrTypeName).
    -- Without this source the registry's perspective entries stay '.'-nested and reconcile logs a
    -- 'drift_detected' warning for each on every startup.
    SELECT DISTINCT split_part(target_name, ',', 1)
      FROM __SCHEMA__.wh_message_associations
      WHERE target_name LIKE '%+%' AND target_name NOT LIKE '%[[%'
  ),
  type_map AS (
    SELECT clr AS plus_form, replace(clr, '+', '.') AS dotted_form
    FROM plus_forms
  )
  UPDATE __SCHEMA__.wh_message_type_registry r
  SET clr_type_name = m.plus_form, updated_at = NOW()
  FROM type_map m
  WHERE r.clr_type_name = m.dotted_form
    AND r.clr_type_name <> m.plus_form;
  GET DIAGNOSTICS v_registry_upd = ROW_COUNT;

  -- Record the new data-format version so this never re-scans on subsequent startups.
  -- Bare wh_settings (unqualified) — see the read above.
  INSERT INTO wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
  VALUES (
    'clr_type_name_format_version', '3', 'integer',
    'Encoding version of stored CLR type names (wh_event_store.aggregate_type, wh_message_type_registry.clr_type_name). 3 = canonical ''+''-nested CLR full name (Type.FullName) for message AND perspective types. Gates the one-time normalization in migration 063.',
    NOW(), 'migration:063_NormalizeClrTypeNamesV2')
  ON CONFLICT (setting_key) DO UPDATE
    SET setting_value = EXCLUDED.setting_value,
        value_type    = EXCLUDED.value_type,
        description   = EXCLUDED.description,
        updated_at    = NOW(),
        updated_by    = EXCLUDED.updated_by;

  RAISE NOTICE 'Normalized CLR type names to v3 (aggregate_type rows: %, registry clr_type_name rows: %).',
    v_agg_updated, v_registry_upd;
END
$migrate$;
