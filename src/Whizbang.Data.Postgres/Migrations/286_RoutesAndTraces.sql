-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 286_RoutesAndTraces.sql
-- Date: 2026-11-01
-- Description: Routes and traces: maintenance distills the staged full metadata into wh_routes and wh_traces, a Postgres
--              trace store rebuilds an event's full metadata from them, and each has its own retention (#1726, WS-2 S9b).
--
--              A route is the path events of one type take (285 stamps its id on every row); a trace is one event's
--              trip along it, with only what varies per message. wh_distill_event_metadata, called in batches by the
--              distillation maintenance step under the maintainer duty, takes staged rows in event id order, removes
--              them, and in the same statement:
--
--                1. inserts each new route (its normalized hops, built once per route);
--                2. adds the batch's occurrences, first and last seen, and duration statistics to each route with one
--                   aggregated upsert per route, never a write per event;
--                3. writes one trace per event: its residual hops (each hop without what its route holds: the hop type,
--                   topic, execution strategy, caller member and service name), the dispatch context and receptor
--                   invocations the slim row dropped, and the event's occurrence time, correlation and causation.
--
--              Removing the staged row in the statement that distills it makes each staged row count once, with no
--              cursor to keep. No event row is touched: its route id was stamped when it was written. Under the Full
--              and Slim forms nothing is distilled; what was staged before ages out.
--
--              Reconstruction (_wh_reconstruct_metadata, wh_trace_metadata): the staged form while it exists, then the
--              row plus its trace plus its route (lossless), then the row alone.
--
--              Retention, each its own window, skipped under debug mode: staged rows metadata_staging_days (2), traces
--              trace_retention_days (30), both partitioned daily and dropped by the partition manager when partitioning
--              is enabled and swept by reap_unbounded_ledgers otherwise (in the default partition once managed); routes
--              route_idle_days (365) after they were last taken.
--
--              Number 286: reserved for this change (285 to 289).
--
-- Dependencies: 285 (wh_event_metadata_staging, _wh_metadata_write_settings), 279 (reap_unbounded_ledgers, last word),
--               233 (wh_partition_sweep_relation), 077 (wh_event_body)
-- Objects: wh_routes, idx_routes_event_type, idx_routes_last_seen, wh_traces, wh_traces_default, idx_traces_correlation,
--          idx_traces_causation, idx_traces_occurred_at, _wh_try_uuid, _wh_try_timestamptz, _wh_timespan_ms,
--          _wh_route_hops, _wh_trace_hops, _wh_first_current_hop_at, _wh_hops_duration_ms, _wh_reconstruct_metadata,
--          wh_distill_event_metadata, wh_trace_metadata, reap_unbounded_ledgers
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('_wh_try_uuid');
SELECT __SCHEMA__.drop_all_overloads('_wh_try_timestamptz');
SELECT __SCHEMA__.drop_all_overloads('_wh_timespan_ms');
SELECT __SCHEMA__.drop_all_overloads('_wh_route_hops');
SELECT __SCHEMA__.drop_all_overloads('_wh_trace_hops');
SELECT __SCHEMA__.drop_all_overloads('_wh_first_current_hop_at');
SELECT __SCHEMA__.drop_all_overloads('_wh_hops_duration_ms');
SELECT __SCHEMA__.drop_all_overloads('_wh_reconstruct_metadata');
SELECT __SCHEMA__.drop_all_overloads('wh_distill_event_metadata');
SELECT __SCHEMA__.drop_all_overloads('wh_trace_metadata');

-- ===========================================================================================
-- The tables
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_routes (
  route_id UUID NOT NULL,
  event_type TEXT NOT NULL,
  hops JSONB NOT NULL,
  first_seen TIMESTAMPTZ NOT NULL,
  last_seen TIMESTAMPTZ NOT NULL,
  occurrences BIGINT NOT NULL DEFAULT 0,
  duration_count BIGINT NOT NULL DEFAULT 0,
  duration_sum_ms DOUBLE PRECISION NOT NULL DEFAULT 0,
  duration_min_ms DOUBLE PRECISION NULL,
  duration_max_ms DOUBLE PRECISION NULL,
  duration_buckets BIGINT[] NOT NULL DEFAULT '{0,0,0,0,0,0}',
  CONSTRAINT pk_routes PRIMARY KEY (route_id)
);

CREATE INDEX IF NOT EXISTS idx_routes_event_type ON __SCHEMA__.wh_routes (event_type);
CREATE INDEX IF NOT EXISTS idx_routes_last_seen ON __SCHEMA__.wh_routes (last_seen);

COMMENT ON TABLE __SCHEMA__.wh_routes IS
  'One row per route (286, #1726): the normalized path events of one type take (hop type, service, topic, execution '
  'strategy, caller class and member, policy decisions per hop), with occurrence counts and duration statistics '
  '(buckets: up to 1 ms, 10 ms, 100 ms, 1 s, 10 s, longer). Bounded by the architecture, kept route_idle_days after '
  'last_seen.';

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_traces (
  event_id UUID NOT NULL,
  recorded_at TIMESTAMPTZ NOT NULL,
  route_id UUID NOT NULL,
  occurred_at TIMESTAMPTZ NULL,
  correlation_id UUID NULL,
  causation_id UUID NULL,
  hops JSONB NOT NULL,
  extra JSONB NULL,
  CONSTRAINT pk_traces PRIMARY KEY (event_id, recorded_at)
) PARTITION BY RANGE (recorded_at);

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_traces_default PARTITION OF __SCHEMA__.wh_traces DEFAULT;

CREATE INDEX IF NOT EXISTS idx_traces_correlation ON __SCHEMA__.wh_traces (correlation_id);
CREATE INDEX IF NOT EXISTS idx_traces_causation ON __SCHEMA__.wh_traces (causation_id);
CREATE INDEX IF NOT EXISTS idx_traces_occurred_at ON __SCHEMA__.wh_traces (occurred_at);

COMMENT ON TABLE __SCHEMA__.wh_traces IS
  'One row per distilled event (286, #1726): its route, its occurrence time (the first Current hop''s), correlation and '
  'causation (the first hop''s), and the per-message values of its hops (hops: each hop without what its route holds; '
  'extra: the top-level keys the slim row dropped). Partitioned by day on recorded_at, kept trace_retention_days.';

-- ===========================================================================================
-- Parsing without raising
-- ===========================================================================================

-- Maintenance must never stop on one malformed value: a value that does not parse reads as NULL.
-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_MalformedValues_ReadAsNothing_AndNeverStopTheBatchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_try_uuid(p_text TEXT)
RETURNS UUID AS $$
  SELECT CASE WHEN p_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN p_text::UUID END;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_try_uuid(TEXT) IS 'Issue #1726: the text as a uuid, or NULL when it is not one.';

-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_MalformedValues_ReadAsNothing_AndNeverStopTheBatchAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_try_timestamptz(p_text TEXT)
RETURNS TIMESTAMPTZ AS $$
  SELECT CASE
    WHEN p_text ~ '^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])T([01]\d|2[0-3]):[0-5]\d:[0-5]\d(\.\d{1,7})?(Z|[+-]([01]\d|2[0-3]):[0-5]\d)$'
    THEN p_text::TIMESTAMPTZ
  END;
$$ LANGUAGE sql STABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_try_timestamptz(TEXT) IS
  'Issue #1726: an ISO 8601 timestamp as the hop writer writes it, or NULL when the text is not one.';

-- TimeSpan's constant ("c") form, [-][d.]hh:mm:ss[.fffffff], in milliseconds; NULL when the text is not one.
-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_AddsDurationStatistics_InOneUpdatePerRouteAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_timespan_ms(p_text TEXT)
RETURNS DOUBLE PRECISION AS $$
DECLARE
  m TEXT[];
BEGIN
  -- Plain assignments rather than a query: distillation calls this once a hop.
  m := regexp_match(COALESCE(p_text, ''), '^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$');
  IF m IS NULL THEN
    RETURN NULL;
  END IF;
  RETURN (CASE WHEN m[1] = '-' THEN -1 ELSE 1 END) * (
    COALESCE(m[2]::DOUBLE PRECISION, 0) * 86400000
    + m[3]::DOUBLE PRECISION * 3600000
    + m[4]::DOUBLE PRECISION * 60000
    + m[5]::DOUBLE PRECISION * 1000
    + COALESCE(('0.' || m[6])::DOUBLE PRECISION * 1000, 0));
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_timespan_ms(TEXT) IS
  'Issue #1726: a TimeSpan written in its constant form ([-][d.]hh:mm:ss[.fffffff]) in milliseconds, NULL otherwise.';

-- ===========================================================================================
-- Splitting a full hop list into its route and its trace, and back
-- ===========================================================================================

-- The route form of a hop list, as wh_routes keeps it: the raw hop type, service name, topic, execution strategy and
-- caller member (JSON null when absent, so a rebuilt hop has exactly the keys it was written with), and the derived
-- caller class and policy decisions for readers.
-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_WritesRoutesAndTraces_AndConsumesTheStagedRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_route_hops(p_hops JSONB)
RETURNS JSONB AS $$
  SELECT COALESCE(jsonb_agg(
    jsonb_build_object(
      'ty', h.hop -> 'ty',
      'sn', h.hop -> 'si' -> 'sn',
      'to', h.hop -> 'to',
      'es', h.hop -> 'es',
      'cm', h.hop -> 'cm',
      'cl', regexp_replace(regexp_replace(COALESCE(h.hop ->> 'cf', ''), '^.*[/\\]', ''), '\.[^.]*$', ''),
      'pd', COALESCE((
        SELECT jsonb_agg(jsonb_build_object('pn', d.decision -> 'pn', 'r', d.decision -> 'r',
                                            'm', COALESCE(d.decision -> 'm', 'false'::jsonb)) ORDER BY d.ord)
        FROM jsonb_array_elements(
          CASE WHEN jsonb_typeof(h.hop -> 'tr' -> 'd') = 'array' THEN h.hop -> 'tr' -> 'd' ELSE '[]'::jsonb END)
          WITH ORDINALITY AS d(decision, ord)), '[]'::jsonb))
    ORDER BY h.ord), '[]'::jsonb)
  FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_hops) = 'array' THEN p_hops ELSE '[]'::jsonb END)
    WITH ORDINALITY AS h(hop, ord);
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_route_hops(JSONB) IS
  'Issue #1726: the route form of a hop list: per hop the raw ty, si.sn, to, es and cm, the caller class (cl) and the '
  'policy decisions (pd: pn, r, m).';

-- The trace form: each hop without the keys its route holds (ty, to, es, cm, and sn inside si).
-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Reconstruct_WithinTheTraceWindow_IsLosslessAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_trace_hops(p_hops JSONB)
RETURNS JSONB AS $$
DECLARE
  v_out JSONB := '[]'::jsonb;
  v_hop JSONB;
BEGIN
  -- A loop rather than a query: distillation calls this once an event.
  IF jsonb_typeof(p_hops) IS DISTINCT FROM 'array' THEN
    RETURN v_out;
  END IF;
  FOR i IN 0 .. jsonb_array_length(p_hops) - 1 LOOP
    v_hop := p_hops -> i;
    IF jsonb_typeof(v_hop) = 'object' THEN
      v_hop := (v_hop - 'ty' - 'to' - 'es' - 'cm' - 'si')
        || CASE WHEN jsonb_typeof(p_hops -> i -> 'si') = 'object' THEN jsonb_build_object('si', (p_hops -> i -> 'si') - 'sn')
                WHEN p_hops -> i ? 'si' THEN jsonb_build_object('si', p_hops -> i -> 'si')
                ELSE '{}'::jsonb END;
    END IF;
    v_out := v_out || jsonb_build_array(v_hop);
  END LOOP;
  RETURN v_out;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_trace_hops(JSONB) IS
  'Issue #1726: the per-message part of a hop list: each hop without ty, to, es, cm and si.sn, which its route holds.';

-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:TraceStore_AnswersEveryQuery_LikeTheInMemoryStore_OnTheSameCorpusAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_first_current_hop_at(p_hops JSONB)
RETURNS TIMESTAMPTZ AS $$
BEGIN
  IF jsonb_typeof(p_hops) IS DISTINCT FROM 'array' THEN
    RETURN NULL;
  END IF;
  FOR i IN 0 .. jsonb_array_length(p_hops) - 1 LOOP
    IF COALESCE(p_hops -> i ->> 'ty', '0') = '0' THEN
      RETURN __SCHEMA__._wh_try_timestamptz(p_hops -> i ->> 'ts');
    END IF;
  END LOOP;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql STABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_first_current_hop_at(JSONB) IS
  'Issue #1726: the timestamp of the first Current hop, the moment the trace store orders and ranges events by.';

-- An occurrence's duration: its hops' durations added up; NULL when no hop has one.
-- <docs>data/event-store#routes-and-traces</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_AddsDurationStatistics_InOneUpdatePerRouteAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_hops_duration_ms(p_hops JSONB)
RETURNS DOUBLE PRECISION AS $$
DECLARE
  v_total DOUBLE PRECISION;
  v_ms DOUBLE PRECISION;
BEGIN
  IF jsonb_typeof(p_hops) IS DISTINCT FROM 'array' THEN
    RETURN NULL;
  END IF;
  FOR i IN 0 .. jsonb_array_length(p_hops) - 1 LOOP
    v_ms := __SCHEMA__._wh_timespan_ms(p_hops -> i ->> 'du');
    IF v_ms IS NOT NULL THEN
      v_total := COALESCE(v_total, 0) + v_ms;
    END IF;
  END LOOP;
  RETURN v_total;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_hops_duration_ms(JSONB) IS
  'Issue #1726: the sum of a hop list''s durations in milliseconds, NULL when no hop carries one.';

-- The full metadata from the row's form plus its trace and route: the row's hop list (Hops, or hops) rebuilt hop by
-- hop from the trace's residual and the route's fields, the trace's extra keys put back, and the route id removed.
-- <docs>data/event-store#reconstruction</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Reconstruct_WithinTheTraceWindow_IsLosslessAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_reconstruct_metadata(p_row JSONB, p_route_hops JSONB, p_trace_hops JSONB, p_extra JSONB)
RETURNS JSONB AS $$
  SELECT (COALESCE(p_row, '{}'::jsonb) - 'rid' - k.hops_key)
    || COALESCE(p_extra, '{}'::jsonb)
    || jsonb_build_object(k.hops_key, COALESCE((
      SELECT jsonb_agg(
        CASE WHEN jsonb_typeof(t.hop) = 'object' THEN
          (t.hop - 'si')
          || jsonb_strip_nulls(jsonb_build_object('ty', r.hop -> 'ty', 'to', r.hop -> 'to', 'es', r.hop -> 'es', 'cm', r.hop -> 'cm'))
          || CASE WHEN jsonb_typeof(t.hop -> 'si') = 'object'
                  THEN jsonb_build_object('si', (t.hop -> 'si') || jsonb_strip_nulls(jsonb_build_object('sn', r.hop -> 'sn')))
                  WHEN t.hop ? 'si' THEN jsonb_build_object('si', t.hop -> 'si')
                  ELSE '{}'::jsonb END
        ELSE t.hop END
        ORDER BY t.ord)
      FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_trace_hops) = 'array' THEN p_trace_hops ELSE '[]'::jsonb END)
        WITH ORDINALITY AS t(hop, ord)
      LEFT JOIN jsonb_array_elements(CASE WHEN jsonb_typeof(p_route_hops) = 'array' THEN p_route_hops ELSE '[]'::jsonb END)
        WITH ORDINALITY AS r(hop, ord) ON r.ord = t.ord), '[]'::jsonb))
  FROM (SELECT CASE WHEN p_row ? 'hops' AND NOT p_row ? 'Hops' THEN 'hops' ELSE 'Hops' END AS hops_key) k;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_reconstruct_metadata(JSONB, JSONB, JSONB, JSONB) IS
  'Issue #1726: rebuilds an event''s full metadata from its row''s form, its trace''s residual hops and extra keys, and '
  'its route''s hops. Lossless for an event distilled from its staged form.';

-- ===========================================================================================
-- Distillation
-- ===========================================================================================

-- One batch: up to p_batch_size staged rows, oldest event id first, removed and distilled in one statement.
-- <docs>data/event-store#distillation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_WritesRoutesAndTraces_AndConsumesTheStagedRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_ARepeatedRoute_OnlyIncrementsItsCountersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_AddsDurationStatistics_InOneUpdatePerRouteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_UnderSlimOrFull_ConsumesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Distill_TakesAtMostTheBatch_OldestFirstAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_distill_event_metadata(p_batch_size INTEGER DEFAULT 1000)
RETURNS TABLE(
  staged_consumed BIGINT,
  routes_added BIGINT,
  routes_updated BIGINT,
  traces_written BIGINT,
  status TEXT
) AS $$
DECLARE
  v_form TEXT;
BEGIN
  SELECT s.write_form INTO v_form FROM __SCHEMA__._wh_metadata_write_settings() s;
  IF v_form <> 'Linked' THEN
    RETURN QUERY SELECT 0::BIGINT, 0::BIGINT, 0::BIGINT, 0::BIGINT, ('skipped (metadata_write_form=' || v_form || ')')::TEXT;
    RETURN;
  END IF;

  RETURN QUERY
  WITH picked AS (
    SELECT s.event_id, s.recorded_at
    FROM __SCHEMA__.wh_event_metadata_staging s
    ORDER BY s.event_id
    LIMIT GREATEST(p_batch_size, 0)
    FOR UPDATE SKIP LOCKED
  ),
  batch AS (
    DELETE FROM __SCHEMA__.wh_event_metadata_staging s
    USING picked p
    WHERE s.event_id = p.event_id AND s.recorded_at = p.recorded_at
    RETURNING s.event_id, s.recorded_at, s.route_id, s.event_type, s.metadata
  ),
  occurrences AS (
    SELECT b.event_id, b.recorded_at, b.route_id, b.event_type, x.hops,
           NULLIF((SELECT jsonb_object_agg(e.key, e.value) FROM jsonb_each(b.metadata) e WHERE e.key IN ('dc', 'rin')),
                  '{}'::jsonb) AS extra,
           __SCHEMA__._wh_hops_duration_ms(x.hops) AS duration_ms
    FROM batch b
    CROSS JOIN LATERAL (SELECT COALESCE(b.metadata -> 'Hops', b.metadata -> 'hops', '[]'::jsonb) AS hops) x
  ),
  route_rows AS (
    INSERT INTO __SCHEMA__.wh_routes AS r
      (route_id, event_type, hops, first_seen, last_seen, occurrences,
       duration_count, duration_sum_ms, duration_min_ms, duration_max_ms, duration_buckets)
    SELECT
      o.route_id,
      (array_agg(o.event_type ORDER BY o.event_id))[1],
      __SCHEMA__._wh_route_hops((array_agg(o.hops ORDER BY o.event_id))[1]),
      MIN(o.recorded_at),
      MAX(o.recorded_at),
      COUNT(*),
      COUNT(*) FILTER (WHERE o.duration_ms > 0),
      COALESCE(SUM(o.duration_ms) FILTER (WHERE o.duration_ms > 0), 0),
      MIN(o.duration_ms) FILTER (WHERE o.duration_ms > 0),
      MAX(o.duration_ms) FILTER (WHERE o.duration_ms > 0),
      ARRAY[
        COUNT(*) FILTER (WHERE o.duration_ms > 0 AND o.duration_ms <= 1),
        COUNT(*) FILTER (WHERE o.duration_ms > 1 AND o.duration_ms <= 10),
        COUNT(*) FILTER (WHERE o.duration_ms > 10 AND o.duration_ms <= 100),
        COUNT(*) FILTER (WHERE o.duration_ms > 100 AND o.duration_ms <= 1000),
        COUNT(*) FILTER (WHERE o.duration_ms > 1000 AND o.duration_ms <= 10000),
        COUNT(*) FILTER (WHERE o.duration_ms > 10000)]::BIGINT[]
    FROM occurrences o
    GROUP BY o.route_id
    ORDER BY o.route_id
    ON CONFLICT (route_id) DO UPDATE SET
      first_seen = LEAST(r.first_seen, EXCLUDED.first_seen),
      last_seen = GREATEST(r.last_seen, EXCLUDED.last_seen),
      occurrences = r.occurrences + EXCLUDED.occurrences,
      duration_count = r.duration_count + EXCLUDED.duration_count,
      duration_sum_ms = r.duration_sum_ms + EXCLUDED.duration_sum_ms,
      duration_min_ms = LEAST(r.duration_min_ms, EXCLUDED.duration_min_ms),
      duration_max_ms = GREATEST(r.duration_max_ms, EXCLUDED.duration_max_ms),
      duration_buckets = ARRAY(
        SELECT u.a + u.b FROM unnest(r.duration_buckets, EXCLUDED.duration_buckets) WITH ORDINALITY AS u(a, b, ord) ORDER BY u.ord)
    RETURNING (xmax = 0) AS inserted
  ),
  traces AS (
    INSERT INTO __SCHEMA__.wh_traces (event_id, recorded_at, route_id, occurred_at, correlation_id, causation_id, hops, extra)
    SELECT o.event_id, o.recorded_at, o.route_id,
           __SCHEMA__._wh_first_current_hop_at(o.hops),
           __SCHEMA__._wh_try_uuid(o.hops -> 0 ->> 'co'),
           __SCHEMA__._wh_try_uuid(o.hops -> 0 ->> 'ca'),
           __SCHEMA__._wh_trace_hops(o.hops),
           o.extra
    FROM occurrences o
    RETURNING 1
  )
  SELECT
    (SELECT COUNT(*) FROM batch),
    (SELECT COUNT(*) FILTER (WHERE rr.inserted) FROM route_rows rr),
    (SELECT COUNT(*) FILTER (WHERE NOT rr.inserted) FROM route_rows rr),
    (SELECT COUNT(*) FROM traces),
    'ok'::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_distill_event_metadata(INTEGER) IS
  'Issue #1726: distills up to p_batch_size staged metadata rows (oldest event id first) into wh_routes (one upsert '
  'per route) and wh_traces (one row per event), removing them from wh_event_metadata_staging in the same statement. '
  'Skipped unless metadata_write_form is Linked. Called by the distillation maintenance step.';

-- ===========================================================================================
-- Reading a trace back
-- ===========================================================================================

-- Each event's full metadata, from the staged form while it exists, else the row plus its trace plus its route, else
-- the row alone; its payload; its route id; and which of the three answered.
-- <docs>data/event-store#reconstruction</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:GetByMessageId_ReadsStaging_ThenTraceAndRoute_ThenTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Reconstruct_WithinTheTraceWindow_IsLosslessAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Reconstruct_AfterTheTraceAgesOut_KeepsTheRouteAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_trace_metadata(p_event_ids UUID[])
RETURNS TABLE(
  event_id UUID,
  metadata JSONB,
  event_data JSONB,
  route_id UUID,
  source TEXT
) AS $$
  SELECT
    ids.eid,
    COALESCE(CASE
      WHEN st.metadata IS NOT NULL THEN st.metadata
      WHEN tr.hops IS NOT NULL AND ro.hops IS NOT NULL
        THEN __SCHEMA__._wh_reconstruct_metadata(eb.metadata, ro.hops, tr.hops, tr.extra)
      ELSE eb.metadata - 'rid'
    END, '{}'::jsonb),
    eb.event_data,
    COALESCE(st.route_id, tr.route_id, __SCHEMA__._wh_try_uuid(eb.metadata ->> 'rid')),
    CASE
      WHEN st.metadata IS NOT NULL THEN 'staged'
      WHEN tr.hops IS NOT NULL AND ro.hops IS NOT NULL THEN 'trace'
      ELSE 'row'
    END
  FROM (SELECT DISTINCT u.eid FROM unnest(p_event_ids) AS u(eid)) ids
  LEFT JOIN LATERAL (
    SELECT s.metadata, s.route_id FROM __SCHEMA__.wh_event_metadata_staging s WHERE s.event_id = ids.eid LIMIT 1) st ON TRUE
  LEFT JOIN LATERAL (
    SELECT t.hops, t.extra, t.route_id FROM __SCHEMA__.wh_traces t WHERE t.event_id = ids.eid LIMIT 1) tr ON TRUE
  LEFT JOIN __SCHEMA__.wh_routes ro ON ro.route_id = tr.route_id
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = ids.eid
  WHERE st.metadata IS NOT NULL OR tr.hops IS NOT NULL OR eb.event_id IS NOT NULL;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_trace_metadata(UUID[]) IS
  'Issue #1726: each event''s full metadata (staged form, else row + trace + route, else the row), payload and route '
  'id, and the source that answered (staged, trace or row). Read by the Postgres trace store.';

-- ===========================================================================================
-- Retention
-- ===========================================================================================

-- reap_unbounded_ledgers, last word 279_CollectiveApplyProgress.sql, copied whole, with three more tasks:
-- staged metadata, traces and idle routes.
-- <docs>fundamentals/workers/maintenance-steps#ledger-retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_OlderThanTheWindow_IsRemoved_YoungerIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:Log_ConfiguredWindow_IsHonoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:ScheduleRuns_OlderThanTheWindow_AreRemoved_ButTheNewestRunOfEachScheduleIsKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:StreamPurgeAudit_IsKeptForeverByDefault_AndBoundedWhenConfiguredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:IntegrityLedger_IsTrimmedToTheConfiguredCount_NewestKeptAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LedgerRetentionSqlTests.cs:DebugMode_KeepsEveryLedgerRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceConverted_RetentionDropsPartitions_InsteadOfDeletingRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:AConvertedLog_WithPartitioningSwitchedOff_IsStillBoundedByTheRowDeleteAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceManaged_TheRowDeleteSweepsOnlyTheDefaultPartition_AndLeavesPartitionsToTheDropAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Partitioning/LogPartitionConversionTests.cs:OnceManaged_WithoutADefaultPartition_TheRowDeleteSkipsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPartitionManagerSqlTests.cs:TheLog_OnceManaged_IsSweptInItsDefaultPartitionOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_RemovesAFinishedRecordPastTheHorizon_AndKeepsAYoungerOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_HonorsTheConfiguredHorizonAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_KeepsAFinishedRecord_WhileItsWorkRowIsPendingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_KeepsUnfinishedAndAbandonedRecords_AndTheFinishedRecordsOfADeadLetteredCollectiveAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressSqlTests.cs:TheSweep_UnderDebugMode_KeepsEveryRecordAsync</tests>
-- <docs>data/event-store#retention</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Retention_EachWindow_RemovesOnlyWhatIsPastItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MetadataDistillationSqlTests.cs:Retention_DebugMode_RemovesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.reap_unbounded_ledgers(p_batch_size INTEGER DEFAULT 5000)
RETURNS TABLE(
  task_name TEXT,
  rows_affected BIGINT,
  duration_ms DOUBLE PRECISION,
  status TEXT
) AS $$
DECLARE
  v_debug_mode BOOLEAN;
  v_start TIMESTAMPTZ;
  v_rows BIGINT;
  v_batch BIGINT;
  v_log_days INTEGER;
  v_run_days INTEGER;
  v_audit_days INTEGER;
  v_ledger_max INTEGER;
  v_log_relation TEXT;
  v_log_default BOOLEAN;
  v_progress_days INTEGER;
  v_staging_days INTEGER;   -- 286
  v_trace_days INTEGER;     -- 286
  v_route_days INTEGER;     -- 286
  v_relation TEXT;          -- 286: the relation a partitioned ledger's row sweep works on
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'), FALSE)
    INTO v_debug_mode;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'log_retention_days'), 14)
    INTO v_log_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'schedule_run_retention_days'), 30)
    INTO v_run_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'stream_purge_audit_retention_days'), 0)
    INTO v_audit_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'integrity_ledger_max_entries'), 100000)
    INTO v_ledger_max;
  -- 286 (#1726): the staged metadata, trace and route windows.
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'metadata_staging_days'), 2)
    INTO v_staging_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'trace_retention_days'), 30)
    INTO v_trace_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'route_idle_days'), 365)
    INTO v_route_days;
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings WHERE setting_key = 'collective_apply_progress_retention_days'), 7)
    INTO v_progress_days;
  -- 238: the relation the wh_log row delete works on (233): wh_log itself while it is plain or partition maintenance
  -- is off; once it is partitioned and maintained, only its default partition, which holds the rows no partition
  -- covers and which no partition drop ever reaches; NULL when a maintained wh_log has no default partition.
  v_log_relation := __SCHEMA__.wh_partition_sweep_relation('wh_log');
  SELECT COALESCE((SELECT c.relispartition FROM pg_class c WHERE c.oid = to_regclass(v_log_relation)), FALSE)
    INTO v_log_default;

  -- wh_log, by logged_at (indexed, 028; a partition inherits the parent's index).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND v_log_relation IS NOT NULL THEN
    LOOP
      EXECUTE format(
        'DELETE FROM %s WHERE log_id IN (SELECT l.log_id FROM %s l WHERE l.logged_at < $1 LIMIT $2)',
        v_log_relation, v_log_relation)
        USING NOW() - (v_log_days * INTERVAL '1 day'), p_batch_size;
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_log_entries'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_log_relation IS NULL THEN 'skipped (partitioned: no default partition)'
      WHEN v_log_default THEN 'ok (partitioned: the default partition only; expired partitions are dropped)'
      ELSE 'ok'
    END::TEXT;

  -- wh_schedule_runs, by fired_at, never a schedule's newest run (the (schedule_id, fired_at DESC) index, 066,
  -- answers "is there a newer run" for each candidate).
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_schedule_runs
      WHERE run_id IN (
        SELECT r.run_id FROM __SCHEMA__.wh_schedule_runs r
        WHERE r.fired_at < NOW() - (v_run_days * INTERVAL '1 day')
          AND EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_schedule_runs newer
            WHERE newer.schedule_id = r.schedule_id AND newer.fired_at > r.fired_at)
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_schedule_runs'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- wh_stream_purge_audit, by purged_at, only when an operator set a window.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode AND v_audit_days > 0 THEN
    DELETE FROM __SCHEMA__.wh_stream_purge_audit a
    WHERE a.purged_at < NOW() - (v_audit_days * INTERVAL '1 day');
    GET DIAGNOSTICS v_rows = ROW_COUNT;
  END IF;
  RETURN QUERY SELECT
    'purge_old_stream_purge_audit'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_audit_days = 0 THEN 'kept (stream_purge_audit_retention_days=0)'
      ELSE 'ok'
    END::TEXT;

  -- wh_integrity_ledger, to its row cap, by the trim 090 shipped.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    v_rows := __SCHEMA__.wh_integrity_ledger_trim(v_ledger_max);
  END IF;
  RETURN QUERY SELECT
    'trim_integrity_ledger'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- 279: wh_collective_apply_progress, a finished record once it is past its horizon (by applied_at) and the work row
  -- of its collective is no longer pending, so a re-delivery of a pending collective still finds its finished apply.
  -- Never an abandoned record, nor a finished record of a collective that has one: those go with the dead letter.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_collective_apply_progress d
      WHERE (d.event_id, d.entry_key) IN (
        SELECT p.event_id, p.entry_key FROM __SCHEMA__.wh_collective_apply_progress p
        WHERE p.completed
          AND NOT p.abandoned
          AND p.applied_at < NOW() - (v_progress_days * INTERVAL '1 day')
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_collective_apply_progress a
            WHERE a.event_id = p.event_id AND a.abandoned)
          AND NOT EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
            WHERE pe.event_id = p.event_id AND pe.processed_at IS NULL AND pe.perspective_name = '__collective__')
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_collective_apply_progress'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;

  -- 286 (#1726): staged metadata maintenance has not distilled, by recorded_at, on the relation the partition manager
  -- leaves to the row sweep (the table, or its default partition once partitioned and maintained).
  v_start := clock_timestamp();
  v_rows := 0;
  v_relation := __SCHEMA__.wh_partition_sweep_relation('wh_event_metadata_staging');
  IF NOT v_debug_mode AND v_relation IS NOT NULL THEN
    LOOP
      EXECUTE format(
        'DELETE FROM %s s WHERE (s.event_id, s.recorded_at) IN (SELECT x.event_id, x.recorded_at FROM %s x WHERE x.recorded_at < $1 LIMIT $2)',
        v_relation, v_relation)
        USING NOW() - (v_staging_days * INTERVAL '1 day'), p_batch_size;
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_staged_metadata'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_relation IS NULL THEN 'skipped (partitioned: no default partition)'
      ELSE 'ok'
    END::TEXT;

  -- 286 (#1726): traces past their window, the same way.
  v_start := clock_timestamp();
  v_rows := 0;
  v_relation := __SCHEMA__.wh_partition_sweep_relation('wh_traces');
  IF NOT v_debug_mode AND v_relation IS NOT NULL THEN
    LOOP
      EXECUTE format(
        'DELETE FROM %s t WHERE (t.event_id, t.recorded_at) IN (SELECT x.event_id, x.recorded_at FROM %s x WHERE x.recorded_at < $1 LIMIT $2)',
        v_relation, v_relation)
        USING NOW() - (v_trace_days * INTERVAL '1 day'), p_batch_size;
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_old_traces'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE
      WHEN v_debug_mode THEN 'skipped (debug_mode=true)'
      WHEN v_relation IS NULL THEN 'skipped (partitioned: no default partition)'
      ELSE 'ok'
    END::TEXT;

  -- 286 (#1726): routes not taken for route_idle_days (idx_routes_last_seen). A route still taken is never removed.
  v_start := clock_timestamp();
  v_rows := 0;
  IF NOT v_debug_mode THEN
    LOOP
      DELETE FROM __SCHEMA__.wh_routes
      WHERE route_id IN (
        SELECT r.route_id FROM __SCHEMA__.wh_routes r
        WHERE r.last_seen < NOW() - (v_route_days * INTERVAL '1 day')
        LIMIT p_batch_size);
      GET DIAGNOSTICS v_batch = ROW_COUNT;
      v_rows := v_rows + v_batch;
      EXIT WHEN v_batch < p_batch_size;
    END LOOP;
  END IF;
  RETURN QUERY SELECT
    'purge_idle_routes'::TEXT, v_rows,
    EXTRACT(MILLISECONDS FROM clock_timestamp() - v_start)::DOUBLE PRECISION,
    CASE WHEN v_debug_mode THEN 'skipped (debug_mode=true)' ELSE 'ok' END::TEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_unbounded_ledgers(INTEGER) IS
  'Issue #1572: bounds wh_log, wh_schedule_runs (keeping each schedule''s newest run), wh_stream_purge_audit '
  '(opt-in) and wh_integrity_ledger, in batches, from their wh_settings windows. Called by perform_maintenance. '
  'Issue #1611: skips the wh_log row delete while wh_log is partitioned and partition maintenance is enabled. '
  'Issue #1694: a partitioned and maintained wh_log is swept in its default partition only (wh_partition_sweep_relation). '
  'Issue #1969: finished collective apply records past collective_apply_progress_retention_days (default 7), once the '
  'collective''s work row is no longer pending. '
  'Issue #1726: and staged metadata past metadata_staging_days, traces past trace_retention_days (both the same '
  'way), and routes not taken for route_idle_days.';
