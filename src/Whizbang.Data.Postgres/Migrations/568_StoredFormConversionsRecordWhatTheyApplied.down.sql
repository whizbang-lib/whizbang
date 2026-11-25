-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 568_StoredFormConversionsRecordWhatTheyApplied.sql (#2553): the journals' content_hash columns are
-- dropped. The rollback gate then identifies every conversion by its name, as it does a row recorded before 568.

-- RECLAIM: not required: wh_stored_form_migrations and wh_perspective_forms are journals of one row per conversion
--          or per perspective form, so the dropped bytes are a few dozen bytes per row on a handful of rows.
ALTER TABLE __SCHEMA__.wh_stored_form_migrations DROP COLUMN IF EXISTS content_hash;
-- RECLAIM: not required: as above, a journal of a handful of rows.
ALTER TABLE __SCHEMA__.wh_perspective_forms DROP COLUMN IF EXISTS content_hash;
