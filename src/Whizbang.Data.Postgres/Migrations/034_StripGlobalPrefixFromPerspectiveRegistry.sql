-- Migration 034: Strip global:: prefix from perspective registry CLR type names
--
-- The EFCoreServiceRegistrationGenerator previously wrote global::-prefixed type names
-- to wh_perspective_registry.clr_type_name via reconcile_perspective_registry().
-- This caused format mismatches with runtime code that uses "FullName, AssemblyName" format.
-- The generator has been fixed; this migration cleans up existing data.

UPDATE __SCHEMA__.wh_perspective_registry
SET clr_type_name = REPLACE(clr_type_name, 'global::', '')
WHERE clr_type_name LIKE 'global::%';
