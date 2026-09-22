-- Migration: 152_JsonbContainmentSet
-- Date: 2026-09-11
-- Description: A helper for set-membership filters on perspectives. A lens filter of the form
--   "this field is any of these values" has to be compiled into containment to reach the GIN index on the
--   data column, and containment tests a document against a document: one document per candidate value.
--   The values arrive as a single array parameter, so something has to turn that array into an array of
--   single-key documents. Doing it inline needs a scalar subquery over unnest, which the query translator
--   cannot build; doing it in a function is a plain call it can. Declared IMMUTABLE and written in SQL so
--   the planner inlines it: an opaque call on the right of @> would still be correct but would plan as a
--   sequential scan, which is the whole point of the exercise.
-- Dependencies: none
-- Objects: jsonb_containment_set
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ONE overload only: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('jsonb_containment_set');

CREATE OR REPLACE FUNCTION __SCHEMA__.jsonb_containment_set(
  p_key TEXT,
  p_values ANYARRAY
) RETURNS JSONB[] AS $$
  -- One single-key document per value: ARRAY['{"k":"a"}','{"k":"b"}']::jsonb[], which is what
  -- "data @> ANY(...)" consumes. STRICT below means a NULL array short-circuits to NULL rather than
  -- reaching this body.
  SELECT array_agg(jsonb_build_object(p_key, v)) FROM unnest(p_values) AS v;
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.jsonb_containment_set(TEXT, ANYARRAY) IS
  'Builds one single-key jsonb document per value, for "data @> ANY(...)" set-membership filters. '
  'IMMUTABLE and SQL-bodied so the planner inlines it and the GIN index on the data column is still matched. '
  'Returns NULL for a NULL array and for an empty one, both of which make the containment test match no rows, '
  'which is what an empty candidate set means.';
