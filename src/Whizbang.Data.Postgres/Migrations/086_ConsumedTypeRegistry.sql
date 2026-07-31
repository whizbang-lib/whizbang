-- Migration: 086_ConsumedTypeRegistry.sql
-- Date: 2026-07-31
-- Description: Stream-integrity Phase S — the consumed-type registry. Records WHEN each event type
--              joined this service's consumed set, so the startup reconciler can tell a
--              subscription EXPANSION (a type appearing on a later boot — history exists that this
--              service never received) from first-boot registration (nothing existed to miss).
--              backfill_status tracks the expansion's repair lifecycle: 0 Baseline (registered on
--              first boot, no backfill needed) / 1 Pending (expansion detected, backfill not yet
--              requested — the audit surface when backfill is disabled) / 2 Requested (broadcast
--              re-delivery request sent). Completion graduates via the audit phases.
-- Dependencies: none (standalone registry table).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_consumed_types (
  event_type TEXT PRIMARY KEY,
  first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  backfill_status SMALLINT NOT NULL DEFAULT 0,
  backfill_requested_at TIMESTAMPTZ
);

COMMENT ON TABLE __SCHEMA__.wh_consumed_types IS
'Stream-integrity Phase S: when each event type joined this service''s consumed set. A type appearing after first boot is a subscription expansion — history exists this service never received; the startup reconciler backfills it via broadcast re-delivery (state-only).';
COMMENT ON COLUMN __SCHEMA__.wh_consumed_types.backfill_status IS
'0 Baseline (first-boot registration, no backfill) / 1 Pending (expansion detected, not yet requested) / 2 Requested (broadcast re-delivery request sent).';
