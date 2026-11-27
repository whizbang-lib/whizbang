-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 562_StrongIdsStoredAsUuidStrings.sql (#2518): the six functions and the two journal columns are dropped; the gate's latch stays. Rows the rewrite
-- converted stay as UUID strings, which the readers of every release since #2518 read; the journal rows it wrote stay
-- as the record of what it did.

DROP FUNCTION IF EXISTS __SCHEMA__.jsonb_strong_id_containment_set(TEXT, ANYARRAY);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_strong_id_document_gate();
DROP FUNCTION IF EXISTS __SCHEMA__.wh_strong_id_document_unreadable_instances();
DROP FUNCTION IF EXISTS __SCHEMA__.wh_strong_id_document_status();
DROP FUNCTION IF EXISTS __SCHEMA__.wh_strong_id_scalar_at(JSONB, TEXT[]);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_strong_id_scalar(JSONB);
-- RECLAIM: not required: wh_stored_form_migrations holds one journal row per rewrite pass (a handful), so the dropped
-- BIGINT and TEXT bytes are a few per row and need no table rewrite.
ALTER TABLE __SCHEMA__.wh_stored_form_migrations
  DROP COLUMN IF EXISTS pass_rows_converted,
  DROP COLUMN IF EXISTS resume_key;
