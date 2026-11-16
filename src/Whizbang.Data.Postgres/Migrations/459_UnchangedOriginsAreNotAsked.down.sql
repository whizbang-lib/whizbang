-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 459_UnchangedOriginsAreNotAsked.sql (#2349): the memo, the last-ask stamp and the functions go, so every
-- audit asks every origin again.

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_decide_origin_ask');
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_record_compare_outcome');
SELECT __SCHEMA__.drop_all_overloads('_wh_integrity_received_max');

ALTER TABLE __SCHEMA__.wh_integrity_known_origins DROP COLUMN IF EXISTS last_asked_at;

ALTER TABLE __SCHEMA__.wh_integrity_seals
  DROP COLUMN IF EXISTS match_sealed_through,
  DROP COLUMN IF EXISTS match_origin_generation,
  DROP COLUMN IF EXISTS match_checkpoint_sequence,
  DROP COLUMN IF EXISTS match_received_max;
