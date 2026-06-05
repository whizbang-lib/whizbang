-- Migration: 052_LiveInstancesView.sql
-- Date: 2026-06-04 (zero-idle-polling Slice 2b)
-- Description: Defines wh_live_instances, a view that joins wh_service_instances
--              against pg_stat_activity on the application_name format that
--              PgSharedNotifyConnection.ComputeApplicationName emits
--              ("whizbang-<instance_id>"). Gives orphan-detection callers a
--              cheap way to ask "is this pod still alive?" without depending
--              solely on last_heartbeat_at freshness — a pod with a live LISTEN
--              connection is provably alive at the TCP layer regardless of how
--              recently it called register_instance_heartbeat.
--
--              Slice 5 of zero-idle-polling will use this view to safely relax
--              HeartbeatWorker's cadence from 5 s to 30 s + add piggybacked
--              heartbeat updates on real work — the view + the timer together
--              give two independent liveness signals so orphan detection stays
--              correct even when the timer is at the relaxed cadence.
--
-- Dependencies: 003_CreateServiceInstancesTable (or earlier — supplies wh_service_instances)
--               pg_stat_activity is a Postgres system view, always present.

CREATE OR REPLACE VIEW __SCHEMA__.wh_live_instances AS
SELECT
  si.instance_id,
  si.service_name,
  si.host_name,
  si.process_id,
  si.last_heartbeat_at,
  si.registered_at,
  -- pg_stat_activity columns are nullable in the view because the LEFT JOIN
  -- may produce rows for service-instance records whose pod has no active
  -- LISTEN connection (e.g., the pod crashed, or it has never opened one yet).
  -- Callers treat NULL listen_connection_started as "no live LISTEN" — combine
  -- with last_heartbeat_at to decide liveness.
  sa.backend_start AS listen_connection_started,
  sa.state_change AS listen_last_state_change,
  EXTRACT(EPOCH FROM (NOW() - sa.state_change))::int AS listen_idle_seconds,
  -- Derived liveness flag, single source of truth for orphan checks. True when
  -- EITHER the heartbeat row is fresh (within stale_cutoff at query time)
  -- OR a LISTEN connection is currently registered in pg_stat_activity.
  -- Callers pass the stale_cutoff via the comparison column at SELECT time:
  --     SELECT instance_id FROM wh_live_instances
  --     WHERE last_heartbeat_at >= NOW() - INTERVAL '30 seconds'
  --        OR listen_connection_started IS NOT NULL;
  -- The view itself doesn't bake in a threshold so different callers can pick
  -- their own freshness windows without an explosion of view variants.
  (sa.backend_start IS NOT NULL) AS listen_alive
FROM __SCHEMA__.wh_service_instances si
LEFT JOIN pg_stat_activity sa
  ON sa.application_name = 'whizbang-' || si.instance_id::text;

COMMENT ON VIEW __SCHEMA__.wh_live_instances IS
'Joins wh_service_instances against pg_stat_activity on application_name=''whizbang-<instance_id>''. Gives orphan-detection callers a per-pod liveness signal that combines the (potentially stale) last_heartbeat_at column with the (TCP-fresh) presence of a LISTEN connection. Format must match PgSharedNotifyConnection.ComputeApplicationName — the format helper has dedicated regression tests at PgSharedNotifyConnectionApplicationNameTests.';
