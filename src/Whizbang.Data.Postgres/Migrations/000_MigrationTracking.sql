-- Migration: 000_MigrationTracking.sql
-- Date: 2026-03-15
-- Description: Creates migration tracking tables for hash-based change detection.
-- This migration is always executed (cannot be hash-checked since it creates the hash tables).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_schema_versions (
  id SERIAL PRIMARY KEY,
  library_version VARCHAR(50) NOT NULL UNIQUE,
  notes TEXT,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_schema_migrations (
  file_name VARCHAR(200) PRIMARY KEY,
  content_hash VARCHAR(64) NOT NULL,
  version_id INTEGER NOT NULL REFERENCES __SCHEMA__.wh_schema_versions(id),
  status SMALLINT NOT NULL DEFAULT 0,
  status_description TEXT,
  previous_content TEXT,
  execution_order INTEGER,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Add columns for rollback support and ordering (idempotent for existing installs)
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'wh_schema_migrations' AND column_name = 'previous_content') THEN
    ALTER TABLE __SCHEMA__.wh_schema_migrations ADD COLUMN previous_content TEXT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'wh_schema_migrations' AND column_name = 'execution_order') THEN
    ALTER TABLE __SCHEMA__.wh_schema_migrations ADD COLUMN execution_order INTEGER;
  END IF;
END $$;

COMMENT ON TABLE __SCHEMA__.wh_schema_versions IS 'Tracks library versions that have applied migrations. Each version maps to a set of migration hashes.';
COMMENT ON TABLE __SCHEMA__.wh_schema_migrations IS 'Tracks individual migration files by content hash for skip-on-unchanged detection. Status: 0=Pending, 1=Applied, 2=Updated, 3=Skipped, 4=MigratingInBackground, -1=Failed.';
