-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 494_CutsAwaitingTheSeal.sql
-- Date: 2026-11-17
-- Description: The streams due for a cut that the seal gate holds are kept, so eligible_not_yet_deleted can count them
--              (#2365).
--
--              A prefix truncate or an archive removes only history this service has sealed, and with
--              Sealing:CutWaitsForConsumers only history every consumer has sealed (460, _wh_cut_awaits_seal). A cut
--              asked for above the seal answers awaiting_seal and removes nothing; the caller asks again later. Nothing
--              recorded that, so cleanup lag held back by the seal was invisible. The truncator records the stream here
--              when its cut answers awaiting_seal, with the task (truncate or archive), and removes it on any other
--              answer. The maintainer duty's holder counts the rows per task, each count stopping at a cap.
--
--              Cost: one row per stream the seal holds now (bounded by streams due for a cut, never by events); one
--              upsert or delete by primary key per cut attempt; a count that reads at most the cap's worth of
--              idx_integrity_cuts_awaiting_seal_task per task.
--
--              Number 494: the range assigned to this change; the integrator renumbers.
--
-- Reversal: lossy (the table goes; eligible_not_yet_deleted reports nothing)
-- Dependencies: 460 (_wh_cut_awaits_seal, the awaiting_seal answer)
-- Objects: wh_integrity_cuts_awaiting_seal, idx_integrity_cuts_awaiting_seal_task

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_cuts_awaiting_seal (
  stream_id UUID        PRIMARY KEY,
  task      TEXT        NOT NULL,
  since     TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_cuts_awaiting_seal IS
'494 (#2365): one row per stream whose last cut answered awaiting_seal: the seal gate (this service''s seal, or a consumer''s) holds it. task is truncate or archive; since is when it was first held. Removed when the cut answers otherwise. Counted per task for eligible_not_yet_deleted.';

CREATE INDEX IF NOT EXISTS idx_integrity_cuts_awaiting_seal_task
  ON __SCHEMA__.wh_integrity_cuts_awaiting_seal (task);
