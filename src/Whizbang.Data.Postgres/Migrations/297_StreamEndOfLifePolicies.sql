-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 297_StreamEndOfLifePolicies.sql
-- Date: 2026-11-02
-- Description: Policy-driven stream end of life (#1740, WS-3 S11).
--
--              A stream type may declare end-of-life triggers (Whizbang:StreamLifecycle:Types:<StreamType>: IdleTtl, MaxAge,
--              TenantByteCap, KeepTailVersions) and an Action. The disposition step passes the configured policies to
--              wh_stream_policy_candidates, which reads the lifecycle ledger alone and returns the open streams a trigger
--              picks, least recently appended first: an active, unsealed stream with no disposition scheduled, of a policy's
--              type, with the first trigger it meets. A whole-stream trigger wins over the version cap, which it subsumes.
--
--              The version cap needs to know how much of a stream's prefix has already left the hot store, which nothing in
--              the ledger recorded: truncated_through_version (unindexed, written only by the step after a cut, never by an
--              append, so an append's ledger update stays heap-only) holds the highest cut, and
--              wh_set_stream_truncated_through records it.
--
--              A policy that purges or compacts an open stream first seals it with wh_seal_stream_if_unchanged: under the
--              append lock every write path takes, and only when the stream's newest version is still the one the policy
--              saw, so an append between the pick and the purge is never lost (the stream is alive again and is left open).
--
--              wh_stream_disposition_readiness answers, for the streams a policy picked, the consumption gate and the holds a
--              due stream's readiness reads.
--
--              wh_due_stream_dispositions (copied from 280, its last word) also returns each due stream's type, so a sealed
--              stream's disposition is its type's Action. Its result changes, so every overload is dropped first.
--
--              Number 297: reserved for this change.
--
-- Dependencies: 260 (wh_stream_lifecycle), 266 (sealed_at, sealed_reason), 280 (wh_due_stream_dispositions)
-- Objects: truncated_through_version, wh_stream_policy_candidates, wh_stream_disposition_readiness,
--          wh_seal_stream_if_unchanged, wh_set_stream_truncated_through, wh_due_stream_dispositions
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_stream_lifecycle ADD COLUMN IF NOT EXISTS truncated_through_version BIGINT NULL;

COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.truncated_through_version IS
  'The highest version through which an end-of-life version cap cut the stream''s prefix out of the hot store (#1740); '
  'null until one did. Unindexed and never written by an append.';

SELECT __SCHEMA__.drop_all_overloads('wh_stream_policy_candidates');

-- The open streams an end-of-life policy trigger picks at p_now, least recently appended first, up to p_limit. The policies
-- are parallel arrays, one element per stream type; a NULL threshold is no trigger. Each picked stream carries the first
-- trigger it meets, numbered as StreamEndOfLifeTrigger numbers them:
--   1 IdleTtl:       the end of the hour of the newest append is at least the TTL before p_now;
--   2 MaxAge:        the first append is at least the age before p_now;
--   3 TenantByteCap: the stream is among the least recently appended of its type and tenant (no tenant is one group) that
--                    must go for the rest, kept newest first, to hold no more than the cap;
--   4 VersionCap:    more versions above the recorded cut than the tail; the prefix through the newest version less the
--                    tail goes (through_version).
-- Reads only the lifecycle ledger.
-- <docs>fundamentals/events/stream-end-of-life#policies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:IdleTtl_PicksAStreamOnceItsNewestAppendsHourIsAFullTtlOld_AndNotASecondBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:MaxAge_PicksAStreamWhoseFirstAppendIsAMaxAgeOld_AndNotAMicrosecondBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:KeepTailVersions_PicksAStreamWithMoreHotVersions_ThroughTheVersionThatLeavesExactlyTheTail_AndTheTruncateLeavesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:TenantByteCap_PicksTheLeastRecentlyAppendedStreamsUntilTheRestFit_PerTenant_WithNoTenantAsOneGroupAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:Triggers_PickOnlyOpenUnscheduledStreamsOfAPolicysType_LeastRecentlyAppendedFirst_TheFirstTriggerWinning_UpToTheLimitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:PolicyCandidates_ReadOnlyTheLifecycleLedger_AndNoOtherTableAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_stream_policy_candidates(
  p_now TIMESTAMPTZ,
  p_limit INTEGER,
  p_stream_types TEXT[],
  p_idle_ttl_us BIGINT[],
  p_max_age_us BIGINT[],
  p_keep_tail_versions BIGINT[],
  p_tenant_byte_caps BIGINT[])
RETURNS TABLE(candidate_stream_id UUID, candidate_stream_type TEXT, end_of_life_trigger INTEGER, candidate_last_version BIGINT,
              through_version BIGINT) AS $$
BEGIN
  RETURN QUERY
  WITH policy AS (
    SELECT p.stream_type, p.idle_us, p.age_us, p.keep_tail, p.byte_cap
    FROM unnest(p_stream_types, p_idle_ttl_us, p_max_age_us, p_keep_tail_versions, p_tenant_byte_caps)
      AS p(stream_type, idle_us, age_us, keep_tail, byte_cap)
  ),
  open_stream AS (
    SELECT l.stream_id, l.stream_type, l.tenant, l.first_appended_at, l.last_appended_bucket, l.last_version, l.approx_bytes,
           l.truncated_through_version, p.idle_us, p.age_us, p.keep_tail, p.byte_cap
    FROM __SCHEMA__.wh_stream_lifecycle l
    JOIN policy p ON p.stream_type = l.stream_type
    WHERE l.state = 'Active'
      AND l.sealed_at IS NULL
      AND l.disposition_due_at IS NULL
  ),
  kept AS (
    -- Per type and tenant, the bytes of each stream and every stream appended after it: the streams whose running total
    -- passes the cap are the ones that must go for the newer ones to fit.
    SELECT s.stream_id,
           SUM(s.approx_bytes) OVER (
             PARTITION BY s.stream_type, s.tenant
             ORDER BY s.last_appended_bucket DESC, s.first_appended_at DESC, s.stream_id DESC) AS running_bytes,
           s.byte_cap
    FROM open_stream s
    WHERE s.byte_cap IS NOT NULL
  ),
  picked AS (
    SELECT s.stream_id, s.stream_type, s.last_appended_bucket, s.last_version, s.keep_tail,
           CASE
             WHEN s.idle_us IS NOT NULL
                  AND s.last_appended_bucket + INTERVAL '1 hour' <= p_now - s.idle_us * INTERVAL '1 microsecond' THEN 1
             WHEN s.age_us IS NOT NULL
                  AND s.first_appended_at <= p_now - s.age_us * INTERVAL '1 microsecond' THEN 2
             WHEN k.running_bytes > k.byte_cap THEN 3
             WHEN s.keep_tail IS NOT NULL
                  AND s.last_version - COALESCE(s.truncated_through_version, 0) > s.keep_tail THEN 4
           END AS end_of_life_trigger
    FROM open_stream s
    LEFT JOIN kept k ON k.stream_id = s.stream_id
  )
  SELECT pk.stream_id,
         pk.stream_type,
         pk.end_of_life_trigger,
         pk.last_version,
         CASE WHEN pk.end_of_life_trigger = 4 THEN pk.last_version - pk.keep_tail END
  FROM picked pk
  WHERE pk.end_of_life_trigger IS NOT NULL
  ORDER BY pk.last_appended_bucket, pk.stream_id
  LIMIT p_limit;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_stream_policy_candidates(TIMESTAMPTZ, INTEGER, TEXT[], BIGINT[], BIGINT[], BIGINT[], BIGINT[]) IS
  'The open streams an end-of-life policy trigger picks (#1740): 1 IdleTtl, 2 MaxAge, 3 TenantByteCap, 4 VersionCap (with the '
  'prefix''s last version), least recently appended first. Reads only the lifecycle ledger.';

SELECT __SCHEMA__.drop_all_overloads('wh_stream_disposition_readiness');

-- Whether each of p_stream_ids can go now, as StreamDispositionReadiness numbers it: 1 (Blocked) while a perspective owes
-- one of its events, 2 (Held) while an active destruction hold covers one, else 0 (Ready). The gates a due stream's
-- readiness reads (280), for the streams an end-of-life policy picked.
-- <docs>fundamentals/events/stream-end-of-life#policies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:GetReadiness_ReadsTheConsumptionGateAndTheHolds_PerStreamAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_stream_disposition_readiness(p_stream_ids UUID[])
RETURNS TABLE(ready_stream_id UUID, readiness INTEGER) AS $$
BEGIN
  RETURN QUERY
  SELECT s.stream_id,
         CASE
           WHEN EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
             WHERE pe.stream_id = s.stream_id AND pe.processed_at IS NULL) THEN 1
           WHEN EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
             WHERE es.stream_id = s.stream_id AND h.hold_until > NOW()) THEN 2
           ELSE 0
         END
  FROM unnest(p_stream_ids) AS s(stream_id);
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_stream_disposition_readiness(UUID[]) IS
  'Whether each stream can go now (#1740): 0 Ready, 1 Blocked (a perspective owes an event), 2 Held (an active destruction '
  'hold). Reads only.';

SELECT __SCHEMA__.drop_all_overloads('wh_seal_stream_if_unchanged');

-- Seals an open stream under its append lock, but only when it is still Active, unsealed and at p_last_version, the version
-- an end-of-life policy saw when it picked it. Returns whether this call sealed it. A purge or a compaction by policy seals
-- first, so an append between the pick and the purge is either stored before the seal (and the seal refused, the stream
-- being alive again) or refused by it.
-- <docs>fundamentals/events/stream-end-of-life#policies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:SealIfUnchanged_SealsAnOpenStreamAtTheVersionSeen_AndNotOneAppendedSinceOrAlreadySealedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:SealIfUnchanged_WaitsForTheStreamsAppendLockAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_seal_stream_if_unchanged(p_stream_id UUID, p_last_version BIGINT, p_reason TEXT, p_now TIMESTAMPTZ)
RETURNS BOOLEAN AS $$
BEGIN
  -- The append lock every write path takes (266), so the version compared is the version stored.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET state = 'Sealed',
      sealed_at = p_now,
      sealed_reason = p_reason
  WHERE l.stream_id = p_stream_id
    AND l.state = 'Active'
    AND l.sealed_at IS NULL
    AND l.last_version = p_last_version;
  RETURN FOUND;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_seal_stream_if_unchanged(UUID, BIGINT, TEXT, TIMESTAMPTZ) IS
  'Seals an open stream under its append lock only when its newest version is still p_last_version (#1740). Returns whether '
  'it sealed.';

SELECT __SCHEMA__.drop_all_overloads('wh_set_stream_truncated_through');

-- Records that the stream's prefix through p_through_version left the hot store (an end-of-life version cap's cut). The
-- recorded cut only moves forward.
-- <docs>fundamentals/events/stream-end-of-life#policies</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:SetTruncatedThrough_RecordsTheCut_AndNeverMovesItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:KeepTailVersions_PicksAStreamWithMoreHotVersions_ThroughTheVersionThatLeavesExactlyTheTail_AndTheTruncateLeavesItAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_set_stream_truncated_through(p_stream_id UUID, p_through_version BIGINT)
RETURNS VOID AS $$
BEGIN
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET truncated_through_version = GREATEST(COALESCE(l.truncated_through_version, 0), p_through_version)
  WHERE l.stream_id = p_stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_set_stream_truncated_through(UUID, BIGINT) IS
  'Records the highest version through which an end-of-life version cap cut the stream''s prefix (#1740); never moves back.';

-- wh_due_stream_dispositions: last word 280, also returning each due stream's type (#1740), so a sealed stream's
-- disposition is its type's Action. The result changes, so every overload is dropped first.
SELECT __SCHEMA__.drop_all_overloads('wh_due_stream_dispositions');

-- The streams whose disposition is due at p_now, oldest first, from idx_stream_lifecycle_due, each with whether it can go,
-- as StreamDispositionReadiness numbers it: 3 (Done) when it is no longer Sealed (archived already), 1 (Blocked) while a
-- perspective owes one of its events, 2 (Held) while an active destruction hold covers one, else 0 (Ready), and with its
-- type. The same gates a whole-stream archive applies, read first so the final snapshot is taken only once every
-- perspective has consumed the stream.
-- <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs:GetDue_ReturnsTheDueStreamsOldestFirst_WithWhetherEachCanGo_AndOnlyUpToItsLimitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs:Step_ArchivesASealedStreamWhole_OnceItsGraceHasPassed_AndNotBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamEndOfLifePolicySqlTests.cs:GetDue_CarriesEachStreamsType_SoItsDispositionIsTheTypesActionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_due_stream_dispositions(p_now TIMESTAMPTZ, p_limit INTEGER)
RETURNS TABLE(due_stream_id UUID, readiness INTEGER, due_stream_type TEXT) AS $$
BEGIN
  RETURN QUERY
  SELECT d.stream_id,
         CASE
           WHEN d.state <> 'Sealed' THEN 3
           WHEN EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
             WHERE pe.stream_id = d.stream_id AND pe.processed_at IS NULL) THEN 1
           WHEN EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
             WHERE es.stream_id = d.stream_id AND h.hold_until > NOW()) THEN 2
           ELSE 0
         END,
         d.stream_type
  FROM (
    SELECT l.stream_id, l.state, l.disposition_due_at, l.stream_type
    FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.disposition_due_at <= p_now
    ORDER BY l.disposition_due_at, l.stream_id
    LIMIT p_limit
  ) d
  ORDER BY d.disposition_due_at, d.stream_id;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_due_stream_dispositions(TIMESTAMPTZ, INTEGER) IS
  'The streams whose disposition is due, oldest first, each 0 Ready, 1 Blocked (a perspective owes an event), 2 Held (an '
  'active destruction hold) or 3 Done (no longer Sealed) (#1734), with its type (#1740). Reads only.';
