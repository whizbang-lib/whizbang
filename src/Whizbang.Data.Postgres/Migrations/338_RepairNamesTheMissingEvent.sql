-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 338_RepairNamesTheMissingEvent.sql
-- Date: 2026-11-06
-- Description: A repair names the one event a bucket is missing, instead of re-sending the stream (#2103).
--
--              Stream digests carry id lanes (336): the XOR of each folded event id's two 64-bit halves. When the
--              origin's bucket holds exactly one event more than the consumer's, origin XOR local over the id lanes is
--              that event's id. The comparison records it on the deficit's ledger row, and the paced repair drain asks
--              the owner for that event only (RequestRedeliveryCommand.EventIds), keeping the stream and window on the
--              request for an owner that cannot name events. A comparison that cannot name one event (more than one
--              missing, or a side without id lanes) records NULL, which also clears an id an earlier comparison left.
--
--              Adding a nullable column without a default rewrites nothing. The claim reads it by primary key.
--
-- Dependencies: 090 (wh_integrity_ledger), 336 (the id lanes)
-- Objects: wh_integrity_ledger (missing_event_id)
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_integrity_ledger ADD COLUMN IF NOT EXISTS missing_event_id UUID;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.missing_event_id IS
  '338 (#2103): the one event this deficit bucket is missing, named by the XOR of the origin''s and the consumer''s id '
  'lanes when the origin held exactly one event more; NULL asks for the stream over the stamped window.';
