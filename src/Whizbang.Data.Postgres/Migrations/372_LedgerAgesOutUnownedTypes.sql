-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 372_LedgerAgesOutUnownedTypes.sql
-- Date: 2026-11-09
-- Description: The stream-integrity repair ledger ages out rows for a type no service owns, from an origin that stopped
--              reporting (#2185).
--
--              370 forgets rows recorded under an origin that does not own the row's type. A row for a type nobody
--              owns (no claim in the ownership map, and outside this service's own owned domains) stays: its origin
--              is taken at its word, so a live origin's next comparison heals or refreshes it. Once that origin stops
--              reporting, nothing ever compares the row again and no owner exists to repair it, so it holds the
--              unhealed gauge up for good. The integrity audit now forgets such a row once its origin's last report is
--              older than Whizbang:StreamIntegrity:UnownedTypeOriginSilenceDays (default 7), in the same sweep and by
--              the same ownership map as 370's rows: the audit judges each (origin, type) pair once, in the
--              application, and hands the judged pairs to wh_integrity_forget_origin_types (370).
--
--              1. wh_integrity_ledger_origin_types: re-created from 370 with a third column, the origin's last report:
--                 the later of its last checkpoint (wh_integrity_known_origins.last_checkpoint_at, 339) and the pair's
--                 last report on the ledger (COALESCE(last_reported_at, first_seen_at), the clock the idle re-check
--                 reads, 348). The checkpoint is the origin's liveness clock; the ledger's own report time covers an
--                 origin whose last checkpoint predates 339 and so was never kept. last_touched cannot serve: the
--                 drain's claims and pending marks move it while the origin says nothing.
--
--              No index: the read is the same bounded GROUP BY over the ledger (090 caps it) as 370's DISTINCT, joined
--              to wh_integrity_known_origins (one row per origin) by its primary key, once per audit cycle.
--
--              Number 372: 371 is this change set's repair-answer cap; 360 to 370 are taken by changes in flight.
--
-- Dependencies: 090 (wh_integrity_ledger), 339 (wh_integrity_known_origins), 370 (wh_integrity_ledger_origin_types)
-- Objects: wh_integrity_ledger_origin_types

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests); the result gains a column.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_ledger_origin_types');

-- The distinct (origin, type) pairs the ledger holds, each with its origin's last report, for the audit to judge once.
-- <docs>resilience/stream-integrity#a-type-no-service-owns</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerForgetNonOwnerSqlTests.cs:Forget_RemovesEveryRowOfAJudgedPair_AndKeepsTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnownedAgeOutSqlTests.cs:Read_ReportsEachPairsOriginLastReport_TheLaterOfItsCheckpointAndItsRowsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_ledger_origin_types()
RETURNS TABLE (origin_service_id UUID, event_type TEXT, origin_last_reported_at TIMESTAMPTZ)
LANGUAGE sql
STABLE
AS $$
  SELECT l.origin_service_id, l.event_type,
         GREATEST(MAX(k.last_checkpoint_at), MAX(COALESCE(l.last_reported_at, l.first_seen_at)))
  FROM __SCHEMA__.wh_integrity_ledger l
  LEFT JOIN __SCHEMA__.wh_integrity_known_origins k ON k.origin_service_id = l.origin_service_id
  GROUP BY l.origin_service_id, l.event_type
  ORDER BY 1, 2;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_ledger_origin_types() IS
'372: the distinct (origin, event type) pairs the integrity ledger holds, each with its origin''s last report (the later of its last checkpoint and the pair''s last report on the ledger). The integrity audit forgets the pairs whose origin does not own the type, and the pairs of a type no service owns whose origin has been silent past UnownedTypeOriginSilenceDays (wh_integrity_forget_origin_types).';
