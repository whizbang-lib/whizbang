-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 351_TheLedgerRecordsWhoAppliedEachChange.sql
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
--              Existing rows: a row whose last write was an apply (status 1 Applied, 2 Updated, 4 migrating in the
--              background) was last written by the version that applied it, so its version_id is taken. A row last
--              skipped (status 3) or failed says nothing about who applied it, and is left NULL: it never stands an
--              instance down. The backfill runs once, when the column is added, before the trigger exists.
--
--              Rolling deploy: an older release writes the ledger exactly as before (it does not know the column) and
--              the trigger records what its writes applied; it does not read the column, and its own Assess compares
--              version_id as it always has.
--
--              Number 351: 331 to 345 are on develop, and 346 to 350 are taken by changes in flight.
--
-- Dependencies: 000 (wh_schema_migrations, wh_schema_versions)
-- Objects: wh_schema_migrations.applied_version_id, wh_schema_migration_applied_by, wh_schema_migrations_applied_by
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- ---------------------------------------------------------------------------------------------
-- The column, and the one-time backfill from the rows whose last write was an apply.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_schema_migrations')
                   AND attname = 'applied_version_id'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_schema_migrations
      ADD COLUMN IF NOT EXISTS applied_version_id INTEGER REFERENCES __SCHEMA__.wh_schema_versions(id);

    UPDATE __SCHEMA__.wh_schema_migrations
       SET applied_version_id = version_id
     WHERE status IN (1, 2, 4);

    COMMENT ON COLUMN __SCHEMA__.wh_schema_migrations.applied_version_id IS
      '351 (#2022): the version that last applied this change (inserted the row or changed its content hash). '
      'version_id is rewritten by every pass; this is not. NULL when unknown (a row last skipped before 351).';
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
  IF NEW.status = -1 THEN
    -- A failed step rolled back: it applied nothing, whatever the row now says.
    NEW.applied_version_id := CASE WHEN TG_OP = 'UPDATE' THEN OLD.applied_version_id END;
  ELSIF TG_OP = 'INSERT' OR NEW.content_hash IS DISTINCT FROM OLD.content_hash THEN
    NEW.applied_version_id := NEW.version_id;
  ELSE
    NEW.applied_version_id := OLD.applied_version_id;
  END IF;
  RETURN NEW;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_schema_migration_applied_by() IS
  '351 (#2022): trigger function keeping wh_schema_migrations.applied_version_id: the row''s version on an insert or '
  'a content-hash change, unchanged on any other write, never moved by a failure (status -1).';

DROP TRIGGER IF EXISTS wh_schema_migrations_applied_by ON __SCHEMA__.wh_schema_migrations;
CREATE TRIGGER wh_schema_migrations_applied_by
  BEFORE INSERT OR UPDATE ON __SCHEMA__.wh_schema_migrations
  FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.wh_schema_migration_applied_by();
