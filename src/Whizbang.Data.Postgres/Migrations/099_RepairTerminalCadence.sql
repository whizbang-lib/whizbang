-- 099_RepairTerminalCadence.sql
--
-- Past the attempt cap the repair ladder FLATTENS to its terminal cadence (base x 2^6)
-- instead of going permanently silent. A bucket whose whole budget burned against an
-- unreachable origin has a STATIC signature — the origin served nothing, so neither side's
-- digest ever moves and the signature-change reset (wh_integrity_try_begin_report) never
-- fires — which under the 090/097 semantics shadow-banned a real, repairable deficit
-- forever. With the terminal cadence a capped bucket still earns one ask per terminal
-- interval, so convergence stays eventually-true at bounded cost once the origin returns.
-- Both grant paths (single-key burst grant and the paced-drain claim) change together so
-- their semantics cannot drift. Mirrors IntegrityRepairLedger.TryBeginRepair.

-- Re-created from 090 verbatim; only the attempt-cap handling changes (cap pins the
-- backoff at 6 doublings instead of denying outright).
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_repair(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID,
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_scope     TEXT := COALESCE(p_tenant_scope, '');
  v_row       __SCHEMA__.wh_integrity_ledger%ROWTYPE;
  v_doublings INTEGER;
  v_wait      INTERVAL;
BEGIN
  SELECT * INTO v_row FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id
  FOR UPDATE;

  IF NOT FOUND THEN
    INSERT INTO __SCHEMA__.wh_integrity_ledger (
      origin_service_id, tenant_scope, event_type, stream_id,
      origin_lo, origin_hi, local_lo, local_hi, repair_attempts, last_repair_at, last_touched)
    VALUES (p_origin_service_id, v_scope, p_event_type, p_stream_id,
            -9223372036854775808, -9223372036854775808, -9223372036854775808, -9223372036854775808,
            1, p_now, p_now)
    ON CONFLICT (origin_service_id, tenant_scope, event_type, stream_id) DO NOTHING;
    RETURN FOUND;
  END IF;

  IF v_row.last_repair_at IS NOT NULL THEN
    -- Capped at 6 doublings so the wait cannot overflow, matching the in-memory ladder.
    -- At/past the attempt cap the ladder stops climbing and holds the terminal wait.
    IF v_row.repair_attempts >= p_max_attempts THEN
      v_doublings := 6;
    ELSE
      v_doublings := LEAST(GREATEST(v_row.repair_attempts - 1, 0), 6);
    END IF;
    v_wait := make_interval(secs => p_base_backoff_secs * POWER(2, v_doublings));
    IF (p_now - v_row.last_repair_at) < v_wait THEN
      RETURN FALSE;
    END IF;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_ledger
  SET repair_attempts = v_row.repair_attempts + 1,
      last_repair_at  = p_now,
      last_touched    = p_now
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id;

  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_repair IS
'True when a repair should be requested now, on an exponential ladder that flattens to its terminal cadence (base x 2^6) at p_max_attempts. Attempt counts survive restarts; a capped bucket still earns one ask per terminal interval so a static deficit is never shadow-banned forever.';

-- Re-created from 097 verbatim; the eligibility drops the hard attempt-cap conjunct and
-- pins capped rows at the terminal (6-doubling) wait instead.
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
      AND (l.last_repair_at IS NULL
           OR (p_now - l.last_repair_at) >= make_interval(
                secs => p_base_backoff_secs * POWER(2,
                  CASE WHEN l.repair_attempts >= p_max_attempts THEN 6
                       ELSE LEAST(GREATEST(l.repair_attempts - 1, 0), 6) END)))
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
'Atomically claims up to p_limit repair-eligible ledger rows for the paced drain — same eligibility ladder as wh_integrity_try_begin_repair (backoff capped at 6 doublings, terminal cadence at the attempt cap), least-recently-attempted first, SKIP LOCKED against concurrent drainers, attempt stamped on claim.';
