-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 568_StoredFormConversionsRecordWhatTheyApplied.sql (#2553): the journals' content_hash columns are
-- dropped. The rollback gate then identifies every conversion by its name, as it does a row recorded before 568.

ALTER TABLE __SCHEMA__.wh_stored_form_migrations DROP COLUMN IF EXISTS content_hash;
ALTER TABLE __SCHEMA__.wh_perspective_forms DROP COLUMN IF EXISTS content_hash;
