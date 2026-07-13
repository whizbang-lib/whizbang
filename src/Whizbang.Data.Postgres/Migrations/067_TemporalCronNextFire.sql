-- Migration: 067_TemporalCronNextFire.sql
-- Date: 2026-07-13 (F2 temporal engine — increment 3b: DB-side recurrence next-fire)
-- Description: The DB half of the dual C#/DB recurrence engine. Home-grown (no pg_cron / no
--              extension) so the schedule worker's atomic claim+advance can compute the next
--              next_fire_at in SQL, in one transaction, without a C# round-trip. Mirrors the C#
--              CronExpression + IntervalRecurrenceRule exactly:
--                * _wh_cron_field  — parse one cron field to a sorted INT[] of allowed values
--                                    (*, values, ranges, steps */n a-b/n a/n, lists, JAN..DEC,
--                                    SUN..SAT); raises on malformed input.
--                * wh_cron_next    — next fire strictly after a timestamp, evaluated in a named
--                                    timezone (DST-aware; spring-forward gap skipped), 5-year
--                                    unsatisfiable horizon; Vixie DOM/DOW OR semantics.
--                * wh_schedule_next_fire — dispatcher over recurrence_kind (0 OneShot -> NULL,
--                                    1 Interval -> after + interval_ms, 2 Cron -> wh_cron_next).
-- Dependencies: 001-066 (066 created wh_schedules; this only adds functions).

-- ---------------------------------------------------------------------------
-- Field parser: one cron field -> sorted INT[] of matching values.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_cron_field');

CREATE OR REPLACE FUNCTION __SCHEMA__._wh_cron_field(
  p_field TEXT,
  p_min INT,
  p_max INT,
  p_kind TEXT
) RETURNS INT[]
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
  v_field TEXT := upper(btrim(p_field));
  v_term TEXT;
  v_range TEXT;
  v_step INT;
  v_lo INT;
  v_hi INT;
  v_slash INT;
  v_dash INT;
  v_v INT;
  v_values INT[] := ARRAY[]::INT[];
BEGIN
  -- Substitute month / day names with their numeric values before parsing.
  IF p_kind = 'month' THEN
    v_field := replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
      v_field, 'JAN', '1'), 'FEB', '2'), 'MAR', '3'), 'APR', '4'), 'MAY', '5'), 'JUN', '6'),
      'JUL', '7'), 'AUG', '8'), 'SEP', '9'), 'OCT', '10'), 'NOV', '11'), 'DEC', '12');
  ELSIF p_kind = 'dow' THEN
    v_field := replace(replace(replace(replace(replace(replace(replace(
      v_field, 'SUN', '0'), 'MON', '1'), 'TUE', '2'), 'WED', '3'), 'THU', '4'), 'FRI', '5'), 'SAT', '6');
  END IF;

  FOREACH v_term IN ARRAY string_to_array(v_field, ',') LOOP
    v_step := 1;
    v_range := v_term;
    v_slash := position('/' IN v_term);
    IF v_slash > 0 THEN
      v_range := substring(v_term FROM 1 FOR v_slash - 1);
      v_step := (substring(v_term FROM v_slash + 1))::int;
      IF v_step < 1 THEN
        RAISE EXCEPTION 'Invalid step in cron term %', v_term;
      END IF;
    END IF;

    IF v_range = '*' THEN
      v_lo := p_min;
      v_hi := p_max;
    ELSE
      v_dash := position('-' IN v_range);
      IF v_dash > 1 THEN
        v_lo := (substring(v_range FROM 1 FOR v_dash - 1))::int;
        v_hi := (substring(v_range FROM v_dash + 1))::int;
      ELSE
        v_lo := v_range::int;
        v_hi := CASE WHEN v_slash > 0 THEN p_max ELSE v_lo END;
      END IF;
    END IF;

    IF v_lo < p_min OR v_hi > p_max OR v_lo > v_hi THEN
      RAISE EXCEPTION 'Cron term % out of range [%-%]', v_term, p_min, p_max;
    END IF;

    v_v := v_lo;
    WHILE v_v <= v_hi LOOP
      v_values := array_append(v_values, v_v);
      v_v := v_v + v_step;
    END LOOP;
  END LOOP;

  IF array_length(v_values, 1) IS NULL THEN
    RAISE EXCEPTION 'Cron field % matches no values', p_field;
  END IF;
  RETURN v_values;
END;
$$;

-- ---------------------------------------------------------------------------
-- Cron next-fire: next match strictly after p_after, evaluated in p_tz.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_cron_next');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_cron_next(
  p_cron TEXT,
  p_after TIMESTAMPTZ,
  p_tz TEXT DEFAULT 'UTC'
) RETURNS TIMESTAMPTZ
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
  v_fields TEXT[];
  v_minutes INT[];
  v_hours INT[];
  v_doms INT[];
  v_months INT[];
  v_dows INT[];
  v_dom_restricted BOOLEAN;
  v_dow_restricted BOOLEAN;
  v_candidate TIMESTAMP;   -- wall-clock local to p_tz
  v_limit TIMESTAMP;
  v_dow INT;
  v_day_ok BOOLEAN;
  v_result TIMESTAMPTZ;
BEGIN
  v_fields := regexp_split_to_array(btrim(p_cron), '\s+');
  IF array_length(v_fields, 1) IS DISTINCT FROM 5 THEN
    RAISE EXCEPTION 'Cron expression must have 5 fields: %', p_cron;
  END IF;

  v_minutes := __SCHEMA__._wh_cron_field(v_fields[1], 0, 59, 'minute');
  v_hours := __SCHEMA__._wh_cron_field(v_fields[2], 0, 23, 'hour');
  v_doms := __SCHEMA__._wh_cron_field(v_fields[3], 1, 31, 'dom');
  v_months := __SCHEMA__._wh_cron_field(v_fields[4], 1, 12, 'month');
  v_dows := __SCHEMA__._wh_cron_field(v_fields[5], 0, 7, 'dow');

  v_dom_restricted := btrim(v_fields[3]) <> '*';
  v_dow_restricted := btrim(v_fields[5]) <> '*';

  -- Fold Sunday-as-7 onto 0 to match EXTRACT(DOW) (0 = Sunday .. 6 = Saturday).
  v_dows := ARRAY(SELECT DISTINCT CASE WHEN x = 7 THEN 0 ELSE x END FROM unnest(v_dows) AS x);

  -- Step strictly forward from the next whole minute, in wall-clock local time.
  v_candidate := date_trunc('minute', (p_after AT TIME ZONE p_tz)) + INTERVAL '1 minute';
  v_limit := v_candidate + INTERVAL '5 years';

  WHILE v_candidate < v_limit LOOP
    IF NOT (EXTRACT(MONTH FROM v_candidate)::int = ANY(v_months)) THEN
      v_candidate := date_trunc('month', v_candidate) + INTERVAL '1 month';
      CONTINUE;
    END IF;

    v_dow := EXTRACT(DOW FROM v_candidate)::int;
    v_day_ok := CASE
      WHEN v_dom_restricted AND v_dow_restricted
        THEN (EXTRACT(DAY FROM v_candidate)::int = ANY(v_doms)) OR (v_dow = ANY(v_dows))
      WHEN v_dom_restricted THEN (EXTRACT(DAY FROM v_candidate)::int = ANY(v_doms))
      WHEN v_dow_restricted THEN (v_dow = ANY(v_dows))
      ELSE TRUE
    END;
    IF NOT v_day_ok THEN
      v_candidate := date_trunc('day', v_candidate) + INTERVAL '1 day';
      CONTINUE;
    END IF;

    IF NOT (EXTRACT(HOUR FROM v_candidate)::int = ANY(v_hours)) THEN
      v_candidate := date_trunc('hour', v_candidate) + INTERVAL '1 hour';
      CONTINUE;
    END IF;

    IF NOT (EXTRACT(MINUTE FROM v_candidate)::int = ANY(v_minutes)) THEN
      v_candidate := v_candidate + INTERVAL '1 minute';
      CONTINUE;
    END IF;

    -- All fields match this local minute. Realize the instant; a spring-forward gap has no instant
    -- (its local<->instant round-trip diverges) so skip it and keep searching.
    v_result := v_candidate AT TIME ZONE p_tz;
    IF (v_result AT TIME ZONE p_tz) <> v_candidate THEN
      v_candidate := v_candidate + INTERVAL '1 minute';
      CONTINUE;
    END IF;
    RETURN v_result;
  END LOOP;

  RETURN NULL;
END;
$$;

-- ---------------------------------------------------------------------------
-- Dispatcher over recurrence_kind — what the schedule worker calls on advance.
-- ---------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_schedule_next_fire');

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_schedule_next_fire(
  p_kind SMALLINT,
  p_cron TEXT,
  p_interval_ms BIGINT,
  p_tz TEXT,
  p_after TIMESTAMPTZ
) RETURNS TIMESTAMPTZ
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
  RETURN CASE p_kind
    WHEN 0 THEN NULL                                                            -- OneShot: no recurrence
    WHEN 1 THEN p_after + make_interval(secs => p_interval_ms / 1000.0)         -- Interval
    WHEN 2 THEN __SCHEMA__.wh_cron_next(p_cron, p_after, COALESCE(p_tz, 'UTC')) -- Cron
    ELSE NULL
  END;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_schedule_next_fire IS
  'Temporal engine: computes a schedule''s next fire time for the atomic claim+advance. '
  'DB half of the dual C#/DB recurrence engine (mirrors DefaultRecurrenceRuleFactory).';
