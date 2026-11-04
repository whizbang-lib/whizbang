-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 325_LocalLaneFollowsOwnership.sql
-- Date: 2026-11-03
-- Description: Stream integrity's local lane follows registered ownership, and owners are exchanged on the
--              checkpoint (#2008, #2032).
--
--              The local lane was every event with no origin recorded. A store that received other services' events
--              before origins were recorded keeps them there, so it advertised them as its own, counted them as surplus
--              and redelivered them under its own identity, and every receiver refused them. Each service now announces
--              its owned domains on its checkpoint (IntegrityCheckpoint.OwnedDomains), and an event whose type is not in
--              an owned namespace is never local. Consumers keep the announced claims here.
--
--              1. wh_integrity_domain_claims: one row per (domain, owner). Domains key in lower case. An owner's
--                 announcement replaces its claim set; two owners on one domain are both kept, and the map built from
--                 them uses neither.
--              2. wh_integrity_record_domain_claims: replaces an owner's claim set from one announcement and returns
--                 the claims other owners hold on the announced domains (the conflicts). Rows that did not change are
--                 not rewritten, so a checkpoint every minute per origin costs no dead tuples.
--
--              Number 325: 321 to 324 are taken by changes in flight.
--
-- Dependencies: none
-- Objects: wh_integrity_domain_claims, idx_integrity_domain_claims_owner, wh_integrity_record_domain_claims
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_domain_claims (
  domain             TEXT        NOT NULL,   -- the claimed domain, lower case, without a trailing dot
  owner_service_id   UUID        NOT NULL,   -- the claiming service's stable id
  owner_service_name TEXT        NOT NULL,   -- its logical name, the directed-message target
  request_topic      TEXT,                   -- where it consumes directed integrity requests, when announced
  claimed_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (domain, owner_service_id)
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_domain_claims IS
  '325 (#2008): the owned domains other services announce on their integrity checkpoints. Used to re-attribute '
  'pre-origin history to its author, to direct repair and backfill at the owner, and to ignore an origin''s counts for '
  'types another service owns. A domain with two owners is used for neither.';

-- <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_AnAnnouncementReplacesTheOwnersClaimSetAsync</tests>
CREATE INDEX IF NOT EXISTS idx_integrity_domain_claims_owner
  ON __SCHEMA__.wh_integrity_domain_claims (owner_service_id);

COMMENT ON INDEX __SCHEMA__.idx_integrity_domain_claims_owner IS
  '325 (#2008): an announcement replaces one owner''s claim set; this finds that owner''s rows.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Replaces an owner's claim set from one announcement and returns the other owners' claims on the announced domains.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_record_domain_claims');
-- <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_StoresTheOwnersClaims_KeyedWithoutCaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_AnAnnouncementReplacesTheOwnersClaimSetAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_AnEmptyAnnouncement_RemovesEveryClaimOfThatOwnerOnlyAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDomainClaimsSqlTests.cs:Record_ADomainAnotherOwnerClaims_ComesBackAsAConflictAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_record_domain_claims(
  p_owner_service_id UUID,
  p_owner_service_name TEXT,
  p_request_topic TEXT,
  p_domains TEXT[]
) RETURNS TABLE(domain TEXT, owner_service_id UUID, owner_service_name TEXT, request_topic TEXT) AS $$
#variable_conflict use_column
DECLARE
  v_domains TEXT[];
BEGIN
  SELECT COALESCE(array_agg(DISTINCT d.normalized), ARRAY[]::TEXT[]) INTO v_domains
  FROM (
    SELECT rtrim(lower(btrim(u.raw)), '.') AS normalized
    FROM unnest(COALESCE(p_domains, ARRAY[]::TEXT[])) AS u(raw)
  ) d
  WHERE d.normalized IS NOT NULL AND d.normalized <> '';

  DELETE FROM __SCHEMA__.wh_integrity_domain_claims c
  WHERE c.owner_service_id = p_owner_service_id
    AND c.domain <> ALL(v_domains);

  INSERT INTO __SCHEMA__.wh_integrity_domain_claims AS c
    (domain, owner_service_id, owner_service_name, request_topic, claimed_at)
  SELECT d, p_owner_service_id, p_owner_service_name, p_request_topic, now()
  FROM unnest(v_domains) AS d
  ORDER BY d
  ON CONFLICT (domain, owner_service_id) DO UPDATE SET
    owner_service_name = EXCLUDED.owner_service_name,
    request_topic = COALESCE(EXCLUDED.request_topic, c.request_topic),
    claimed_at = EXCLUDED.claimed_at
  WHERE c.owner_service_name IS DISTINCT FROM EXCLUDED.owner_service_name
     OR (EXCLUDED.request_topic IS NOT NULL AND c.request_topic IS DISTINCT FROM EXCLUDED.request_topic);

  RETURN QUERY
  SELECT c.domain, c.owner_service_id, c.owner_service_name, c.request_topic
  FROM __SCHEMA__.wh_integrity_domain_claims c
  WHERE c.domain = ANY(v_domains)
    AND c.owner_service_id <> p_owner_service_id
  ORDER BY c.domain, c.owner_service_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_record_domain_claims(UUID, TEXT, TEXT, TEXT[]) IS
  '325 (#2008): replaces an owner''s domain claims with one announcement (lower case, no trailing dot; an absent '
  'request topic keeps the known one; unchanged rows are not rewritten) and returns the claims other owners hold on '
  'the announced domains.';
