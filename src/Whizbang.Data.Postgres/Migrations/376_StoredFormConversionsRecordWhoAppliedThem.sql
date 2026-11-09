-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 376_StoredFormConversionsRecordWhoAppliedThem.sql
-- Date: 2026-11-09
-- Description: A stored-form conversion records which library and application version applied it (#2131).
--
--              The rollback gate (#2022) reads who applied each change from wh_schema_migrations.applied_version_id
--              (359). Stored-form conversions are recorded elsewhere, and recorded no version: the stored-form
--              migrations and the declared-key pass in wh_stored_form_migrations, the canonical temporal rewrite in
--              wh_perspective_forms. A rollback after a newer version converted stored documents went unseen. Each
--              journal now records applied_library_version and applied_application_version:
--
--              * set when a pass converted rows (a generated migration or the temporal rewrite that touched a row, the
--                declared-key pass that moved a key) or ran a custom migration, which is not assumed reversible;
--              * kept as they were by a pass that converted nothing (a check, a settle), so a newer version that only
--                found the documents already converted applied nothing;
--              * NULL on rows written before this migration, and on a conversion run with no version to record: an
--                applier nobody knows never stands an instance down, as on the migration ledger.
--
--              Text, not a wh_schema_versions id: the conversions run before the schema pass, which is what records
--              this binary's version row, so the versions are carried as the instance records them (the application
--              version as recorded, 'unknown' when nobody knows it).
--
--              In the bootstrap region: the conversions run before the migration pass, on the elected migrator, and
--              write these columns.
--
--              Rolling deploy: an older release writes the journals as before and never these columns, so the
--              conversions it applies record nobody; its own Assess does not read them.
--
--              Number 376: above every number on develop (through 372) and in open pull requests (through 375).
--
-- Reversal: irreversible (it ships with the release that first runs reversals, so no rollback ever crosses it)
-- Dependencies: 153 (wh_perspective_forms), 176 (wh_stored_form_migrations)
-- Objects: wh_stored_form_migrations.applied_library_version, wh_stored_form_migrations.applied_application_version,
--          wh_perspective_forms.applied_library_version, wh_perspective_forms.applied_application_version
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the stored-form rewrite runs before the migration pass and records who applied each conversion.

-- <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredFormApplierTests.cs</tests>
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_stored_form_migrations')
                   AND attname = 'applied_library_version'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_stored_form_migrations
      ADD COLUMN IF NOT EXISTS applied_library_version TEXT,
      ADD COLUMN IF NOT EXISTS applied_application_version TEXT;

    COMMENT ON COLUMN __SCHEMA__.wh_stored_form_migrations.applied_library_version IS
      '376 (#2131): the library version of the pass that last converted rows (or ran a custom migration); kept through a '
      'pass that converted nothing. NULL when nobody known applied it.';
    COMMENT ON COLUMN __SCHEMA__.wh_stored_form_migrations.applied_application_version IS
      '376 (#2131): the application version, as recorded, of the pass that last converted rows; NULL when nobody known.';
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_perspective_forms')
                   AND attname = 'applied_library_version'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_perspective_forms
      ADD COLUMN IF NOT EXISTS applied_library_version TEXT,
      ADD COLUMN IF NOT EXISTS applied_application_version TEXT;

    COMMENT ON COLUMN __SCHEMA__.wh_perspective_forms.applied_library_version IS
      '376 (#2131): the library version of the temporal rewrite that last converted a row of the table; kept through a '
      'pass that converted nothing. NULL when nobody known applied it.';
    COMMENT ON COLUMN __SCHEMA__.wh_perspective_forms.applied_application_version IS
      '376 (#2131): the application version, as recorded, of the rewrite that last converted a row; NULL when nobody known.';
  END IF;
END $$;

-- @whizbang:bootstrap-end
