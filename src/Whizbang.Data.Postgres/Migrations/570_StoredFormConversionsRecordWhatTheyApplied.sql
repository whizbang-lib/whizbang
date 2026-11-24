-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 570_StoredFormConversionsRecordWhatTheyApplied.sql
-- Date: 2026-11-23
-- Description: A stored-form conversion records what it applied, as a content hash, beside who applied it (#2553).
--
--              The rollback gate judges a recorded change by what was applied, not who applied it: a change this build
--              defines identically is current, whatever version applied it. The migration ledger records a content
--              hash for every row; the conversion journals (wh_stored_form_migrations for the stored-form migrations,
--              the dictionary-pairs rewrite and the declared-key pass, wh_perspective_forms for the canonical temporal
--              rewrite) recorded only who applied each conversion (385), so a database restored from another
--              environment stood down over conversions this build makes identically. Each journal now records
--              content_hash:
--
--              * set beside applied_library_version and applied_application_version whenever those are (a pass that
--                converted rows, or ran a custom migration): the SHA-256 of the statement the rewrite phase ran, carried
--                as the transaction setting whizbang.applied_content_hash, or of what a declared-key check declares;
--              * kept by a pass that converted nothing;
--              * NULL on every row written before this migration. Such a row is judged by its name, which is the
--                conversion's form: current when this build declares a conversion of that name. The name rule exists
--                only for those rows.
--
--              In the bootstrap region: the conversions run before the migration pass, on the elected migrator, and
--              write this column.
--
--              Rolling deploy: an older release writes the journals as before and never this column, so a conversion
--              it applies records no hash and is judged by its name.
--
--              Number 570: the range assigned to this change.
--
-- Reversal: reversible (the column is dropped; the gate then judges every conversion by its name, as for rows recorded
--           before this migration)
-- Dependencies: 153 (wh_perspective_forms), 176 (wh_stored_form_migrations), 385 (the applier columns)
-- Objects: wh_stored_form_migrations.content_hash, wh_perspective_forms.content_hash
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the stored-form rewrite runs before the migration pass and records what each conversion applied.

-- <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredFormApplierTests.cs:EveryConversion_RecordsWhatItApplied_AsTheHashOfItsStatementAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredFormApplierTests.cs:OnceAConversionRecordsItsHash_TheHashDecides_NotTheNameAsync</tests>
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_stored_form_migrations')
                   AND attname = 'content_hash'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_stored_form_migrations ADD COLUMN IF NOT EXISTS content_hash TEXT;

    COMMENT ON COLUMN __SCHEMA__.wh_stored_form_migrations.content_hash IS
      '570 (#2553): the SHA-256 of what the conversion that last converted rows applied; kept through a pass that '
      'converted nothing. NULL on rows recorded before 570, which the rollback gate identifies by name.';
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_perspective_forms')
                   AND attname = 'content_hash'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_perspective_forms ADD COLUMN IF NOT EXISTS content_hash TEXT;

    COMMENT ON COLUMN __SCHEMA__.wh_perspective_forms.content_hash IS
      '570 (#2553): the SHA-256 of the temporal rewrite that last converted a row of the table; kept through a pass that '
      'converted nothing. NULL on rows recorded before 570, which the rollback gate identifies by name.';
  END IF;
END $$;

-- @whizbang:bootstrap-end
