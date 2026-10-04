-- Migration: 006_CreateNormalizeEventTypeFunction.sql
-- Date: 2025-12-23
-- Description: The canonical SQL form of a stored CLR type name (wh_normalize_clr_type_name) and the
--              assembly it names (wh_normalize_assembly_name), used across event store insertions
--              and perspective checkpoint matching. normalize_event_type and normalize_assembly_name
--              are kept as aliases (#1034).
-- Dependencies: None
-- Used By: 029_ProcessWorkBatch.sql

-- ======================================================================================
-- wh_normalize_clr_type_name - the canonical form of a stored CLR type name (#1034)
-- ======================================================================================
-- Contractual surface: a consumer may call it from its own migrations. It strips every
-- ", Version=..., Culture=..., PublicKeyToken=..." decoration, at any depth, and agrees byte for
-- byte with EventTypeMatchingHelper.NormalizeTypeName in C#. For a type whose name carries no type
-- arguments that is also TypeNameFormatter.Format: "Namespace.Type, Assembly". The stability
-- contract is the wh_settings row 'clr_type_name_format_version': if the canonical form ever
-- changes, that number changes with it.
--
-- The first definition truncated at the first ", Version=", which for a generic name is inside the
-- type arguments and left an unbalanced "Ns.Box`1[[Ns.Inner, Inner.Asm". The strip is careful where
-- the truncation was not.
--
-- Examples:
--   wh_normalize_clr_type_name('Ns.Created, Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null')
--     => 'Ns.Created, Contracts'
--   wh_normalize_clr_type_name('Ns.Box`1[[Ns.Inner, Inner, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null')
--     => 'Ns.Box`1[[Ns.Inner, Inner]], Contracts'
-- ======================================================================================
SELECT __SCHEMA__.drop_all_overloads('wh_normalize_clr_type_name');
-- <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SqlNormalizer_AgreesWithTheCSharpHelperAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SqlNormalizer_OfAVersionedName_IsTheFormattedKeyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_normalize_clr_type_name(type_name TEXT)
RETURNS TEXT AS $$
  -- The decoration always starts with Version=, which the C# helper requires too; a name without it
  -- is returned as it is, without running the expression.
  SELECT CASE
    WHEN strpos(type_name, 'Version=') = 0 THEN type_name
    ELSE regexp_replace(type_name, ', *Version=[^],]*(, *(Version|Culture|PublicKeyToken)=[^],]*){0,2}', '', 'g')
  END;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.wh_normalize_clr_type_name IS 'The canonical form of a stored CLR type name: every Version/Culture/PublicKeyToken decoration stripped, at any depth. Agrees byte for byte with EventTypeMatchingHelper.NormalizeTypeName. Contractual surface; versioned by the wh_settings row clr_type_name_format_version.';

-- ======================================================================================
-- wh_normalize_assembly_name - the assembly of a stored CLR type name
-- ======================================================================================
-- "Namespace.Type, Assembly, Version=1.0.0.0, ..." => "Assembly". The assembly is what follows the
-- last ", " outside the type arguments, so a generic name yields its own assembly rather than one of
-- its arguments'. A name with no assembly is returned normalized.
-- ======================================================================================
SELECT __SCHEMA__.drop_all_overloads('wh_normalize_assembly_name');
-- <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SqlAssemblyName_IsTheTypesOwnAssemblyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_normalize_assembly_name(type_name TEXT)
RETURNS TEXT AS $$
  SELECT CASE
    WHEN strpos(n.normalized, ', ') = 0 THEN n.normalized
    WHEN n.tail ~ '[][]' THEN n.normalized
    ELSE btrim(n.tail)
  END
  FROM (
    SELECT v.normalized, regexp_replace(v.normalized, '^.*, ', '') AS tail
    FROM (SELECT __SCHEMA__.wh_normalize_clr_type_name(type_name) AS normalized) v
  ) n;
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.wh_normalize_assembly_name IS 'The assembly name of a stored CLR type name, the one outside any type arguments. Contractual surface.';

-- ======================================================================================
-- normalize_event_type / normalize_assembly_name - the original names, kept as aliases
-- ======================================================================================
-- Every store procedure calls these, and consumers may already. They are aliases of the canonical
-- functions above, so there is one definition of the form in SQL, not two.
-- ======================================================================================
SELECT __SCHEMA__.drop_all_overloads('normalize_event_type');
-- <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SqlNormalizer_AgreesWithTheCSharpHelperAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.normalize_event_type(type_name TEXT)
RETURNS TEXT AS $$
  SELECT __SCHEMA__.wh_normalize_clr_type_name(type_name);
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.normalize_event_type IS 'Alias of wh_normalize_clr_type_name, kept for the store procedures and consumers that call it by this name.';

SELECT __SCHEMA__.drop_all_overloads('normalize_assembly_name');
-- <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/ClrTypeNameNormalizerParityTests.cs:SqlAssemblyName_IsTheTypesOwnAssemblyAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.normalize_assembly_name(type_name TEXT)
RETURNS TEXT AS $$
  SELECT __SCHEMA__.wh_normalize_assembly_name(type_name);
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__.normalize_assembly_name IS 'Alias of wh_normalize_assembly_name, kept for the callers that use this name.';
