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
--              uuid, which the object form fails; so the stored-form phase rewrites the object form once per table,
--              before any query runs, journaled in wh_stored_form_migrations under 'strong-id-scalar:<table>:<shape>'
--              and settled by the first pass that converts nothing.
--
--              _wh_strong_id_scalar converts one value: an object whose Value is a string becomes that string; any
--              other value is returned as it was. wh_strong_id_scalar_at walks one path to it, through nested objects
--              and, at a segment written '[]', every element of a collection.
--
--              In the bootstrap region because the stored-form phase runs ahead of the migration pass.
--
-- Reversal: reversible (the functions are dropped; rows already converted stay converted, and every reader since #2518 reads them)
-- Dependencies: 176 (wh_stored_form_migrations, the journal the rewrite records in)
-- Objects: _wh_strong_id_scalar, wh_strong_id_scalar_at
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

-- @whizbang:bootstrap-end
