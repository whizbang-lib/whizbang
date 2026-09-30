-- Migration: 176_StoredFormMigrations
-- Date: 2026-09-30
-- Description: The journal of app-declared stored-form migrations (#986).
--
--   An app declares how its stored perspective documents change when a model changes shape: a
--   property's former type or name, a removed property, a default for a new one ([StoredForm],
--   [StoredFormRemoved]), or raw SQL (IStoredFormMigration). Each becomes one statement the
--   stored-format rewrite phase runs, and this table records, per migration name, what became of it.
--
--   A generated migration is idempotent: it converts only values still in the old form. It is
--   settled by the first pass that finds nothing left to convert, so a pass that converted rows runs
--   once more on the next start and catches rows an older release wrote during a rolling deploy.
--   A custom migration is not assumed idempotent and is settled by its first successful run. A
--   settled migration is skipped with one lookup and no scan.
--
--   The phase inserts a row for every declared migration before it runs any of them, so a
--   migration that is waiting (its table does not exist yet) or blocked (values it cannot convert)
--   still shows as pending to anything reading the journal. Each migration writes its own row in
--   the same savepoint as its conversion, so the journal cannot say "applied" about rows that were
--   not converted.
--
--   In the bootstrap region because the rewrite phase runs ahead of the migration pass, on the
--   instance elected to migrate, and reads and writes this table.
-- Dependencies: none
-- Objects: wh_stored_form_migrations

-- @whizbang:bootstrap-begin
-- Bootstrap: the stored-form rewrite runs before the migration pass and journals into this table.

-- <docs>fundamentals/perspectives/stored-form-migrations#journal</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StoredFormMigrationTests.cs</tests>
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_stored_form_migrations (
  name TEXT PRIMARY KEY,
  table_name TEXT NOT NULL,
  kind TEXT NOT NULL,
  declared_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  first_applied_at TIMESTAMPTZ NULL,
  last_applied_at TIMESTAMPTZ NULL,
  rows_converted BIGINT NOT NULL DEFAULT 0,
  settled_at TIMESTAMPTZ NULL
);

COMMENT ON TABLE __SCHEMA__.wh_stored_form_migrations IS
  'App-declared stored-form migrations of perspective documents, one row per migration name. '
  'kind is generated (from [StoredForm]/[StoredFormRemoved]) or custom (IStoredFormMigration). '
  'Pending while first_applied_at is null; settled_at is set once a generated migration finds '
  'nothing left to convert, or once a custom migration has run, and a settled migration is skipped.';

-- @whizbang:bootstrap-end
