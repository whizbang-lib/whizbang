-- 097_PacedRepairDrain.sql
--
-- The paced-repair-drain substrate (discovery/dispatch separation): discovery stamps the
-- compared window onto ledger rows, and a continuous drain CLAIMS eligible rows atomically
-- instead of the compare bursting its whole budget at the audit tick. The claim mirrors the
-- proven single-key repair grant's eligibility ladder EXACTLY (exponential backoff capped at
-- 6 doublings, hard attempt cap), so burst-path and drain-path semantics cannot drift.

-- The compared window, stamped at discovery. NULL = the row predates window stamping (or the
-- stamp degraded); the drain then derives a coarser per-origin range instead.
ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS window_from  BIGINT,
  ADD COLUMN IF NOT EXISTS window_until BIGINT;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.window_from IS
'Exclusive floor of the origin-commit-sequence window the divergence was observed in — dispatch context for the paced repair drain.';
COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.window_until IS
'Inclusive ceiling of the observed window. NULL (with window_from NULL) = pre-stamp row; the drain derives a coarser range.';

-- Discovery-time window stamp for a compared chunk's keys — additive beside the report batch,
-- so the proven report path keeps its signature. Only rows that exist are touched (the report
-- upserts first in the same handler).
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_stamp_repair_windows(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_window_from       BIGINT,
  p_window_until      BIGINT
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_n INTEGER := COALESCE(array_length(p_stream_ids, 1), 0);
BEGIN
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET window_from = p_window_from, window_until = p_window_until
  FROM (
    SELECT COALESCE(u.scope, '') AS tenant_scope, u.event_type, u.stream_id
    FROM UNNEST(p_tenant_scopes, p_event_types, p_stream_ids) AS u(scope, event_type, stream_id)
  ) k
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = k.tenant_scope
    AND l.event_type = k.event_type
    AND l.stream_id = k.stream_id;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_stamp_repair_windows IS
'Persists the compared [from, until] origin-sequence window onto the keyed ledger rows — the dispatch context the paced repair drain sends with, without the in-flight manifest.';

-- The drain's atomic claim: eligible rows (past the exponential backoff, under the attempt
-- cap, per-stream lanes only — the synthetic bulk lane at stream zero dispatches through bulk
-- escalation), least-recently-attempted first, restricted to origins whose request topic the
-- caller has learned. FOR UPDATE SKIP LOCKED keeps concurrent drainers disjoint, and the claim
-- stamps the attempt exactly like a burst-path grant.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_claim_repair_drain(
  p_origin_ids        UUID[],
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER,
  p_limit             INTEGER
) RETURNS TABLE (
  origin_service_id UUID,
  tenant_scope      TEXT,
  event_type        TEXT,
  stream_id         UUID,
  window_from       BIGINT,
  window_until      BIGINT
)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  WITH eligible AS (
    SELECT l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id
    FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.origin_service_id = ANY(p_origin_ids)
      AND l.stream_id <> '00000000-0000-0000-0000-000000000000'::uuid
      AND l.repair_attempts < p_max_attempts
      AND (l.last_repair_at IS NULL
           OR (p_now - l.last_repair_at) >= make_interval(
                secs => p_base_backoff_secs * POWER(2, LEAST(GREATEST(l.repair_attempts - 1, 0), 6))))
    ORDER BY l.last_repair_at ASC NULLS FIRST, l.first_seen_at ASC
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET repair_attempts = l.repair_attempts + 1,
      last_repair_at  = p_now,
      last_touched    = p_now
  FROM eligible e
  WHERE l.origin_service_id = e.origin_service_id
    AND l.tenant_scope = e.tenant_scope
    AND l.event_type = e.event_type
    AND l.stream_id = e.stream_id
  RETURNING l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id, l.window_from, l.window_until;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_claim_repair_drain IS
'Atomically claims up to p_limit repair-eligible ledger rows for the paced drain — same eligibility ladder as wh_integrity_try_begin_repair (backoff capped at 6 doublings, hard attempt cap), least-recently-attempted first, SKIP LOCKED against concurrent drainers, attempt stamped on claim.';
