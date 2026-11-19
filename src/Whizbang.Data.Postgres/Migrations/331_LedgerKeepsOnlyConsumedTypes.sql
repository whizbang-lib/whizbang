-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 331_LedgerKeepsOnlyConsumedTypes.sql
-- Date: 2026-11-04
-- Description: The stream-integrity repair ledger keeps rows only for the event types the service consumes (#2058).
--
--              Integrity used to audit every event type the application knew, not the ones it consumes. A type
--              nothing here consumes is never stored here, so every bucket of it reads as a deficit, and its
--              redelivery is discarded at the receive boundary: the row can never heal. Such rows held the
--              unhealed-buckets gauge up and kept the repair budget cycling forever.
--
--              wh_integrity_forget_unconsumed (new): deletes the ledger rows whose event type is neither in the
--              consumed set nor in a namespace the service absorbs (absorbed events are stored on purpose, so their
--              rows can heal). The integrity audit calls it each cycle with the consumed set, so the ledger heals
--              itself: rows recorded before the audit was narrowed, or before a consumer was removed, go on the next
--              cycle. No data is changed by this file.
--
--              Number 331: 330 is the highest on develop.
--
-- Dependencies: 090 (wh_integrity_ledger)

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_forget_unconsumed');

-- Deletes the ledger rows of event types the service does not keep: not in p_consumed_event_types (the
-- assembly-qualified wire form) and not in a namespace of p_absorbed_namespaces (lowercase). The namespace of a row's
-- type is the part of its name before the last '.', the form the receive boundary derives, so a nested type
-- ("Ns.Outer+Inner") belongs to "ns". A null consumed set forgets nothing: only an explicit set may empty the ledger.
-- Returns the rows deleted. The ledger is bounded (090 reclaims past a ceiling), so the scan is small and runs once
-- per audit cycle.
-- <docs>resilience/stream-integrity#the-integrity-surface-is-what-the-service-consumes</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_RemovesEveryRowOfAnUnconsumedType_AndKeepsTheConsumedOnesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_AnEmptyConsumedSet_RemovesEveryRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_KeepsTheRowsOfATypeInAnAbsorbedNamespaceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetUnconsumedSqlTests.cs:Forget_ANullConsumedSet_ForgetsNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed(
  p_consumed_event_types TEXT[],
  p_absorbed_namespaces  TEXT[]
) RETURNS INTEGER
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_deleted INTEGER;
BEGIN
  IF p_consumed_event_types IS NULL THEN
    RETURN 0;
  END IF;
  DELETE FROM __SCHEMA__.wh_integrity_ledger l
  WHERE l.event_type <> ALL (p_consumed_event_types)
    AND lower(COALESCE(substring(split_part(l.event_type, ', ', 1) FROM '^(.*)[.][^.]*$'), ''))
        <> ALL (COALESCE(p_absorbed_namespaces, ARRAY[]::TEXT[]));
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  RETURN v_deleted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_unconsumed(TEXT[], TEXT[]) IS
'331 (#2058): deletes the integrity ledger rows of event types the service neither consumes nor absorbs. Such a type is never stored here, so its buckets are deficits whose redelivery the receive boundary discards: they can never heal. Called by the integrity audit each cycle with the consumed set; a null set forgets nothing. Returns the rows deleted.';
