-- Migration: 113_CapSweepSafetyAndFoldWatermarks
-- Purpose:  Close the retention deferrals that gate safe adoption.
--           (1) CAP SWEEP SAFETY: the cap sweep gains the same two guards the expiry sweep has had
--               since 104 — the acknowledgement gate (a newly-declared cap REPORTS before it
--               removes) and a batch bound (a first sweep over a large backlog drains across
--               cycles, never in one statement). Without these, the first deploy after declaring
--               a cap would evict every over-cap row in one unbounded, unannounced statement.
--               The collect function's cap side re-gains parity (offer == destroy, always).
--           (2) FOLD WATERMARKS: a stream folds into wh_apply_paths exactly once — the watermark
--               makes fold idempotent BY MECHANISM (kills the re-close double-fold), lets the
--               pointer prune fold-before-discard safely, and enables idle-based settled-stream
--               folding.
--
-- Dependencies: 104 (ack gate), 111 (holds/collect), 112 (journal/fold), 076 (pointer prune)

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- (2a) The watermark: one row per folded stream. PRESENCE means "this stream's shape is already
-- in the counts"; the fold skips watermarked streams, so callers need no coordination.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_apply_fold_watermarks (
  stream_id UUID        NOT NULL PRIMARY KEY,
  folded_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_apply_fold_watermarks IS
  'Streams whose apply path has folded into wh_apply_paths. The fold skips watermarked streams, '
  'making fold-exactly-once a mechanism rather than a caller contract.';

-- (2b) Fold re-created from 112: watermark-aware. Only unwatermarked streams fold; folded streams
-- are watermarked in the same statement chain.
CREATE OR REPLACE FUNCTION __SCHEMA__.fold_stream_apply_paths(p_stream_ids UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_folded INTEGER := 0;
  v_unfolded UUID[];
BEGIN
  SELECT COALESCE(array_agg(ids.id), '{}')
    INTO v_unfolded
    FROM unnest(p_stream_ids) AS ids(id)
   WHERE NOT EXISTS (
     SELECT 1 FROM __SCHEMA__.wh_apply_fold_watermarks w WHERE w.stream_id = ids.id);

  IF array_length(v_unfolded, 1) IS NULL THEN
    RETURN 0;
  END IF;

  WITH runs AS (
    SELECT es.stream_id, es.version, es.event_type, es.created_at,
           CASE WHEN lag(es.event_type) OVER (PARTITION BY es.stream_id ORDER BY es.version)
                     IS DISTINCT FROM es.event_type THEN 1 ELSE 0 END AS run_break
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = ANY(v_unfolded)
  ),
  grouped AS (
    SELECT stream_id, event_type, created_at,
           SUM(run_break) OVER (PARTITION BY stream_id ORDER BY version) AS run_no
    FROM runs
  ),
  collapsed AS (
    SELECT stream_id, run_no,
           CASE WHEN COUNT(*) > 1 THEN MIN(event_type) || '+' ELSE MIN(event_type) END AS element,
           MAX(created_at) AS last_at
    FROM grouped
    GROUP BY stream_id, run_no
  ),
  paths AS (
    SELECT stream_id,
           array_agg(element ORDER BY run_no) AS path,
           MAX(last_at) AS head_at
    FROM collapsed
    GROUP BY stream_id
  ),
  marked AS (
    INSERT INTO __SCHEMA__.wh_apply_fold_watermarks (stream_id)
    SELECT stream_id FROM paths
    ON CONFLICT (stream_id) DO NOTHING
    RETURNING stream_id
  ),
  shapes AS (
    SELECT p.path, COUNT(*) AS n, MIN(p.head_at) AS first_at, MAX(p.head_at) AS last_at
    FROM paths p
    JOIN marked m ON m.stream_id = p.stream_id
    GROUP BY p.path
  )
  INSERT INTO __SCHEMA__.wh_apply_paths AS ap (path, stream_count, first_seen, last_seen)
  SELECT s.path, s.n, s.first_at, s.last_at FROM shapes s
  ON CONFLICT (path) DO UPDATE SET
    stream_count = ap.stream_count + EXCLUDED.stream_count,
    first_seen = LEAST(ap.first_seen, EXCLUDED.first_seen),
    last_seen = GREATEST(ap.last_seen, EXCLUDED.last_seen);

  GET DIAGNOSTICS v_folded = ROW_COUNT;
  RETURN v_folded;
END;
$$ LANGUAGE plpgsql;

-- (2c) Settled-stream folding: fold streams idle past the window that nothing has folded yet —
-- the proposal's "settled fold once", detached from any destruction. Non-destructive, so no
-- debug_mode gate; bounded so a large backlog drains across cycles.
CREATE OR REPLACE FUNCTION __SCHEMA__.fold_settled_apply_paths(
  p_idle_seconds BIGINT,
  p_limit INTEGER DEFAULT 1000)
RETURNS INTEGER AS $$
DECLARE
  v_settled UUID[];
BEGIN
  SELECT COALESCE(array_agg(s.stream_id), '{}')
    INTO v_settled
    FROM (
      SELECT es.stream_id
      FROM __SCHEMA__.wh_event_store es
      WHERE NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_apply_fold_watermarks w WHERE w.stream_id = es.stream_id)
      GROUP BY es.stream_id
      HAVING MAX(es.created_at) < NOW() - make_interval(secs => p_idle_seconds)
      LIMIT p_limit
    ) s;

  IF array_length(v_settled, 1) IS NULL THEN
    RETURN 0;
  END IF;

  RETURN __SCHEMA__.fold_stream_apply_paths(v_settled);
END;
$$ LANGUAGE plpgsql;

-- (2d) Pointer prune re-created VERBATIM from 076 with fold-before-discard: the streams about to
-- lose pointers fold FIRST (watermark-aware — already-folded streams cost nothing), so the pruned
-- prefix of a path is never lost unfolded.
CREATE OR REPLACE FUNCTION __SCHEMA__.prune_ancient_ephemeral_pointers()
RETURNS TABLE(rows_pruned BIGINT, status TEXT) AS $$
DECLARE
  v_enabled BOOLEAN;
  v_debug BOOLEAN;
  v_retention_days INTEGER;
  v_dedup_days INTEGER;
  v_interval_days INTEGER;
  v_horizon_days INTEGER;
  v_claimed BIGINT;
  v_rows BIGINT;
  v_fold_targets UUID[];
BEGIN
  -- Opt-in gate. Pruning append-only pointers is a deliberate storage-economy choice; off by default.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_deep_maintenance_enabled'),
    FALSE) INTO v_enabled;
  IF NOT v_enabled THEN
    RETURN QUERY SELECT 0::BIGINT, 'disabled'::TEXT;
    RETURN;
  END IF;

  -- debug_mode retains forensic rows — the reaper (073) and completed-message purges honor it, so must this.
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'),
    FALSE) INTO v_debug;
  IF v_debug THEN
    RETURN QUERY SELECT 0::BIGINT, 'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_prune_interval_days'),
    30) INTO v_interval_days;

  -- Self-gate (multi-pod safe): atomically claim this tick by advancing the watermark ONLY if the interval
  -- has elapsed. If no row is updated, another pod already claimed it this interval (or it is not yet due) —
  -- return without pruning. The conditional UPDATE is the atomic CAS; no advisory lock needed.
  UPDATE __SCHEMA__.wh_settings
    SET setting_value = NOW()::TEXT
    WHERE setting_key = 'ephemeral_pointer_prune_last_run'
      AND setting_value::TIMESTAMPTZ < NOW() - (v_interval_days * INTERVAL '1 day');
  GET DIAGNOSTICS v_claimed = ROW_COUNT;
  IF v_claimed = 0 THEN
    RETURN QUERY SELECT 0::BIGINT, 'not due'::TEXT;
    RETURN;
  END IF;

  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_pointer_retention_days'),
    90) INTO v_retention_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'dedup_retention_days'),
    30) INTO v_dedup_days;
  -- Safety floor: the horizon can NEVER be shorter than the dedup window — a still-deduped redelivery must
  -- never find its pointer already gone. Operators widen ephemeral_pointer_retention_days for longer
  -- cross-service replay windows; they can never accidentally shorten it below dedup.
  v_horizon_days := GREATEST(v_retention_days, v_dedup_days);

  -- Fold-before-discard: the streams about to lose pointers fold their CURRENT full path first.
  -- Watermark-aware, so a stream folded earlier (a close, a settled fold) costs one anti-join probe.
  SELECT COALESCE(array_agg(DISTINCT es.stream_id), '{}')
    INTO v_fold_targets
    FROM __SCHEMA__.wh_event_store es
   WHERE (es.flags & 8) = 8
     AND es.created_at < NOW() - (v_horizon_days * INTERVAL '1 day')
     AND NOT EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
     AND NOT EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
       WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
     AND es.version < (
       SELECT MAX(es2.version) FROM __SCHEMA__.wh_event_store es2
       WHERE es2.stream_id = es.stream_id);
  IF array_length(v_fold_targets, 1) IS NOT NULL THEN
    PERFORM __SCHEMA__.fold_stream_apply_paths(v_fold_targets);
  END IF;

  DELETE FROM __SCHEMA__.wh_event_store es
  WHERE (es.flags & 8) = 8                                            -- ephemeral pointers only
    AND es.created_at < NOW() - (v_horizon_days * INTERVAL '1 day')   -- past the horizon
    AND NOT EXISTS (                                                  -- body already reaped by tier-1 (073)
      SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
    AND NOT EXISTS (                                                  -- no pending perspective work item
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
      WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
    AND es.version < (                                                -- KEEP the newest pointer per stream:
      SELECT MAX(es2.version) FROM __SCHEMA__.wh_event_store es2       -- the surviving "tombstone" keeps the
      WHERE es2.stream_id = es.stream_id);                            -- stream flagged ephemeral (guard) and
                                                                      -- is the cursor's last_event_id target.
  GET DIAGNOSTICS v_rows = ROW_COUNT;

  RETURN QUERY SELECT v_rows, 'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- (1a) Cap sweep re-created from 112 with the 104 safety pair: acknowledgement-gated and batched.
-- The zero-argument signature is DROPPED first — a defaulted parameter does not replace a no-arg
-- overload, it sits beside it (42725; the 085/104 trap).
DROP FUNCTION IF EXISTS __SCHEMA__.reap_perspective_row_caps();

CREATE OR REPLACE FUNCTION __SCHEMA__.reap_perspective_row_caps(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start      TIMESTAMPTZ := clock_timestamp();
  v_rows       INTEGER := 0;
  v_deleted    INTEGER := 0;
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
  v_pending    INTEGER := 0;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN QUERY SELECT
      'reap_perspective_row_caps'::TEXT, 0,
      EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
      'skipped (debug_mode=true)'::TEXT;
    RETURN;
  END IF;

  FOR v_reg IN
    SELECT table_name, row_cap_per_scope, row_cap_scope_key
    FROM __SCHEMA__.wh_perspective_registry
    WHERE row_retention_enrolled AND retention_enforcement_acknowledged AND row_cap_per_scope IS NOT NULL
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = current_schema() AND table_name = v_reg.table_name);

    v_partition := CASE
      WHEN v_reg.row_cap_scope_key IS NULL THEN '1'
      ELSE format('scope ->> %L', v_reg.row_cap_scope_key)
    END;

    EXECUTE format(
      'WITH del AS (
         DELETE FROM %I.%I WHERE id IN (
           SELECT id FROM (
             SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
             FROM %I.%I
           ) ranked WHERE ranked.rn > $1
           LIMIT $2)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
         RETURNING id)
       INSERT INTO %I.wh_row_eviction_journal (table_name, row_id)
       SELECT %L, id FROM del
       ON CONFLICT (table_name, row_id) DO NOTHING',
      current_schema(), v_reg.table_name, v_partition, current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name)
    USING v_reg.row_cap_per_scope, p_batch_size;

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    v_rows := v_rows + v_deleted;
    IF v_deleted >= p_batch_size THEN
      v_pending := v_pending + 1;
    END IF;
  END LOOP;

  RETURN QUERY SELECT
    'reap_perspective_row_caps'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_pending > 0
      THEN format('ok (draining: %s perspective(s) hit the batch bound)', v_pending)
      ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_perspective_row_caps(INTEGER) IS
  'Evicts rows ranked beyond a perspective''s per-scope cap, ordered by business time. Gated on '
  'retention_enforcement_acknowledged (a declared cap REPORTS before it removes) and batched so a '
  'first sweep over a large backlog drains across cycles. Holds honored; victims journaled.';

-- (1b) Collect re-created from 111 with the cap side ack-gated — offer == destroy, always.
CREATE OR REPLACE FUNCTION __SCHEMA__.collect_perspective_row_reap_targets(
  p_clr_type_names TEXT[],
  p_per_table_limit INTEGER DEFAULT 500)
RETURNS TABLE(o_clr_type_name TEXT, o_table_name TEXT, o_row_id UUID, o_scope JSONB, o_data JSONB, o_reason TEXT) AS $$
DECLARE
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;

  IF v_debug_mode THEN
    RETURN;
  END IF;

  FOR v_reg IN
    SELECT r.clr_type_name, r.table_name, r.row_ttl_seconds, r.row_max_age_seconds,
           r.row_cap_per_scope, r.row_cap_scope_key, r.retention_enforcement_acknowledged
    FROM __SCHEMA__.wh_perspective_registry r
    WHERE r.row_retention_enrolled AND r.clr_type_name = ANY(p_clr_type_names)
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = current_schema() AND table_name = v_reg.table_name);

    -- BOTH sides gate on acknowledgement now (the cap sweep gained the gate in this migration).
    CONTINUE WHEN NOT v_reg.retention_enforcement_acknowledged;

    -- Expiry ladder.
    RETURN QUERY EXECUTE format(
      'SELECT %L::TEXT, %L::TEXT, id, scope, data, ''ttl''::TEXT FROM %I.%I WHERE
            ((expires_at IS NOT NULL AND expires_at < NOW())
         OR (expires_at IS NULL AND $1 IS NOT NULL
             AND updated_at < NOW() - make_interval(secs => $1))
         OR ($2 IS NOT NULL
             AND created_at < NOW() - make_interval(secs => $2)))
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = id AND h.hold_until > NOW())
       LIMIT $3',
      v_reg.clr_type_name, v_reg.table_name, current_schema(), v_reg.table_name,
      current_schema(), v_reg.table_name)
    USING v_reg.row_ttl_seconds, v_reg.row_max_age_seconds, p_per_table_limit;

    -- Cap overflow.
    IF v_reg.row_cap_per_scope IS NOT NULL THEN
      v_partition := CASE
        WHEN v_reg.row_cap_scope_key IS NULL THEN '1'
        ELSE format('scope ->> %L', v_reg.row_cap_scope_key)
      END;

      RETURN QUERY EXECUTE format(
        'SELECT %L::TEXT, %L::TEXT, t.id, t.scope, t.data, ''cap''::TEXT FROM %I.%I t
         WHERE t.id IN (
           SELECT id FROM (
             SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
             FROM %I.%I
           ) ranked WHERE ranked.rn > $1)
         AND NOT EXISTS (
           SELECT 1 FROM %I.wh_perspective_row_hold h
           WHERE h.table_name = %L AND h.row_id = t.id AND h.hold_until > NOW())
         LIMIT $2',
        v_reg.clr_type_name, v_reg.table_name, current_schema(), v_reg.table_name,
        v_partition, current_schema(), v_reg.table_name,
        current_schema(), v_reg.table_name)
      USING v_reg.row_cap_per_scope, p_per_table_limit;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
