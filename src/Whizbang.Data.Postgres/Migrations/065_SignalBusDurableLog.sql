-- Migration: 065_SignalBusDurableLog.sql
-- Date: 2026-07-12
-- Description: F1 signal-bus durable delivery. Adds wh_signals (durable log for
--              must-not-miss signals) and wh_signal_cursors (per-instance tail
--              cursors). Best-effort signals stay purely in-memory over NOTIFY;
--              Durable signals ALSO append to wh_signals in the same publish call
--              and get replayed on reconnect via a per-instance cursor tail.
-- Dependencies: None (independent subsystem).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_signals (
  -- Identity: monotonic id, cursors compare on this. BIGSERIAL is enough — even
  -- a very busy cluster would take centuries to overflow bigint. TIMESTAMPTZ is
  -- for retention/observability, NOT for ordering (clock-skew resistant).
  id BIGSERIAL PRIMARY KEY,

  -- Wire-name of the signal type (matches SignalTypeEntry.WireName in .NET).
  -- Doorbell-not-data — subscribers use the wire-name to reconstruct a default
  -- signal instance; they fetch authoritative state from the DB elsewhere.
  wire_name TEXT NOT NULL,

  -- Target scope:
  --  target_instance_id NULL       → broadcast (every instance's tail delivers)
  --  target_instance_id = <id>     → only that instance's tail delivers
  target_instance_id UUID,

  -- Emission timestamp for retention / diagnostics; NOT for ordering.
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- The tail query filters by id > cursor and (target IS NULL OR target = me) —
-- the partial index makes the target = me path index-only, and the "IS NULL"
-- broadcast path scans a small subset.
CREATE INDEX IF NOT EXISTS idx_wh_signals_target_instance
  ON __SCHEMA__.wh_signals (target_instance_id, id)
  WHERE target_instance_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_wh_signals_broadcast
  ON __SCHEMA__.wh_signals (id)
  WHERE target_instance_id IS NULL;

COMMENT ON TABLE __SCHEMA__.wh_signals IS
  'Durable signal log for must-not-miss control-plane signals (Delivery=Durable). Best-effort signals do NOT write here; they rely on NOTIFY + pull-source reconciliation.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_signal_cursors (
  -- Per-instance tail position. Primary key is instance_id — each pod tracks its
  -- own cursor independently, so a slow tail on one pod does not stall others.
  instance_id UUID PRIMARY KEY,

  -- Last signal id this instance has delivered to its bus. New signals to consume
  -- are `id > last_delivered_signal_id`.
  last_delivered_signal_id BIGINT NOT NULL,

  -- Housekeeping for observability / stale-cursor cleanup.
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_signal_cursors IS
  'Per-instance tail cursor into wh_signals for durable delivery. New pods initialize to MAX(wh_signals.id) so they do not replay pre-startup history.';
