-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 491_DeficitRowsKeepTheirTrace.sql (#2365): the drain's requests start traces of their own again.

-- RECLAIM: not required: wh_integrity_ledger is bounded by MaxRepairDrainBacklog, so the dropped columns' bytes are
--          negligible and no rewrite is worth running.
ALTER TABLE __SCHEMA__.wh_integrity_ledger
  DROP COLUMN IF EXISTS trace_parent,
  DROP COLUMN IF EXISTS trace_request_id;
