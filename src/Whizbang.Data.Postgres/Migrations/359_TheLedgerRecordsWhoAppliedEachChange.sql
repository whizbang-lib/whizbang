-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 359_TheLedgerRecordsWhoAppliedEachChange.sql
-- Date: 2026-11-07
-- Description: The migration ledger records which version applied each change (#2022).
--
--              wh_schema_migrations.version_id is rewritten by every schema pass, a pass that skips an unchanged file
--              included, so it says which version last looked at a file, not which version changed the schema. An
--              application rolled back to the version before has nothing to fear from a pass that changed nothing, and
--              everything to fear from one that applied a migration it did not create; the ledger could not tell the
--              two apart. applied_version_id can:
--
--              * set to the row's version_id when a row is inserted, or when its content hash changes (the file was
--                applied, or a perspective table or application object was rebuilt): a version applied it;
--              * kept as it was on every other write: a skip, a status update, a re-stamp;
--              * never moved by a failure (status -1): a failed step rolled back and applied nothing.
--
--              A trigger keeps it, so both drivers' ledger writers record it without a change to either, and a writer
--              added later cannot forget it. Startup's Assess reads it to decide whether an instance may serve a schema
--              a newer version has migrated (the rollback gate, #2022): only when nothing was applied after its own
--              version, since a migration cannot be reversed yet (#2122).
--
--              Existing rows are not rewritten (a ledger write is what a pass and its observers count as work). A row
--              whose last write before 359 was an apply (status 1 Applied, 2 Updated, 4 migrating in the background) was
--              written by the version that applied it, so while applied_version_id is NULL its version_id is the
--              applier: the reader takes it, and the trigger carries it over on the row's next write. A row last skipped
--              (status 3) or failed says nothing about who applied it: it never stands an instance down.
--
--              Rolling deploy: an older release writes the ledger exactly as before (it does not know the column) and
--              the trigger records what its writes applied; it does not read the column, and its own Assess compares
--              version_id as it always has.
--
--              Number 359: above every number on develop (through 358) and in open pull requests.
--
-- Dependencies: 000 (wh_schema_migrations, wh_schema_versions)
-- Objects: wh_schema_migrations.applied_version_id, wh_schema_migration_applied_by, wh_schema_migrations_applied_by
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- ---------------------------------------------------------------------------------------------
-- The column.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_schema_migrations')
                   AND attname = 'applied_version_id'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_schema_migrations
      ADD COLUMN IF NOT EXISTS applied_version_id INTEGER REFERENCES __SCHEMA__.wh_schema_versions(id);

    COMMENT ON COLUMN __SCHEMA__.wh_schema_migrations.applied_version_id IS
      '359 (#2022): the version that last applied this change (inserted the row or changed its content hash). '
      'version_id is rewritten by every pass; this is not. NULL on a row not written since 359: then a row last applied '
      '(status 1, 2, 4) was applied by its version_id, and any other row by nobody known.';
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------
-- wh_schema_migration_applied_by: keeps applied_version_id on every ledger write.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RollbackGateTests.cs:TheLedger_RecordsWhoAppliedAChange_AndKeepsItThroughSkipsAndFailuresAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_schema_migration_applied_by()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    -- A failed step rolled back: it applied nothing, whatever the row now says.
    NEW.applied_version_id := CASE WHEN NEW.status <> -1 THEN NEW.version_id END;
  ELSIF NEW.status <> -1 AND NEW.content_hash IS DISTINCT FROM OLD.content_hash THEN
    NEW.applied_version_id := NEW.version_id;
  ELSE
    -- Nothing applied: the applier stays, and a row last applied before 359 carries its version_id over.
    NEW.applied_version_id := COALESCE(OLD.applied_version_id,
                                       CASE WHEN OLD.status IN (1, 2, 4) THEN OLD.version_id END);
  END IF;
  RETURN NEW;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_schema_migration_applied_by() IS
  '359 (#2022): trigger function keeping wh_schema_migrations.applied_version_id: the row''s version on an insert or '
  'a content-hash change, unchanged on any other write (a row last applied before 359 carrying its version_id), never '
  'moved by a failure (status -1).';

DROP TRIGGER IF EXISTS wh_schema_migrations_applied_by ON __SCHEMA__.wh_schema_migrations;
CREATE TRIGGER wh_schema_migrations_applied_by
  BEFORE INSERT OR UPDATE ON __SCHEMA__.wh_schema_migrations
  FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.wh_schema_migration_applied_by();
