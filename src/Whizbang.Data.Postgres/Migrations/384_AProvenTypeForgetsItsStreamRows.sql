-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 384_AProvenTypeForgetsItsStreamRows.sql
-- Date: 2026-11-09
-- Description: A type proven complete over its whole history forgets every per-stream ledger row of it, with its type
--              lane, in one step (phantom heal, follow-up to #2207).
--
--              #2207 made a type-level comparison that proves a (tenant, type) complete heal the type's type lane row.
--              The same proof covers every stream of the type: equal roll-ups over the whole history mean no stream of
--              the type is missing anything, so a per-stream row still tracked for it is a phantom (written under an
--              older rule, or by a comparison whose deficit has since healed without the stream being compared again).
--              Those rows kept the gauges up and, where they were deficits, kept the paced drain asking origins for
--              events this service already held.
--
--              1. wh_integrity_mark_types_healed (new): deletes every ledger row of each (origin, tenant, type), the type
--                 lane and every per-stream row, and returns each deleted row's age in seconds, as
--                 wh_integrity_mark_healed_batch (094) does for its keys, so every heal is recorded.
--
--              The caller decides the proof; the function deletes what it is given. The comparison passes only types it
--              proved over the whole history (a full-history answer, or a window starting at or below the seal that the
--              origin did not cap); a type with a stream genuinely missing has a roll-up that disagrees and is never
--              passed. The delete reads the ledger through its primary key prefix (origin, tenant, type).
--
--              Number 384: 376 to 383 are taken on develop or by changes in flight.
--
-- Dependencies: 090 (wh_integrity_ledger), 094 (the heal's age rule)
-- Objects: wh_integrity_mark_types_healed

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_mark_types_healed');

-- Forgets every ledger row of the given (tenant, type) pairs of one origin, the type lane and every per-stream row,
-- returning each forgotten row's age in seconds (first sighting to this heal), clamped at zero for clock skew (094).
-- An absent tenant keys as the ledger keys it, the empty scope.
-- <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeHealSqlTests.cs:MarkTypesHealed_ForgetsTheTypeLaneAndEveryStreamRow_OfTheProvenTypesOnly_WithTheirAgesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeHealSqlTests.cs:MarkTypesHealed_NothingTracked_IsAnEmptyAnswerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_types_healed(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[]
) RETURNS SETOF DOUBLE PRECISION
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  WITH types AS (
    SELECT DISTINCT COALESCE(t.tenant, '') AS tenant_scope, t.etype AS event_type
    FROM unnest(p_tenant_scopes, p_event_types) AS t(tenant, etype)
  ),
  healed AS (
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    USING types t
    WHERE l.origin_service_id = p_origin_service_id
      AND l.tenant_scope      = t.tenant_scope
      AND l.event_type        = t.event_type
    RETURNING l.first_seen_at
  )
  SELECT GREATEST(EXTRACT(EPOCH FROM (NOW() - h.first_seen_at)), 0)::DOUBLE PRECISION
  FROM healed h;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_types_healed(UUID, TEXT[], TEXT[]) IS
'384 (phantom heal, follow-up to #2207): forgets every ledger row (the type lane and every per-stream row) of the given (tenant, type) pairs of one origin, which a comparison proved complete over the whole history, returning each forgotten row''s age in seconds.';
