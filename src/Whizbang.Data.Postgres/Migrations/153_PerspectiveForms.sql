-- Migration: 153_PerspectiveForms.sql
-- Date: 2026-09-15
-- Description: The stored form of a perspective's dates, times and durations, and the ledger that
--              records which form each table is in.
--
--              Every kind now stores as one unit, microseconds: an instant since the Unix epoch, a
--              date the same at its midnight UTC, a time of day since midnight, a duration plain.
--              The first release of the canonical form stored a date as a day count and a duration
--              as a tick count, and a day count and a microsecond count are both integers: nothing
--              in a document says which unit a number is in. So the ledger says. A table absent
--              from it is in the mixed-unit form (1); the rewrite converts it and records the
--              microsecond form (2) in the same transaction, and a table created by this release
--              starts at 2. settled_at is set once a pass at form 2 finds no rendering left, so a
--              later startup skips the table without scanning it.
--
--              wh_canonicalize_temporal rewrites one path of one document: a rendering becomes the
--              canonical number, a number in the mixed-unit form converts its unit, anything else is
--              left exactly as it was, because a value the reader cannot parse is a thing to look
--              at rather than a thing to write over. The path walks nested objects and, at a
--              segment written '[]', every element of a collection, which is how a temporal inside
--              a collection element is reached at all. Kinds are the ordinals of the runtime's
--              StoredTemporalKind: 0 instant, 1 offset instant, 2 day, 3 time of day, 4 duration.
--
--              In the bootstrap region because the rewrite runs ahead of the migration pass, on the
--              instance elected to migrate, and needs both the ledger and the function to exist.
-- Dependencies: none

-- @whizbang:bootstrap-begin
-- Bootstrap: the stored-form rewrite runs before the migration pass and reads the ledger and calls
-- the function, so both have to exist by then.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_perspective_forms (
  table_name TEXT PRIMARY KEY,
  temporal_form SMALLINT NOT NULL,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  settled_at TIMESTAMPTZ NULL
);

COMMENT ON TABLE __SCHEMA__.wh_perspective_forms IS
  'Which stored form each perspective table is in. temporal_form 1 = the mixed-unit form (a date as '
  'a day count, a duration as ticks); 2 = microseconds for every kind. A table absent here is at 1. '
  'settled_at is set once a pass at form 2 converts nothing, so later startups skip the table.';

-- The canonical number for one stored value: a rendering parsed, a mixed-unit number converted,
-- anything else returned as it was. Renderings without a zone are UTC, whatever the session says.
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_canonical_leaf(
  p_value JSONB,
  p_kind SMALLINT,
  p_from_form SMALLINT
) RETURNS JSONB
LANGUAGE plpgsql
STABLE
SET timezone = 'UTC'
AS $$
DECLARE
  v_text TEXT;
  v_number BIGINT;
  v_parts TEXT[];
BEGIN
  IF jsonb_typeof(p_value) = 'number' THEN
    IF p_from_form >= 2 THEN
      RETURN p_value;
    END IF;
    v_number := (p_value #>> '{}')::BIGINT;
    IF p_kind = 2 THEN
      -- A day count becomes the instant at that day's midnight.
      RETURN to_jsonb(v_number * 86400000000);
    END IF;
    IF p_kind = 4 THEN
      -- A tick count divides down to microseconds, truncating toward zero as the writer does.
      RETURN to_jsonb(v_number / 10);
    END IF;
    RETURN p_value;
  END IF;

  IF jsonb_typeof(p_value) <> 'string' THEN
    RETURN p_value;
  END IF;
  v_text := p_value #>> '{}';

  BEGIN
    IF p_kind IN (0, 1) THEN
      IF v_text = 'infinity' THEN
        RETURN to_jsonb(253402300799999999::BIGINT);
      END IF;
      IF v_text = '-infinity' THEN
        RETURN to_jsonb((-62135596800000000)::BIGINT);
      END IF;
      RETURN to_jsonb((EXTRACT(EPOCH FROM v_text::TIMESTAMPTZ) * 1000000)::BIGINT);
    ELSIF p_kind = 2 THEN
      RETURN to_jsonb((v_text::DATE - DATE '1970-01-01')::BIGINT * 86400000000);
    ELSIF p_kind = 3 THEN
      -- A time was rendered with seven fractional digits and the cast to a time rounds the seventh;
      -- the writer truncates it, so the extra digits are dropped from the text first.
      RETURN to_jsonb((EXTRACT(EPOCH FROM regexp_replace(v_text, '(\.\d{6})\d+$', '\1')::TIME) * 1000000)::BIGINT);
    ELSIF p_kind = 4 THEN
      -- A duration was rendered as an optional day count, the clock, and an optional fraction,
      -- with a sign that applies to the whole value. The ticks are computed exactly and divided
      -- down to microseconds, truncating toward zero as the writer does.
      v_parts := regexp_match(v_text, '^-?(?:(\d+)\.)?(\d\d):(\d\d):(\d\d)(?:\.(\d+))?$');
      IF v_parts IS NULL THEN
        RETURN p_value;
      END IF;
      RETURN to_jsonb(
        ((CASE WHEN left(v_text, 1) = '-' THEN -1 ELSE 1 END)::BIGINT * (
          coalesce(v_parts[1], '0')::BIGINT * 864000000000
          + v_parts[2]::BIGINT * 36000000000
          + v_parts[3]::BIGINT * 600000000
          + v_parts[4]::BIGINT * 10000000
          + coalesce(rpad(v_parts[5], 7, '0'), '0')::BIGINT
        )) / 10);
    END IF;
    RETURN p_value;
  EXCEPTION WHEN OTHERS THEN
    -- An unrecognized rendering is left for the reader to refuse, loudly, with the row intact.
    RETURN p_value;
  END;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_canonical_leaf(JSONB, SMALLINT, SMALLINT) IS
  'The canonical number for one stored temporal value of the given kind (0 instant, 1 offset '
  'instant, 2 day, 3 time of day, 4 duration): a rendering parsed as UTC, a number in the mixed-unit '
  'form (from_form 1) converted to microseconds, anything else returned unchanged.';

-- One path of one document, walking nested objects and, at a segment written '[]', every element
-- of a collection. A missing key, a value of another type or a collection that is not one is
-- returned as it was.
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_canonicalize_temporal(
  p_doc JSONB,
  p_path TEXT[],
  p_kind SMALLINT,
  p_from_form SMALLINT
) RETURNS JSONB
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
  v_head TEXT;
  v_rest TEXT[];
  v_out JSONB;
BEGIN
  IF p_doc IS NULL THEN
    RETURN NULL;
  END IF;

  IF coalesce(array_length(p_path, 1), 0) = 0 THEN
    RETURN __SCHEMA__._wh_canonical_leaf(p_doc, p_kind, p_from_form);
  END IF;

  v_head := p_path[1];
  v_rest := p_path[2:];

  IF v_head = '[]' THEN
    IF jsonb_typeof(p_doc) <> 'array' THEN
      RETURN p_doc;
    END IF;
    SELECT coalesce(
             jsonb_agg(__SCHEMA__.wh_canonicalize_temporal(e.value, v_rest, p_kind, p_from_form) ORDER BY e.ordinality),
             '[]'::JSONB)
      INTO v_out
      FROM jsonb_array_elements(p_doc) WITH ORDINALITY AS e(value, ordinality);
    RETURN v_out;
  END IF;

  IF jsonb_typeof(p_doc) <> 'object' OR NOT (p_doc ? v_head) THEN
    RETURN p_doc;
  END IF;

  RETURN jsonb_set(
    p_doc,
    ARRAY[v_head],
    __SCHEMA__.wh_canonicalize_temporal(p_doc -> v_head, v_rest, p_kind, p_from_form),
    false);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_canonicalize_temporal(JSONB, TEXT[], SMALLINT, SMALLINT) IS
  'Rewrites one temporal path of a perspective document into the canonical microsecond form. The '
  'path walks nested objects and, at a segment written [], every element of a collection; the kind is '
  'the ordinal of the runtime StoredTemporalKind; from_form is the table''s ledger form (1 converts '
  'day counts and tick counts, 2 leaves numbers alone). Idempotent over its own output.';

-- @whizbang:bootstrap-end
