-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 491_DeficitRowsKeepTheirTrace.sql
-- Date: 2026-11-17
-- Description: A deficit row keeps the trace of the comparison that recorded it (#2365).
--
--              With the paced repair drain on (the default), a comparison records a deficit in the integrity ledger and
--              the drain sends the redelivery request later, in another process scope. The request carries its own span
--              to the origin, so the origin's answer and the consumer's repair join the request's trace, but nothing
--              tied that trace to the audit that found the gap. Each ledger row now keeps the W3C traceparent of the
--              comparison that recorded its deficit and the id of the manifest ask whose answer it compared; the drain
--              starts its request under that trace. A later comparison of the same bucket replaces both.
--
--              Cost: two nullable columns on wh_integrity_ledger (one row per divergent bucket, bounded by
--              MaxRepairDrainBacklog); one UPDATE per comparison that records deficits, by primary key, and only while
--              the comparison has a span. The drain's claim already reads each claimed row by its primary key.
--
--              Number 491: the range assigned to this change; the integrator renumbers.
--
-- Reversal: lossy (the columns go; the drain's requests start traces of their own)
-- Dependencies: 090 (wh_integrity_ledger)
-- Objects: wh_integrity_ledger.trace_parent, wh_integrity_ledger.trace_request_id

ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS trace_parent TEXT,
  ADD COLUMN IF NOT EXISTS trace_request_id UUID;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.trace_parent IS
'491 (#2365): the W3C traceparent of the comparison that last recorded this bucket''s deficit; the paced repair drain starts its request under it. NULL when the comparison ran with no span.';

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.trace_request_id IS
'491 (#2365): the id of the manifest ask whose answer that comparison compared. NULL when the ask named none.';
