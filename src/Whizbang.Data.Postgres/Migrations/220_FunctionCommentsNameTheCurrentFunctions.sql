-- Migration: 220_FunctionCommentsNameTheCurrentFunctions.sql
-- Date: 2026-10-20
-- Description: The database comments on two functions named their caller as an orchestrator that no longer
--              exists, so a reader of \df+ went looking for a function that migration 029 removed (#1463).
--              Each comment is re-issued with the caller that actually calls the function today:
--
--                register_instance_heartbeat  comment last written by 010; called at startup by the schema
--                                             bootstrap phase, which puts the instance in the registry before
--                                             the duty election.
--                cleanup_stale_instances      comment last written by 106 (162 redefined the body at the same
--                                             signature, which keeps the comment); called by record_heartbeat
--                                             when a peer has gone silent past the stale cutoff.
--
--              Comments only: no function body changes, so nothing here is copied forward under rule 5. The
--              objects are declared below so that a re-run of an earlier file that rewrites either comment
--              (010, 106) re-runs this one after it (rule 11).
-- Dependencies: 010 (register_instance_heartbeat), 106 and 162 (cleanup_stale_instances)
-- Objects: register_instance_heartbeat, cleanup_stale_instances
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

COMMENT ON FUNCTION __SCHEMA__.register_instance_heartbeat(UUID, VARCHAR, VARCHAR, INTEGER, JSONB, TIMESTAMPTZ, TIMESTAMPTZ) IS
'Updates service instance heartbeat timestamp. Inserts new instance if not exists, updates last_heartbeat_at if exists and is more than 10 seconds old. Called at startup by the schema bootstrap phase, which registers the instance before the duty election that chooses which instance migrates; the heartbeat worker keeps the row current afterwards through record_heartbeat.';

COMMENT ON FUNCTION __SCHEMA__.cleanup_stale_instances(TIMESTAMPTZ, TIMESTAMPTZ) IS
'Removes stale service instances (with last_heartbeat_at < p_stale_cutoff) and releases their work items. Returns deleted instance IDs for logging. Called by record_heartbeat when a peer has gone silent past the stale cutoff. v0.687: optional p_definitive_dead_cutoff bypasses the v0.681 alive-lock guard when the heartbeat is so old we trust it over pg_locks (covers OOMKilled pods on half-open TCP sessions where the advisory lock can linger for hours). Migration 106: every reaped instance is tombstoned in wh_instance_evictions so record_heartbeat can refuse it if it later resumes.';
