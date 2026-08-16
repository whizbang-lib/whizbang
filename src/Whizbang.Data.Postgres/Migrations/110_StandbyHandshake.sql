-- Migration: 110_StandbyHandshake.sql
-- Date: 2026-08-16
-- Description: The standby request. A breaking migration is a planned outage — the honest
--              description of what a breaking schema change is — and the framework's job is to
--              convert an outage that today is silent and corrupting into one that is bounded,
--              announced and observable. The migrating instance records ONE fleet-wide request
--              (who asked, at what version, when); live older peers observe it, drain, hold their
--              data plane, and post STANDING BY through record_instance_state; the migrator waits
--              for every LIVE peer to acknowledge — the wait bounded by lease expiry, never by the
--              goodwill of a process that may already be dead.
--
--              One request at a time, by table shape (a single-row table, not a flag): two
--              concurrent migrators is exactly what duty election exists to prevent, and this
--              record must not quietly disagree with it. Only the requester clears its own
--              request; a dead requester's request is voided by LIVENESS (peers watch the
--              requester's heartbeat), not by deletion — every path out of standby is bounded:
--              success, clean failure, or a dead migrator.
--
--              evict_instance is the deliberate act (taken by the migrator during a breaking
--              handshake, or by an operator — never an automatic consequence of slowness) that
--              writes the tombstone the fence machinery (106/108) already honours. Because it
--              forcibly stops a process, it records who issued it, when, and why — an operator
--              finding a stopped instance needs that answer without archaeology.
-- Dependencies: 010 (wh_service_instances), 106 (wh_instance_evictions), 109 (record_instance_state)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_standby_requests (
  one_row SMALLINT PRIMARY KEY DEFAULT 1 CHECK (one_row = 1),
  requested_by UUID NOT NULL,
  requested_version TEXT NOT NULL,
  requested_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_standby_requests IS
'The single active standby request: a migrating instance asking live older peers to drain and
stand by before a breaking migration. Single-row by CHECK constraint — one handshake at a time.
Voided by the requester clearing it, or by the requester''s own liveness lapsing (peers decide).';

-- Who is evicting matters as much as who is evicted.
ALTER TABLE __SCHEMA__.wh_instance_evictions ADD COLUMN IF NOT EXISTS evicted_by UUID;

COMMENT ON COLUMN __SCHEMA__.wh_instance_evictions.evicted_by IS
'The instance (or operator-driven process) that issued the eviction. NULL for reaper-written
tombstones (cleanup_stale_instances), which are the automatic staleness path.';

-- ============================================================================
-- request_standby — record the single fleet-wide request. TRUE when this
-- instance now holds the active request (first claim or idempotent re-request);
-- FALSE when another instance's request is active, or when the requester is
-- itself evicted (the fence reaches here too — an evicted instance must not
-- orchestrate a fleet).
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.request_standby(
  p_instance_id UUID,
  p_version TEXT
) RETURNS BOOLEAN AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions WHERE instance_id = p_instance_id) THEN
    RETURN FALSE;
  END IF;

  INSERT INTO __SCHEMA__.wh_standby_requests (one_row, requested_by, requested_version)
  VALUES (1, p_instance_id, p_version)
  ON CONFLICT (one_row) DO NOTHING;

  RETURN EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_standby_requests WHERE requested_by = p_instance_id
  );
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- clear_standby — only the requester withdraws its own request. TRUE when a
-- row was cleared. Death needs no call: peers void a dead requester's request
-- by watching its heartbeat.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.clear_standby(
  p_instance_id UUID
) RETURNS BOOLEAN AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_standby_requests WHERE requested_by = p_instance_id;
  RETURN FOUND;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- evict_instance — the deliberate fence. Writes the tombstone record_heartbeat
-- and record_capability already refuse against, recording who issued it and why.
-- Idempotent: an existing tombstone is left as first written.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.evict_instance(
  p_instance_id UUID,
  p_evicted_by UUID,
  p_reason TEXT
) RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_instance_evictions (instance_id, evicted_by, reason)
  VALUES (p_instance_id, p_evicted_by, p_reason)
  ON CONFLICT (instance_id) DO NOTHING;
END;
$$ LANGUAGE plpgsql;
