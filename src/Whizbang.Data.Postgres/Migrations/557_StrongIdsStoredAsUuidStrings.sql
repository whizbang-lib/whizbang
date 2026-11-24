-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 557_StrongIdsStoredAsUuidStrings.sql
-- Date: 2026-11-22
-- Description: The stored-form rewrite that brings a strongly-typed id inside a perspective document from the object
--              {"Value": "<uuid>"} to the UUID string itself (#2518).
--
--              A [WhizbangId] in a perspective document is now stored as a UUID string, the same as a Guid and as the
--              transport writes it, by both writers of the document: Entity Framework, whose type mapping makes the id
--              a uuid scalar, and the serializer the atomic upsert writes with. A document written before stores each
--              id as an object. The readers take either form, but a query that compares an id casts the stored text to
--              uuid unless it is told to take either form; so the object form is rewritten, per table, journaled in
--              wh_stored_form_migrations under 'strong-id-scalar:<table>:<shape>' and settled by the first complete
--              pass that converts nothing.
--
--              The rewrite runs a batch at a time, each batch its own transaction under the schema lock, and records
--              in the journal the last primary key it reached (resume_key) and what the current pass has converted
--              (pass_rows_converted). Startup runs one budgeted slice and the maintenance cycle carries on from the
--              saved key, so a table of any size finishes; a restart redoes nothing.
--
--              _wh_strong_id_scalar converts one value: an object whose Value is a string becomes that string; any
--              other value is returned as it was. wh_strong_id_scalar_at walks one path to it, through nested objects
--              and, at a segment written '[]', every element of a collection.
--
--              In the bootstrap region because the stored-form phase runs ahead of the migration pass.
--
--              jsonb_strong_id_containment_set is jsonb_containment_set (152) for an id still in the object form:
--              one document {"<key>": {"Value": <id>}} per value, so "data @> ANY(...)" over a set of ids also
--              matches a row the rewrite has not reached or an earlier release wrote after it settled. Queries call
--              it, so it lives after the bootstrap region.
--
-- Reversal: reversible (the functions are dropped; rows already converted stay converted, and every reader since #2518 reads them)
-- Dependencies: 176 (wh_stored_form_migrations, the journal the rewrite records in)
-- Objects: _wh_strong_id_scalar, wh_strong_id_scalar_at, jsonb_strong_id_containment_set,
--          wh_stored_form_migrations.resume_key, wh_stored_form_migrations.pass_rows_converted
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the stored-form rewrite runs before the migration pass and calls these.

-- <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_strong_id_scalar(
  p_value JSONB
) RETURNS JSONB
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN jsonb_typeof(p_value) = 'object' AND jsonb_typeof(p_value -> 'Value') = 'string' THEN p_value -> 'Value'
    ELSE p_value
  END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_strong_id_scalar(JSONB) IS
  'One stored strongly-typed id as a UUID string: an object whose Value is a string becomes that string; anything '
  'else is returned unchanged.';

-- <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_strong_id_scalar_at(
  p_doc JSONB,
  p_path TEXT[]
) RETURNS JSONB
LANGUAGE plpgsql
IMMUTABLE
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
    RETURN __SCHEMA__._wh_strong_id_scalar(p_doc);
  END IF;

  v_head := p_path[1];
  v_rest := p_path[2:];

  IF v_head = '[]' THEN
    IF jsonb_typeof(p_doc) <> 'array' THEN
      RETURN p_doc;
    END IF;
    SELECT coalesce(
             jsonb_agg(__SCHEMA__.wh_strong_id_scalar_at(e.value, v_rest) ORDER BY e.ordinality),
             '[]'::JSONB)
      INTO v_out
      FROM jsonb_array_elements(p_doc) WITH ORDINALITY AS e(value, ordinality);
    RETURN v_out;
  END IF;

  IF jsonb_typeof(p_doc) <> 'object' OR NOT (p_doc ? v_head) THEN
    RETURN p_doc;
  END IF;

  RETURN jsonb_set(p_doc, ARRAY[v_head], __SCHEMA__.wh_strong_id_scalar_at(p_doc -> v_head, v_rest), false);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_strong_id_scalar_at(JSONB, TEXT[]) IS
  'Rewrites the strongly-typed id at one path of a perspective document from {"Value": "<uuid>"} to the UUID string. '
  'The path walks nested objects and, at a segment written [], every element of a collection; a missing key or a '
  'value already a string is returned as it was. Idempotent over its own output.';

-- Where a batched rewrite stands: the primary key its last batch reached (as text; null at the start of a pass) and what
-- the current pass has converted, so a run resumes there and a table settles on a complete pass that converts nothing.
ALTER TABLE __SCHEMA__.wh_stored_form_migrations ADD COLUMN IF NOT EXISTS resume_key TEXT NULL;
ALTER TABLE __SCHEMA__.wh_stored_form_migrations ADD COLUMN IF NOT EXISTS pass_rows_converted BIGINT NOT NULL DEFAULT 0;

COMMENT ON COLUMN __SCHEMA__.wh_stored_form_migrations.resume_key IS
  'For a migration run a batch at a time (#2518): the primary key, as text, the last committed batch reached. Null '
  'at the start of a pass. Each run starts from it, so a restart redoes nothing.';
COMMENT ON COLUMN __SCHEMA__.wh_stored_form_migrations.pass_rows_converted IS
  'For a migration run a batch at a time (#2518): the rows the current pass has converted. A pass that reaches the '
  'end with none settles the migration; one with any starts another, which catches rows written meanwhile.';

-- @whizbang:bootstrap-end

-- ONE overload only: the sweep force-replays by name and a second signature would be left behind.
SELECT __SCHEMA__.drop_all_overloads('jsonb_strong_id_containment_set');

-- <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/GraphQLWhizbangIdFilterIndexTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.jsonb_strong_id_containment_set(
  p_key TEXT,
  p_values ANYARRAY
) RETURNS JSONB[] AS $$
  SELECT array_agg(jsonb_build_object(p_key, jsonb_build_object('Value', v))) FROM unnest(p_values) AS v;
$$ LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.jsonb_strong_id_containment_set(TEXT, ANYARRAY) IS
  'Builds one document {"<key>": {"Value": <id>}} per id, for "data @> ANY(...)" over a set of strongly-typed ids '
  'still stored in the object form an earlier release wrote (#2518). IMMUTABLE and SQL-bodied so the planner inlines '
  'it and the GIN index on the data column is still matched.';
