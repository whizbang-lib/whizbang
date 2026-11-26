-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 544_DictionaryMembersStoredAsPairs.sql
-- Date: 2026-11-20
-- Description: The stored-form rewrite that brings a dictionary member of a perspective document from a JSON object
--              to an array of key/value objects (#1911).
--
--              A dictionary in a perspective document is now stored as [{"Key":..,"Value":..}], in the dictionary's
--              order, by both writers of the document: Entity Framework's JSON mapping, which refuses a dictionary as
--              an object, and the serializer the atomic upsert writes with. A document written before stores each
--              dictionary as an object: one stored as a single value, or a dictionary promoted to a jsonb column. The
--              readers take either form, and the stored-form phase rewrites the object form once per table, journaled
--              in wh_stored_form_migrations under 'dictionary-pairs:<table>:<shape>' and settled by the first pass
--              that converts nothing.
--
--              _wh_dictionary_pairs converts one object. A key's stored JSON depends on its type, which the database
--              cannot know, so the caller says: 1 a string (a string, an identifier, a character: the property name
--              as it is), 2 a number (the property name read as one), 3 a map from each name to its stored JSON (an
--              enumeration's names). A name the caller's form cannot read leaves the whole dictionary as it was, for
--              the reader, which takes the object form and counts it. wh_dictionary_pairs_at walks one path to it,
--              through nested objects and, at a segment written '[]', every element of a collection.
--
--              In the bootstrap region because the stored-form phase runs ahead of the migration pass.
--
-- Reversal: reversible (the functions are dropped; rows already converted stay converted, and every reader reads them)
-- Dependencies: 176 (wh_stored_form_migrations, the journal the rewrite records in)
-- Objects: _wh_dictionary_pairs, wh_dictionary_pairs_at
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- @whizbang:bootstrap-begin
-- Bootstrap: the stored-form rewrite runs before the migration pass and calls these.

-- <docs>fundamentals/perspectives/dictionary-members#stored-form-rewrite</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryPairsRewriteIntegrationTests.cs:AnObjectFormDictionary_IsRewrittenToPairs_AndTheTableSettlesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryPairsRewriteIntegrationTests.cs:EachKeyKind_IsStoredAsItsTypeWritesItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_dictionary_pairs(
  p_value JSONB,
  p_key_kind SMALLINT,
  p_key_map JSONB
) RETURNS JSONB
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
  v_out JSONB := '[]'::JSONB;
  v_key JSONB;
  v_entry RECORD;
BEGIN
  IF p_value IS NULL OR jsonb_typeof(p_value) <> 'object' THEN
    RETURN p_value;
  END IF;

  FOR v_entry IN SELECT e.key, e.value FROM jsonb_each(p_value) AS e LOOP
    v_key := CASE p_key_kind
      WHEN 1 THEN to_jsonb(v_entry.key)
      WHEN 2 THEN to_jsonb(v_entry.key::NUMERIC)
      ELSE p_key_map -> v_entry.key
    END;
    IF v_key IS NULL THEN
      RETURN p_value;
    END IF;
    v_out := v_out || jsonb_build_array(jsonb_build_object('Key', v_key, 'Value', v_entry.value));
  END LOOP;

  RETURN v_out;
EXCEPTION WHEN invalid_text_representation THEN
  -- A name that is not a number under a numeric key: left whole for the reader, which refuses it naming the key.
  RETURN p_value;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_dictionary_pairs(JSONB, SMALLINT, JSONB) IS
  'One stored dictionary as an array of key/value objects: an object becomes [{"Key":..,"Value":..}] with each key '
  'as its type stores it (key_kind 1 the name as a string, 2 the name as a number, 3 the name looked up in key_map). '
  'Anything but an object, or an object holding a name the key kind cannot read, is returned unchanged.';

-- <docs>fundamentals/perspectives/dictionary-members#stored-form-rewrite</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryPairsRewriteIntegrationTests.cs:AnObjectFormDictionary_IsRewrittenToPairs_AndTheTableSettlesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryPairsRewriteIntegrationTests.cs:ADictionaryInsideACollectionAndInsideADictionary_IsRewrittenAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_dictionary_pairs_at(
  p_doc JSONB,
  p_path TEXT[],
  p_key_kind SMALLINT,
  p_key_map JSONB
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
    RETURN __SCHEMA__._wh_dictionary_pairs(p_doc, p_key_kind, p_key_map);
  END IF;

  v_head := p_path[1];
  v_rest := p_path[2:];

  IF v_head = '[]' THEN
    IF jsonb_typeof(p_doc) <> 'array' THEN
      RETURN p_doc;
    END IF;
    SELECT coalesce(
             jsonb_agg(__SCHEMA__.wh_dictionary_pairs_at(e.value, v_rest, p_key_kind, p_key_map) ORDER BY e.ordinality),
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
    __SCHEMA__.wh_dictionary_pairs_at(p_doc -> v_head, v_rest, p_key_kind, p_key_map),
    false);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_dictionary_pairs_at(JSONB, TEXT[], SMALLINT, JSONB) IS
  'Rewrites the dictionary at one path of a perspective document from a JSON object to an array of key/value objects. '
  'The path walks nested objects and, at a segment written [], every element of a collection; a missing key or a '
  'value already in pairs is returned as it was. Idempotent over its own output.';

-- @whizbang:bootstrap-end
