-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 740_ARetryKeepsTheIdsItsFirstRunDerived.sql (#2633): the fetch that records the derivation scheme
-- goes, and so does the column. 576's fetch_inbox_batch_in_stream_order was never changed, so nothing comes back.

SELECT __SCHEMA__.drop_all_overloads('fetch_inbox_batch_with_derivation_scheme');

-- RECLAIM: not required: a SMALLINT that is NULL on every row the stamp never reached, and two bytes on the rows it
--          did; wh_inbox_state is a queue whose rows are deleted as they complete, so the bytes leave with the rows.
ALTER TABLE __SCHEMA__.wh_inbox_state DROP COLUMN IF EXISTS derivation_scheme;
