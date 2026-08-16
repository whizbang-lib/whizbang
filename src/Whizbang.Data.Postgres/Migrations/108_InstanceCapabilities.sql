-- Migration: 108_InstanceCapabilities.sql
-- Date: 2026-08-16
-- Description: Recorded capability holdings. A capability (an exclusive one is a duty — migrator,
--              maintainer) is WON by attempting a database primitive, never assigned; this table
--              records what each live instance currently holds and since when, so "which instance
--              is the migrator right now, and for how long" is a query rather than a broadcast to
--              instances that may not be answering.
--
--              The rule the design turns on: THE LOCK DECIDES, THE ROW REPORTS. Holdings are
--              recorded but never consulted to decide — if the record and the lock disagree, the
--              lock is right and the record is stale. That is why this table needs no staleness
--              machinery of its own: it rides wh_service_instances' lease/heartbeat/reaper rails,
--              and the foreign key cascades when a stale instance is genuinely DELETEd.
--
--              Holdings are a separate table rather than a column on the instance row because an
--              instance holds several capabilities at once, the relationship carries its own data
--              (acquired_at), and the heartbeat's 10-second freshness guard would either swallow
--              capability writes inside the takeover window or have to be bypassed, re-creating
--              the write amplification it exists to prevent.
--
--              record_capability is also where the eviction fence reaches exclusive work: an
--              instance tombstoned in wh_instance_evictions is refused at acquisition (returns
--              FALSE — the caller must release the underlying primitive and stand down), closing
--              the window that heartbeat-delivered refusal leaves open.
-- Dependencies: 010 (wh_service_instances)
--               106 (wh_instance_evictions)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_instance_capabilities (
  instance_id UUID NOT NULL REFERENCES __SCHEMA__.wh_service_instances(instance_id) ON DELETE CASCADE,
  capability TEXT NOT NULL,
  acquired_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (instance_id, capability)
);

COMMENT ON TABLE __SCHEMA__.wh_instance_capabilities IS
'What each live instance currently holds, and since when. Derived state: the lock decides, the row
reports. Reaped with the instance row via ON DELETE CASCADE; written by record_capability after the
underlying primitive is won.';

-- Which instance holds capability X right now — the incident question — without scanning by pk.
CREATE INDEX IF NOT EXISTS idx_instance_capabilities_capability
  ON __SCHEMA__.wh_instance_capabilities (capability);

-- ============================================================================
-- record_capability — record a holding AFTER the underlying primitive was won.
-- Returns FALSE when the instance must not hold capabilities: tombstoned in
-- wh_instance_evictions (the fence reaching exclusive work), or not registered
-- in wh_service_instances (nothing to attach the holding to). The caller must
-- then release the primitive it won and stand down.
-- acquired_at is NOT refreshed on re-record: tenure is measured from the
-- original acquisition, and "how long has this instance been the migrator" is
-- the question this row exists to answer.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.record_capability(
  p_instance_id UUID,
  p_capability TEXT
) RETURNS BOOLEAN AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_instance_evictions WHERE instance_id = p_instance_id) THEN
    RETURN FALSE;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_service_instances WHERE instance_id = p_instance_id) THEN
    RETURN FALSE;
  END IF;

  INSERT INTO __SCHEMA__.wh_instance_capabilities (instance_id, capability)
  VALUES (p_instance_id, p_capability)
  ON CONFLICT (instance_id, capability) DO NOTHING;

  RETURN TRUE;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- release_capability — remove a holding on clean release. Death needs no call:
-- the instance row's reap cascades.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.release_capability(
  p_instance_id UUID,
  p_capability TEXT
) RETURNS VOID AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_instance_capabilities
  WHERE instance_id = p_instance_id AND capability = p_capability;
END;
$$ LANGUAGE plpgsql;
