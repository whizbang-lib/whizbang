-- Migration 080: Per-type TTL overrides for Destruction.AfterTtl events (E2-4b).
--
-- The runtime substrate for age-based ephemeral expiry. A [Ephemeral(Destruction = AfterTtl, TtlSeconds = N)]
-- type declares how long its events live; the startup reconciler syncs those declarations into this lookup,
-- and the reaper / logical-expiry read filter (E2-4c) resolve an event's TTL by joining event_type -> this
-- table. Presence of a row is what marks a type AfterTtl (age-gated) rather than WhenConsumed
-- (consumption-gated) — the two Destruction strategies are otherwise indistinguishable on the pointer.
--
-- Mirrors the wh_ephemeral_type_grace lookup + sync_ephemeral_type_grace pattern from migration 073.
-- Inert until E2-4c wires the reaper/read filter to it.

-- Per-type TTL overrides ([Ephemeral(TtlSeconds >= 0)]). Synced from the catalog at startup (full replace).
-- Absent row = the type is not AfterTtl (no age-based expiry). ttl_seconds is the age window in seconds.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_ephemeral_type_ttl (
  event_type  VARCHAR(500) PRIMARY KEY,
  ttl_seconds INTEGER NOT NULL
);

-- Full replace of the per-type TTL overrides: upsert the declared set (normalized), then prune any row no
-- longer declared. Called from the startup reconciler with the current [Ephemeral(TtlSeconds>=0)] set.
-- Empty input clears all overrides (no type has an age-based TTL).
CREATE OR REPLACE FUNCTION __SCHEMA__.sync_ephemeral_type_ttl(p_names TEXT[], p_ttls INTEGER[])
RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_ephemeral_type_ttl (event_type, ttl_seconds)
  SELECT __SCHEMA__.normalize_event_type(t), s
  FROM unnest(p_names, p_ttls) AS x(t, s)
  ON CONFLICT (event_type) DO UPDATE SET ttl_seconds = EXCLUDED.ttl_seconds;

  DELETE FROM __SCHEMA__.wh_ephemeral_type_ttl
  WHERE event_type <> ALL (SELECT __SCHEMA__.normalize_event_type(t) FROM unnest(p_names) AS t);
END;
$$ LANGUAGE plpgsql;
