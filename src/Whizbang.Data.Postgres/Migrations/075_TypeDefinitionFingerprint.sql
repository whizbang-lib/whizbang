-- Migration: 075_TypeDefinitionFingerprint.sql
-- Date: 2026-07-16
-- Description: Type-definition fingerprint storage substrate (fingerprint F-1). One substrate detects when
--              a type's DEFINITION changed in code and drives reconciliation of the affected stored events,
--              serving ephemeral reclassification (settings), event versioning (schema), and audit.
--              wh_type_definitions: one row per distinct type-definition-version, keyed by its content
--              hashes (settings + schema). wh_definition_lineage: edges describing how one definition
--              superseded another (relationship enum + the developer-authored migration that bridges them).
--              register_type_definition is idempotent by hash and reports whether a definition is new plus
--              the type's previous definition, so the C# reconciler can record a lineage edge. Events do NOT
--              carry per-definition FKs — reclassification reuses the flags ephemeral bit and versioning a
--              small per-event schema_version (see the type-definition-fingerprint proposal).
-- Dependencies: 063 (normalize_event_type)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_type_definitions (
  definition_id  INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_type     VARCHAR(500) NOT NULL,
  settings_hash  BYTEA NOT NULL,
  schema_hash    BYTEA NOT NULL,
  schema_version INT NOT NULL,
  first_seen_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT uq_type_definition UNIQUE (event_type, settings_hash, schema_hash)
);
CREATE INDEX IF NOT EXISTS ix_type_definitions_event_type ON __SCHEMA__.wh_type_definitions (event_type);

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_definition_lineage (
  from_definition_id INT NOT NULL REFERENCES __SCHEMA__.wh_type_definitions(definition_id) ON DELETE CASCADE,
  to_definition_id   INT NOT NULL REFERENCES __SCHEMA__.wh_type_definitions(definition_id) ON DELETE CASCADE,
  relationship       SMALLINT NOT NULL,  -- 0 SchemaUpgradedTo | 1 ReclassifiedTo | 2 MetadataChangedTo
  migration_ref      VARCHAR(500),
  created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (from_definition_id, to_definition_id)
);

-- Idempotent by content hash. Returns (definition_id, is_new, previous_definition_id): the type's current
-- latest definition is reported as previous_definition_id ONLY when a genuinely new definition is inserted,
-- so the reconciler can record a lineage edge from it. Concurrency-safe: a lost insert race resolves to the
-- winner's row as not-new.
CREATE OR REPLACE FUNCTION __SCHEMA__.register_type_definition(
  p_event_type TEXT,
  p_settings_hash BYTEA,
  p_schema_hash BYTEA,
  p_schema_version INTEGER
)
RETURNS TABLE(definition_id INTEGER, is_new BOOLEAN, previous_definition_id INTEGER) AS $$
DECLARE
  v_normalized TEXT;
  v_existing INTEGER;
  v_previous INTEGER;
  v_new INTEGER;
BEGIN
  v_normalized := __SCHEMA__.normalize_event_type(p_event_type);

  -- Fast path: already registered (idempotent by hashes).
  SELECT d.definition_id INTO v_existing
  FROM __SCHEMA__.wh_type_definitions d
  WHERE d.event_type = v_normalized
    AND d.settings_hash = p_settings_hash
    AND d.schema_hash = p_schema_hash;
  IF v_existing IS NOT NULL THEN
    RETURN QUERY SELECT v_existing, FALSE, NULL::INTEGER;
    RETURN;
  END IF;

  -- The type's current latest definition becomes the lineage predecessor of the new one.
  SELECT d.definition_id INTO v_previous
  FROM __SCHEMA__.wh_type_definitions d
  WHERE d.event_type = v_normalized
  ORDER BY d.first_seen_at DESC, d.definition_id DESC
  LIMIT 1;

  INSERT INTO __SCHEMA__.wh_type_definitions (event_type, settings_hash, schema_hash, schema_version)
  VALUES (v_normalized, p_settings_hash, p_schema_hash, p_schema_version)
  ON CONFLICT ON CONSTRAINT uq_type_definition DO NOTHING
  RETURNING wh_type_definitions.definition_id INTO v_new;

  IF v_new IS NULL THEN
    -- Lost a concurrent insert race for this exact definition; resolve to the winner, not-new.
    SELECT d.definition_id INTO v_new
    FROM __SCHEMA__.wh_type_definitions d
    WHERE d.event_type = v_normalized
      AND d.settings_hash = p_settings_hash
      AND d.schema_hash = p_schema_hash;
    RETURN QUERY SELECT v_new, FALSE, NULL::INTEGER;
    RETURN;
  END IF;

  RETURN QUERY SELECT v_new, TRUE, v_previous;
END;
$$ LANGUAGE plpgsql;

-- Upsert a lineage edge (idempotent on the (from,to) PK).
CREATE OR REPLACE FUNCTION __SCHEMA__.record_definition_lineage(
  p_from INTEGER,
  p_to INTEGER,
  p_relationship SMALLINT,
  p_migration_ref TEXT
)
RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_definition_lineage (from_definition_id, to_definition_id, relationship, migration_ref)
  VALUES (p_from, p_to, p_relationship, p_migration_ref)
  ON CONFLICT (from_definition_id, to_definition_id)
  DO UPDATE SET relationship = EXCLUDED.relationship, migration_ref = EXCLUDED.migration_ref;
END;
$$ LANGUAGE plpgsql;

COMMENT ON TABLE __SCHEMA__.wh_type_definitions IS
'Fingerprint F-1: one row per distinct type-definition-version, keyed by its content hashes (settings + schema) with a developer-declared schema_version. Loaded into memory on startup and diffed against the generator-stamped current hashes to detect definition drift.';
COMMENT ON TABLE __SCHEMA__.wh_definition_lineage IS
'Fingerprint F-1: edges describing how one type definition superseded another (relationship enum) and the developer-authored migration (upcaster / reclassify action) that bridges them — makes a stale definition actionable.';
