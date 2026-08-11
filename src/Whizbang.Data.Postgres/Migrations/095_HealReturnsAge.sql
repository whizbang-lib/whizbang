-- 095: the batch heal returns each healed bucket's age.
--
-- The heal was already DELETEing the ledger rows that carry first_seen_at (the clock the
-- oldest-unhealed gauge reads). Returning EXTRACT(EPOCH FROM (NOW() - first_seen_at)) for each
-- destroyed row makes per-bucket time-to-reconcile observable at ZERO extra work — no new
-- queries, no new state; the delete reads back what it was destroying.
--
-- The 094 loop-over-singles becomes one set-based DELETE. Semantics are identical: the single
-- function (090 wh_integrity_mark_healed) is delete-by-key only, and keys with no row simply
-- return no age. A return-type change requires DROP first — CREATE OR REPLACE cannot alter
-- RETURNS (the 094 shape was VOID).

DROP FUNCTION IF EXISTS __SCHEMA__.wh_integrity_mark_healed_batch(UUID, TEXT[], TEXT[], UUID[]);

CREATE FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[]
) RETURNS SETOF DOUBLE PRECISION
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  WITH keys AS (
    SELECT unnest(p_tenant_scopes) AS tenant_scope,
           unnest(p_event_types)   AS event_type,
           unnest(p_stream_ids)    AS stream_id
  ),
  healed AS (
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    USING keys k
    WHERE l.origin_service_id = p_origin_service_id
      AND l.tenant_scope      = k.tenant_scope
      AND l.event_type        = k.event_type
      AND l.stream_id         = k.stream_id
    RETURNING l.first_seen_at
  )
  SELECT EXTRACT(EPOCH FROM (NOW() - h.first_seen_at))::DOUBLE PRECISION
  FROM healed h;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch IS
'One round trip to forget a chunk''s healed buckets, returning each healed bucket''s age in seconds (first sighting -> heal) — the per-stream time-to-reconcile, read from the rows the delete destroys.';
