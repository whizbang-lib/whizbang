-- Migration: 006_CreateNormalizeEventTypeFunction.sql
-- Date: 2025-12-23
-- Description: Creates normalize_event_type utility function for defensive type name normalization
--              across event store insertions and perspective checkpoint matching.
--              Extracts "TypeName, AssemblyName" format from various .NET type name formats.
-- Dependencies: None
-- Used By: 029_ProcessWorkBatch.sql

-- ======================================================================================
-- normalize_event_type Function - Defensive Type Name Normalization
-- ======================================================================================
-- Extracts "TypeName, AssemblyName" format from various .NET type name formats.
-- Matches TypeNameFormatter.Parse() logic in C# for consistency.
--
-- Supports:
-- - Short form: "TypeName, AssemblyName" (returned as-is)
-- - Long form: "TypeName, AssemblyName, Version=X, Culture=Y, PublicKeyToken=Z" (extracts first two parts)
--
-- Returns: "TypeName, AssemblyName" format
--
-- Examples:
--   normalize_event_type('ECommerce.Events.ProductCreated, ECommerce.Contracts')
--     => 'ECommerce.Events.ProductCreated, ECommerce.Contracts'
--
--   normalize_event_type('ECommerce.Events.ProductCreated, ECommerce.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null')
--     => 'ECommerce.Events.ProductCreated, ECommerce.Contracts'
-- ======================================================================================
DO $$
DECLARE _oid oid;
BEGIN
  FOR _oid IN
    SELECT p.oid FROM pg_proc p
    JOIN pg_namespace n ON p.pronamespace = n.oid
    WHERE p.proname = 'normalize_event_type' AND n.nspname = current_schema()
  LOOP
    EXECUTE format('DROP FUNCTION IF EXISTS %s', _oid::regprocedure);
  END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION __SCHEMA__.normalize_event_type(type_name TEXT)
RETURNS TEXT AS $$
BEGIN
  -- Defensive normalization: Extract "TypeName, AssemblyName" from any format
  -- Truncates at ", Version=" or ", Culture=" or ", PublicKeyToken=" if present
  -- Returns as-is if already in short/medium form
  RETURN CASE
    WHEN POSITION(', Version=' IN type_name) > 0 THEN
      SUBSTRING(type_name FROM 1 FOR POSITION(', Version=' IN type_name) - 1)
    WHEN POSITION(', Culture=' IN type_name) > 0 THEN
      SUBSTRING(type_name FROM 1 FOR POSITION(', Culture=' IN type_name) - 1)
    WHEN POSITION(', PublicKeyToken=' IN type_name) > 0 THEN
      SUBSTRING(type_name FROM 1 FOR POSITION(', PublicKeyToken=' IN type_name) - 1)
    ELSE type_name
  END;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__.normalize_event_type IS 'Defensively normalizes .NET type names to "TypeName, AssemblyName" format by truncating Version/Culture/PublicKeyToken metadata. Matches TypeNameFormatter.Parse() in C#. Used in event store insertions and perspective checkpoint matching for consistent type identification.';

-- ======================================================================================
-- normalize_assembly_name Function - Extract Assembly Name from Type String
-- ======================================================================================
-- Extracts just the assembly name portion from a .NET type string.
-- "Namespace.TypeName, MyAssembly" => "MyAssembly"
-- "Namespace.TypeName, MyAssembly, Version=1.0.0.0, ..." => "MyAssembly"
-- Used for fuzzy type matching in Phase 4.6/4.7 to compare types across assemblies.
-- ======================================================================================
DO $$
DECLARE _oid oid;
BEGIN
  FOR _oid IN
    SELECT p.oid FROM pg_proc p
    JOIN pg_namespace n ON p.pronamespace = n.oid
    WHERE p.proname = 'normalize_assembly_name' AND n.nspname = current_schema()
  LOOP
    EXECUTE format('DROP FUNCTION IF EXISTS %s', _oid::regprocedure);
  END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION __SCHEMA__.normalize_assembly_name(type_name TEXT)
RETURNS TEXT AS $$
DECLARE
  v_normalized TEXT;
  v_comma_pos INTEGER;
BEGIN
  -- First normalize to "TypeName, AssemblyName" form
  v_normalized := __SCHEMA__.normalize_event_type(type_name);
  -- Find the comma separator
  v_comma_pos := POSITION(', ' IN v_normalized);
  IF v_comma_pos > 0 THEN
    RETURN TRIM(SUBSTRING(v_normalized FROM v_comma_pos + 2));
  ELSE
    RETURN v_normalized;
  END IF;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__.normalize_assembly_name IS 'Extracts the assembly name from a .NET type string. Used for fuzzy type matching across different assembly versions.';
