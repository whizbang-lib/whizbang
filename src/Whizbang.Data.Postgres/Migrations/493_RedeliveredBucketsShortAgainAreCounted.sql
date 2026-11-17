-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 493_RedeliveredBucketsShortAgainAreCounted.sql
-- Date: 2026-11-17
-- Description: A deficit bucket a redelivery answer filled that a later comparison finds short again is counted once per
--              answer (#2365, redelivered_then_reaped).
--
--              The resync loop the integrity plan names (section 2.7) is a redelivered event that the consumer then
--              loses again, usually to its own cleanup, so the next comparison asks for it again. The ledger already
--              says when a redelivery answered a bucket (answered_at) and whether the answer could change it
--              (unhealable_at). A comparison that finds the bucket short again after such an answer is one turn of the
--              loop. counted_short_at records when the turn was counted, so the same answer is not counted again by the
--              comparisons that follow it; a later answer moves answered_at past it and can be counted again.
--
--              Cost: one nullable column on wh_integrity_ledger (bounded by MaxRepairDrainBacklog); one UPDATE by primary
--              key per comparison that finds deficits.
--
--              Number 493: the range assigned to this change; the integrator renumbers.
--
-- Reversal: lossy (the column goes; redelivered_then_reaped counts nothing)
-- Dependencies: 090 (wh_integrity_ledger), 249 (unhealable_at), 320 (answered_at)
-- Objects: wh_integrity_ledger.counted_short_at

ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS counted_short_at TIMESTAMPTZ;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.counted_short_at IS
'493 (#2365): when a comparison last counted this bucket as short again after the redelivery answer of answered_at (redelivered_then_reaped). NULL when never counted.';
