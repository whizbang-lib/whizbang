-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 339_IntegrityGapTrackerSurvivesARestart.sql
-- Date: 2026-11-06
-- Description: The integrity gap tracker's known origins and pending gaps survive a restart (#2104).
--
--              The tracker held, in memory only, every origin it had seen a checkpoint from (name, request address, last
--              checkpoint time) and every deficit awaiting confirmation by the origin's next checkpoint. After a restart
--              the audit skipped manifest requests until each origin checkpointed again, the liveness clock started
--              over, and a pending gap was re-detected a cycle late. These two tables keep both:
--              1. wh_integrity_known_origins: one row per origin, written when a checkpoint is recorded (a heap-only
--                 update: no indexed column changes).
--              2. wh_integrity_pending_gaps: an origin's pending gaps, replaced when a checkpoint changes them (a gap
--                 first seen, confirmed or healed); a checkpoint that changes nothing writes nothing.
--              A new process reads both once, before the tracker is first read.
--
--              The origins could have been read from wh_integrity_domain_claims (325), but an origin that announces no
--              owned domains has no row there, and the liveness clock needs a time per checkpoint, so they get their own
--              table.
--
-- Dependencies: none
-- Objects: wh_integrity_known_origins, wh_integrity_pending_gaps
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_known_origins (
  origin_service_id   UUID        NOT NULL PRIMARY KEY,
  origin_service_name TEXT        NOT NULL,
  request_topic       TEXT,                     -- where it consumes directed integrity requests, when announced
  last_checkpoint_at  TIMESTAMPTZ NOT NULL      -- the liveness clock
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_known_origins IS
  '339 (#2104): every origin this service has recorded a checkpoint from, with its request address and the time of its '
  'last checkpoint, so a restarted process audits it and keeps its liveness clock without waiting for a new checkpoint.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_pending_gaps (
  origin_service_id    UUID   NOT NULL,
  origin_service_name  TEXT   NOT NULL,
  tenant_scope         TEXT   NOT NULL DEFAULT '',   -- '' rather than NULL so it can key
  event_type           TEXT   NOT NULL,
  from_commit_sequence BIGINT NOT NULL,              -- exclusive window floor
  to_commit_sequence   BIGINT NOT NULL,              -- inclusive window watermark
  expected_count       INTEGER NOT NULL,             -- the origin's emission count for the bucket
  PRIMARY KEY (origin_service_id, tenant_scope, event_type, from_commit_sequence, to_commit_sequence)
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_pending_gaps IS
  '339 (#2104): the checkpoint deficits awaiting confirmation by their origin''s next checkpoint, so a gap seen before a '
  'restart is confirmed by the first checkpoint after it.';
