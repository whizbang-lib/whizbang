-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 334_PacedSubscriptionBackfill.sql
-- Date: 2026-11-05
-- Description: A grown subscription is backfilled in paced windows by the owner's commit sequence (#2092, #2098).
--
--              The startup reconciler used to ask each owner for the whole history of every newly consumed type at
--              once, and each owner answered with a bundle per event, capped at its answer bound with no follow-up.
--              The backfill walks each type in bounded windows instead:
--
--              1. wh_subscription_backfills (requester side): one row per (owner, event type) with the durable
--                 position (the last owner commit sequence received), the window awaiting its answer and until when,
--                 attempts, counts and the outcome. A restart resumes from the position; an ended row stops.
--              2. The owner-side index a window reads through (idx_event_store_type_commit_sequence, one type's
--                 stamped events in commit order) is NOT built here: a CREATE INDEX inside this migration's
--                 transaction would hold every write to wh_event_store for the whole build, minutes on a large store.
--                 It is built concurrently after the migrations by the framework index build (ConcurrentIndexBuild,
--                 run by a maintenance step), and windows read without it until it is valid.
--
--              Number 334: the next free number.
--
-- Dependencies: none (a standalone table)
-- Objects: wh_subscription_backfills

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_subscription_backfills (
  backfill_id UUID PRIMARY KEY,
  owner_service_id UUID NOT NULL,
  owner_service_name TEXT NOT NULL,
  event_type TEXT NOT NULL,
  cursor_commit_sequence BIGINT,
  window_number BIGINT NOT NULL DEFAULT 0,
  answer_due_by TIMESTAMPTZ,
  window_attempts INTEGER NOT NULL DEFAULT 0,
  windows_requested BIGINT NOT NULL DEFAULT 0,
  events_received BIGINT NOT NULL DEFAULT 0,
  first_commit_sequence BIGINT,
  head_commit_sequence BIGINT,
  last_requested_at TIMESTAMPTZ,
  started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  completed_at TIMESTAMPTZ,
  outcome SMALLINT NOT NULL DEFAULT 0,
  CONSTRAINT uq_subscription_backfills_owner_type UNIQUE (owner_service_id, event_type)
);

COMMENT ON TABLE __SCHEMA__.wh_subscription_backfills IS
'Paced backfill of a grown subscription (#2092): one row per (owner, event type). Each window asks the owner for the type''s events after cursor_commit_sequence, up to the window size; the next window is asked only once the answer lands. owner_service_id is the empty id when no owner is known and each window is broadcast.';
COMMENT ON COLUMN __SCHEMA__.wh_subscription_backfills.cursor_commit_sequence IS
'The last owner commit sequence a window answered with: the next window starts after it. NULL before any event.';
COMMENT ON COLUMN __SCHEMA__.wh_subscription_backfills.answer_due_by IS
'Set while window_number awaits its answer; past it the window may be asked again, up to the attempt limit.';
COMMENT ON COLUMN __SCHEMA__.wh_subscription_backfills.outcome IS
'0 running / 1 complete (the owner had nothing more) / 2 unanswered (a window went unanswered for every attempt, which an owner on a release before windowed backfill does).';
