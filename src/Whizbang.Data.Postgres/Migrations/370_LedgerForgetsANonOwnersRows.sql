-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 370_LedgerForgetsANonOwnersRows.sql
-- Date: 2026-11-08
-- Description: The stream-integrity repair ledger forgets rows recorded under an origin that does not own the row's
--              event type.
--
--              Before comparisons ignored a non-owner's digests (#2032), a service that held events it had only
--              received still reported them as its own, so consumers recorded deficit rows under that service for
--              another service's types (and for their own types). Since #2008 an origin's answer leaves out every
--              type it does not own, so those rows are never compared again: a re-check or a repair answer from the
--              non-owner carries nothing for them, a comparison only touches the buckets an answer names, and the
--              owner's answers land under the owner's id. The rows can neither heal nor clear; with automatic repair
--              on they are marked unhealable and hold the unhealed-buckets gauge up for good, even though the
--              consumer holds exactly the events the owner reports.
--
--              Ownership is decided in the application (IntegrityDomainOwnership.IsNotAuthoritative: the domain claims
--              other services announce, and this service's own owned domains), so the SQL does not restate the
--              hierarchical domain rule. The integrity audit reads the distinct (origin, type) pairs the ledger holds,
--              judges each, and hands the judged pairs back to be forgotten, once per audit cycle. The rows already
--              on a ledger go on the first cycle after upgrade, so no one-time data step is needed. No data is changed
--              by this file.
--
--              1. wh_integrity_ledger_origin_types: the distinct (origin, type) pairs the ledger holds.
--              2. wh_integrity_forget_origin_types: deletes every row of the given pairs; returns the rows deleted.
--
--              No index: the ledger is bounded (090 reclaims past a ceiling) and both run once per audit cycle, the
--              same cost as wh_integrity_forget_unconsumed (331).
--
--              Number 370: 358 is the highest on develop and 360 to 369 are taken by changes in flight.
--
-- Dependencies: 090 (wh_integrity_ledger)
-- Objects: wh_integrity_ledger_origin_types, wh_integrity_forget_origin_types

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_origin_types');

-- The distinct (origin, type) pairs the ledger holds, for the audit to judge each once.
-- <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_RemovesEveryRowOfAJudgedPair_AndKeepsTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_origin_types()
RETURNS TABLE (origin_service_id UUID, event_type TEXT)
LANGUAGE sql
STABLE
AS $$
  SELECT DISTINCT l.origin_service_id, l.event_type
  FROM __SCHEMA__.wh_integrity_ledger l
  ORDER BY 1, 2;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_ledger_origin_types() IS
'370: the distinct (origin, event type) pairs the integrity ledger holds. The integrity audit judges each by ownership and forgets the pairs whose origin does not own the type (wh_integrity_forget_origin_types).';

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_forget_origin_types');

-- Deletes every ledger row of the given (origin, type) pairs, in every tenant and stream. Pairs are positional across
-- the two arrays. A null or empty list forgets nothing. Returns the rows deleted.
-- <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_RemovesEveryRowOfAJudgedPair_AndKeepsTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_AnEmptyPairList_RemovesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_forget_origin_types(
  p_origin_ids  UUID[],
  p_event_types TEXT[]
) RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_deleted INTEGER;
BEGIN
  IF p_origin_ids IS NULL OR p_event_types IS NULL OR cardinality(p_origin_ids) = 0 THEN
    RETURN 0;
  END IF;
  DELETE FROM __SCHEMA__.wh_integrity_ledger l
  USING unnest(p_origin_ids, p_event_types) AS p(origin_service_id, event_type)
  WHERE l.origin_service_id = p.origin_service_id
    AND l.event_type = p.event_type;
  GET DIAGNOSTICS v_deleted = ROW_COUNT;
  RETURN v_deleted;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_forget_origin_types(UUID[], TEXT[]) IS
'370: deletes every integrity ledger row of the given (origin, event type) pairs, which the integrity audit judged to be recorded under an origin that does not own the type: such a row is never compared again, so it can neither heal nor clear. A null or empty list forgets nothing. Returns the rows deleted.';
