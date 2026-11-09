-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 377_TheLedgerRecordsHowToReverseEachChange.sql
-- Date: 2026-11-09
-- Description: The migration ledger records how each change it applied is reversed (#2122).
--
--              An older version started against a database a newer version migrated cannot know the newer version's
--              migrations, so it cannot know how to undo them. The newer version stores the reverse when it applies each
--              change, and the older version rolls back from the record (owner decision, 2026-11-09):
--
--              * reversal: 'reversible' (lossless), 'lossy' (reversing discards data the newer version wrote) or
--                'irreversible'. NULL is a change applied before this record, or by a writer that recorded nothing: it
--                counts as irreversible, so a rollback across it stands down exactly as before.
--              * down_sql: the script that reverses the change, with the schema already substituted; empty when nothing
--                needs to run (the associations, which the older version's own pass registers again); NULL when the
--                change cannot be reversed.
--
--              The writer records both in the transaction that applied the change, after its ledger row. The trigger
--              that keeps applied_version_id (359) is redefined here to forget both when a change is applied again by a
--              statement that does not set them itself, so a re-apply by a writer that records nothing never inherits
--              the reverse of what it replaced.
--
--              Rolling deploy: an older release writes the ledger as before and never these columns, so what it applies
--              records nothing and counts as irreversible; its own Assess does not read them.
--
--              Number 377: above every number on develop (through 372) and in open pull requests (through 375).
--
-- Reversal: irreversible (it ships with the release that first runs reversals, so no rollback ever crosses it)
-- Dependencies: 000 (wh_schema_migrations), 359 (applied_version_id and its trigger)
-- Objects: wh_schema_migrations.reversal, wh_schema_migrations.down_sql, wh_schema_migration_applied_by
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- ---------------------------------------------------------------------------------------------
-- The columns.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/infrastructure/migrations#how-a-migration-is-reversed</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RollbackGateTests.cs:TheLedger_RecordsHowEachChangeIsReversed_AndAssessReadsItAsync</tests>
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_schema_migrations')
                   AND attname = 'reversal'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_schema_migrations
      ADD COLUMN IF NOT EXISTS reversal TEXT NULL CHECK (reversal IN ('reversible', 'lossy', 'irreversible')),
      ADD COLUMN IF NOT EXISTS down_sql TEXT NULL;

    COMMENT ON COLUMN __SCHEMA__.wh_schema_migrations.reversal IS
      '377 (#2122): how the change is reversed: reversible, lossy (discards data the newer version wrote) or '
      'irreversible. NULL counts as irreversible.';
    COMMENT ON COLUMN __SCHEMA__.wh_schema_migrations.down_sql IS
      '377 (#2122): the script that reverses the change, schema substituted; empty when nothing needs to run; NULL when '
      'the change cannot be reversed.';
  END IF;
END $$;

-- ---------------------------------------------------------------------------------------------
-- wh_schema_migration_applied_by: 359's trigger function, and a re-apply forgets the reverse it replaced.
-- ---------------------------------------------------------------------------------------------
-- <docs>operations/infrastructure/migrations#how-a-migration-is-reversed</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RollbackGateTests.cs:AReapplyThatRecordsNoReversal_ForgetsTheOneBeforeAsync</tests>
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
    -- 377: applied again. The reverse of what it replaced does not reverse this; the writer records the new one.
    IF NEW.reversal IS NOT DISTINCT FROM OLD.reversal AND NEW.down_sql IS NOT DISTINCT FROM OLD.down_sql THEN
      NEW.reversal := NULL;
      NEW.down_sql := NULL;
    END IF;
  ELSE
    -- Nothing applied: the applier stays, and a row last applied before 359 carries its version_id over.
    NEW.applied_version_id := COALESCE(OLD.applied_version_id,
                                       CASE WHEN OLD.status IN (1, 2, 4) THEN OLD.version_id END);
  END IF;
  RETURN NEW;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_schema_migration_applied_by() IS
  '359 (#2022), 377 (#2122): trigger function keeping wh_schema_migrations.applied_version_id: the row''s version on an '
  'insert or a content-hash change, unchanged on any other write (a row last applied before 359 carrying its version_id), '
  'never moved by a failure (status -1); a content-hash change that does not set reversal and down_sql itself clears them.';
