-- Migration: 103_ReapPerspectiveRowCaps
-- Purpose:  Bound CARDINALITY, not just age. Time-based retention never limits how many rows a
--           scope accumulates — a heavy tenant or user can hold thousands all created inside the
--           window — so a count cap keeps the newest N per scope and evicts the rest.
--
-- Companion, not alternative: Redis streams pair MAXLEN with age trimming, EventStoreDB pairs
-- $maxCount with $maxAge, Kafka pairs retention.bytes with retention.ms. A cap is a RANK, so it
-- cannot fold into the effective-expiry ladder (an instant); it is a second, independent rule
-- unioned with it.
--
-- Cadence: ranking needs a window function and no index removes the scan, so this is a separate
-- function run on a SLOWER cadence than the expiry sweep. A cap is a bound, not a deadline —
-- being late costs nothing, whereas expiry wants the standard cycle.
--
-- Dependencies: 101 (retention enrolment), 102 (expiry ladder sweep)

ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS row_cap_per_scope INTEGER;

-- Which scope key partitions the ranking: 'u' (per user) or 't' (per tenant). NULL with a cap set
-- means cap the whole table, which is rarely what anyone wants but is representable.
ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS row_cap_scope_key TEXT;

COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.row_cap_per_scope IS
  'Maximum rows retained per scope partition, ranked by updated_at (business time) descending.';
COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.row_cap_scope_key IS
  'Scope JSON key partitioning the cap ranking: u (per user), t (per tenant), or NULL for whole-table.';

CREATE OR REPLACE FUNCTION __SCHEMA__.reap_perspective_row_caps()
RETURNS TABLE(task TEXT, rows_affected INTEGER, duration_ms DOUBLE PRECISION, status TEXT) AS $$
DECLARE
  v_start      TIMESTAMPTZ := clock_timestamp();
  v_rows       INTEGER := 0;
  v_deleted    INTEGER := 0;
  v_debug_mode BOOLEAN := FALSE;
  v_reg        RECORD;
  v_partition  TEXT;
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
    WHERE row_retention_enrolled AND row_cap_per_scope IS NOT NULL
  LOOP
    CONTINUE WHEN NOT EXISTS (
      SELECT 1 FROM information_schema.tables
      WHERE table_schema = current_schema() AND table_name = v_reg.table_name);

    -- A NULL scope key ranks the whole table as one partition; otherwise partition on the scope
    -- JSON key. Ranking is by updated_at DESC — BUSINESS time — so "keep the newest N" means the
    -- most recently ACTIVE, and a rebuild reproduces the same ordering. Ranking on a wall-clock
    -- column would make a rebuild evict essentially arbitrary rows, since every row's timestamp
    -- would become the rebuild moment in write order.
    v_partition := CASE
      WHEN v_reg.row_cap_scope_key IS NULL THEN '1'
      ELSE format('scope ->> %L', v_reg.row_cap_scope_key)
    END;

    EXECUTE format(
      'DELETE FROM %I.%I WHERE id IN (
         SELECT id FROM (
           SELECT id, ROW_NUMBER() OVER (PARTITION BY %s ORDER BY updated_at DESC) AS rn
           FROM %I.%I
         ) ranked WHERE ranked.rn > $1)',
      current_schema(), v_reg.table_name, v_partition, current_schema(), v_reg.table_name)
    USING v_reg.row_cap_per_scope;

    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    v_rows := v_rows + v_deleted;
  END LOOP;

  RETURN QUERY SELECT
    'reap_perspective_row_caps'::TEXT,
    v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_perspective_row_caps() IS
  'Evicts rows ranked beyond a perspective''s per-scope cap, ordered by business time. Separate from '
  'the expiry sweep and intended for a slower cadence: ranking needs a window function no index avoids.';
