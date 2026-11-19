-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 493_RedeliveredBucketsShortAgainAreCounted.sql (#2365): redelivered_then_reaped counts nothing.

-- RECLAIM: not required: wh_integrity_ledger is bounded by MaxRepairDrainBacklog, so the dropped column's bytes are
--          negligible and no rewrite is worth running.
ALTER TABLE __SCHEMA__.wh_integrity_ledger DROP COLUMN IF EXISTS counted_short_at;
