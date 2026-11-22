-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 556_StrongIdsStoredAsUuidStrings.sql (#2518): the two functions are dropped. Rows the rewrite
-- converted stay as UUID strings, which the readers of every release since #2518 read; the journal rows it wrote stay
-- as the record of what it did.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_strong_id_scalar_at(JSONB, TEXT[]);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_strong_id_scalar(JSONB);
