-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 544_DictionaryMembersStoredAsPairs.sql (#1911): the two functions are dropped. Rows the rewrite
-- converted stay as arrays of key/value objects, which the readers of every release since #1911 read; the journal rows
-- it wrote stay as the record of what it did.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_dictionary_pairs_at(JSONB, TEXT[], SMALLINT, JSONB);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_dictionary_pairs(JSONB, SMALLINT, JSONB);
