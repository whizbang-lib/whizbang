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
  applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_schema_versions IS 'Tracks library versions that have applied migrations. Each version maps to a set of migration hashes.';
COMMENT ON TABLE __SCHEMA__.wh_schema_migrations IS 'Tracks individual migration files by content hash for skip-on-unchanged detection. Status: 0=Pending, 1=Applied, 2=Updated, 3=Skipped, -1=Failed.';
