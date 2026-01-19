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
DROP FUNCTION IF EXISTS __SCHEMA__.normalize_event_type;

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
