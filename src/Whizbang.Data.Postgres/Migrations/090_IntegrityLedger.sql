-- 090_IntegrityLedger.sql
--
-- Durable convergence state for stream integrity.
--
-- The ledger remembers what divergence has already been REPORTED and how often a bucket's REPAIR
-- has been requested, so a persistent divergence produces a bounded trickle instead of a storm.
-- It was in-memory by design, on the reasoning that "a restart re-reports once, then re-bounds —
-- over-reporting after a restart is the safe failure mode".
--
-- That reasoning holds only when restarts are rare and the divergent set is small. Neither is true
-- when it matters: the storm saturates the database, saturation restarts the pods, and the restart
-- clears the very memory that would have suppressed the storm. Each cycle re-reports EVERY
-- divergent bucket. Observed live: 260,602 undelivered report messages across twelve databases,
-- with the audit correctly re-detecting the same real gaps every boot.
--
-- It is also per-process, so every replica of a service reports the same divergence independently
-- — the same data requested N times for N pods, which no amount of in-memory bounding can fix.
--
-- Durable state fixes both: the cooldown and the attempt count survive restarts, and all replicas
-- share one record per divergent bucket. The key IS the identity of a divergence, so "have we
-- already asked about this exact thing?" becomes a primary-key lookup rather than a hope.
--
-- Dependencies: 032 (wh_settings)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_ledger (
  origin_service_id  UUID        NOT NULL,
  tenant_scope       TEXT        NOT NULL DEFAULT '',   -- '' rather than NULL so it can key
  event_type         TEXT        NOT NULL,
  stream_id          UUID        NOT NULL,              -- Guid.Empty for type/window-level entries
  origin_lo          BIGINT      NOT NULL,
  origin_hi          BIGINT      NOT NULL,
  local_lo           BIGINT      NOT NULL,
  local_hi           BIGINT      NOT NULL,
  last_reported_at   TIMESTAMPTZ,
  last_repair_at     TIMESTAMPTZ,
  repair_attempts    INTEGER     NOT NULL DEFAULT 0,
  last_touched       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  -- When this bucket FIRST diverged, never updated afterwards. last_touched moves on every
  -- sighting, so it answers "when did we last look", not "how long has this been broken" —
  -- and the second question is the one an operator acts on. A heal DELETEs the row, so a later
  -- re-divergence is genuinely new and starts its own clock.
  first_seen_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (origin_service_id, tenant_scope, event_type, stream_id)
);

-- Existing installs (the table shipped before this column did).
ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

COMMENT ON TABLE __SCHEMA__.wh_integrity_ledger IS
'One row per divergent bucket. The primary key is the identity of a divergence, so a repeat sighting updates a row instead of emitting another report, across restarts and across replicas.';

-- Bounded like the in-memory ledger was: reclaim the least-recently-touched rows past a ceiling so
-- a pathological divergent set cannot grow the table without limit.
CREATE INDEX IF NOT EXISTS idx_integrity_ledger_touched
  ON __SCHEMA__.wh_integrity_ledger (last_touched);

-- True when this divergence should be REPORTED now: first sighting, a changed signature (either
-- side's digest moved — progress or fresh damage, which also resets the repair budget), or the
-- cooldown elapsed. Records the sighting either way. Mirrors IntegrityRepairLedger.TryBeginReport
-- exactly; the only difference is where the memory lives.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_report(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID,
  p_origin_lo         BIGINT,
  p_origin_hi         BIGINT,
  p_local_lo          BIGINT,
  p_local_hi          BIGINT,
  p_now               TIMESTAMPTZ,
  p_cooldown_seconds  INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_scope   TEXT := COALESCE(p_tenant_scope, '');
  v_row     __SCHEMA__.wh_integrity_ledger%ROWTYPE;
  v_changed BOOLEAN;
  v_report  BOOLEAN;
BEGIN
  -- Lock the row for the duration so concurrent replicas serialise on this bucket and exactly one
  -- of them is told to report. This is the part in-memory state could never provide.
  SELECT * INTO v_row FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id
  FOR UPDATE;

  IF NOT FOUND THEN
    INSERT INTO __SCHEMA__.wh_integrity_ledger (
      origin_service_id, tenant_scope, event_type, stream_id,
      origin_lo, origin_hi, local_lo, local_hi, last_reported_at, repair_attempts, last_touched,
      first_seen_at)
    VALUES (p_origin_service_id, v_scope, p_event_type, p_stream_id,
            p_origin_lo, p_origin_hi, p_local_lo, p_local_hi, p_now, 0, p_now,
            p_now)
    ON CONFLICT (origin_service_id, tenant_scope, event_type, stream_id) DO NOTHING;
    -- A racing replica may have inserted first; only the winner reports.
    IF FOUND THEN
      RETURN TRUE;
    END IF;
    RETURN FALSE;
  END IF;

  v_changed := v_row.origin_lo IS DISTINCT FROM p_origin_lo
            OR v_row.origin_hi IS DISTINCT FROM p_origin_hi
            OR v_row.local_lo  IS DISTINCT FROM p_local_lo
            OR v_row.local_hi  IS DISTINCT FROM p_local_hi;

  v_report := v_row.last_reported_at IS NULL
           OR v_changed
           OR (p_now - v_row.last_reported_at) >= make_interval(secs => p_cooldown_seconds);

  UPDATE __SCHEMA__.wh_integrity_ledger
  SET origin_lo        = p_origin_lo,
      origin_hi        = p_origin_hi,
      local_lo         = p_local_lo,
      local_hi         = p_local_hi,
      -- A moved signature is a fresh incident: the repair budget starts over.
      repair_attempts  = CASE WHEN v_changed THEN 0 ELSE v_row.repair_attempts END,
      last_repair_at   = CASE WHEN v_changed THEN NULL ELSE v_row.last_repair_at END,
      last_reported_at = CASE WHEN v_report THEN p_now ELSE v_row.last_reported_at END,
      last_touched     = p_now
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id;

  RETURN v_report;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_report IS
'True when a divergence should be reported now (first sighting, changed signature, or cooldown elapsed). Row-locked so concurrent replicas cannot both report the same bucket.';

-- True when a repair request should be SENT now: the first attempt goes immediately, each later
-- attempt waits base x 2^(n-1), and past p_max_attempts the requester stops asking until the
-- signature changes. Records the attempt when true. Mirrors TryBeginRepair.
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

  IF v_row.repair_attempts >= p_max_attempts THEN
    RETURN FALSE;   -- budget spent; only a changed signature reopens it
  END IF;

  IF v_row.last_repair_at IS NOT NULL THEN
    -- Capped at 6 doublings so the wait cannot overflow, matching the in-memory ladder.
    v_doublings := LEAST(GREATEST(v_row.repair_attempts - 1, 0), 6);
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
'True when a repair should be requested now, on an exponential ladder capped at p_max_attempts. Attempt counts survive restarts, so a bucket can actually reach a terminal state.';

-- The bucket folded identical — forget it. A later divergence is a brand-new incident.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_healed(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id
    AND tenant_scope = COALESCE(p_tenant_scope, '')
    AND event_type = p_event_type
    AND stream_id = p_stream_id;
END;
$$;

-- Bound the table the way the in-memory ledger bounded its dictionary: drop the least-recently
-- touched rows past a ceiling. Healed buckets delete themselves; this covers the pathological case
-- of an unbounded divergent set.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_trim(p_max_entries INTEGER DEFAULT 100000)
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
  v_deleted BIGINT;
BEGIN
  WITH doomed AS (
    SELECT origin_service_id, tenant_scope, event_type, stream_id
    FROM __SCHEMA__.wh_integrity_ledger
    ORDER BY last_touched DESC
    OFFSET p_max_entries
  )
  DELETE FROM __SCHEMA__.wh_integrity_ledger l
  USING doomed d
  WHERE l.origin_service_id = d.origin_service_id AND l.tenant_scope = d.tenant_scope
    AND l.event_type = d.event_type AND l.stream_id = d.stream_id;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  RETURN v_deleted;
END;
$$;

-- The ledger, read as a GAUGE.
--
-- This is what replaces publishing a durable event per divergence sighting. Those events had no
-- consumer, and each one minted its own stream, so they grew without bound and could never be
-- reaped. More importantly they were the wrong SHAPE: a stream of past-tense notifications tells
-- an operator what was noticed, never what is currently broken, and it never goes down when
-- things heal. A row per unhealed bucket does both — mark_healed DELETEs, so these numbers fall
-- on their own as repair works.
--
-- p_max_attempts is the caller's MaxRepairAttemptsPerBucket. Buckets at or past it have stopped
-- asking for repair, which is exactly the set that needs human attention rather than patience.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_summary(p_max_attempts INTEGER)
RETURNS TABLE (
  unhealed_buckets       BIGINT,
  repair_exhausted       BIGINT,
  oldest_unhealed_secs   DOUBLE PRECISION
)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  SELECT
    COUNT(*)::BIGINT,
    COUNT(*) FILTER (WHERE repair_attempts >= p_max_attempts)::BIGINT,
    COALESCE(EXTRACT(EPOCH FROM (NOW() - MIN(first_seen_at))), 0)::DOUBLE PRECISION
  FROM __SCHEMA__.wh_integrity_ledger;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_ledger_summary IS
'Ledger as a gauge: currently-unhealed buckets, how many have exhausted their repair budget, and the age of the oldest. Replaces per-sighting report events, which had no consumer and never fell when things healed.';
