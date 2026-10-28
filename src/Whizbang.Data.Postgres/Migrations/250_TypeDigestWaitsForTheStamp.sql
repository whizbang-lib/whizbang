-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 250_TypeDigestWaitsForTheStamp.sql
-- Date: 2026-10-27
-- Description: The local lane's type-level digest folds only stamped rows in its open window (#1791).
--
--              compute_type_digests_epoch folded every local row above the closure frontier, and every row with
--              no commit_sequence at all, once its created_at was older than the settle window. An upgrade from
--              below 046 leaves the whole history unstamped, and history is old by its creation time, so the open
--              window counted it as live while the stamper was still handing it sequences. Everything else that
--              decides what this lane has settled waits for the sequence: integrity_settled_max reads it,
--              compute_type_digests_epoch_window folds only rows that have one, epoch closure ranges over it, and
--              the stamp fence holds an unstamped row back from the perspectives. The type answer now waits too,
--              and a row joins it as soon as it is stamped, in whichever epoch or window its sequence falls.
--
--              The received lanes are unchanged. A received row can lack the origin's sequence for good (an outbox
--              row published before it was stamped carries none, and so does a row received before 087), and the
--              open window is the only place it can ever fold.
--
--              Cost: the local lane's open fold is now a range of commit_sequence (idx_event_store_commit_sequence)
--              instead of that range OR an IS NULL test, which no single index scan could serve.
--
--              Number 250: 249 is reserved for a concurrent change.
--
-- Dependencies: 216 (last word of compute_type_digests_epoch, copied verbatim apart from the local lane's open fold)
-- Objects: compute_type_digests_epoch
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- <docs>resilience/stream-integrity#unstamped-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:LocalLane_AnUnstampedRow_IsLeftOutOfTheOpenWindowUntilItIsStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:ReceivedLane_ARowWithNoOriginSequence_StillFoldsInTheOpenWindowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochServedTypeDigestSqlTests.cs:EpochComposition_EqualsTheFullFold_AcrossClosedAndOpenRangesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.compute_type_digests_epoch(
  p_origin_lane    UUID,          -- NULL = the local lane (this service's own emissions)
  p_event_types    TEXT[],        -- NULL = all types
  p_now            TIMESTAMPTZ,
  p_settle_seconds INTEGER
) RETURNS TABLE (tenant TEXT, event_type TEXT, digest_lo BIGINT, digest_hi BIGINT, event_count INTEGER)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
  c_zero CONSTANT UUID := __EMPTY_UUID__;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_lane      UUID := COALESCE(p_origin_lane, c_zero);
  v_frontier  BIGINT := -1;
  v_width     BIGINT;
  v_open_from BIGINT := 0;
  v_types     TEXT[];
BEGIN
  IF p_event_types IS NOT NULL THEN
    SELECT array_agg(__SCHEMA__.normalize_event_type(t)) INTO v_types
    FROM unnest(p_event_types) AS t;
  END IF;

  SELECT f.closed_through_epoch, f.epoch_width INTO v_frontier, v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = v_lane;

  IF v_width IS NULL OR v_frontier < 0 THEN
    v_frontier := -1;      -- nothing sealed: the whole lane folds live below
    v_open_from := 0;
  ELSE
    v_open_from := (v_frontier + 1) * v_width;
  END IF;

  RETURN QUERY
  WITH sealed AS (
    SELECT de.scope_tenant AS b_tenant, de.event_type AS b_type,
           bit_xor(de.digest_lo) AS lo, bit_xor(de.digest_hi) AS hi,
           SUM(de.event_count)::bigint AS cnt
    FROM __SCHEMA__.wh_digest_epochs de
    WHERE de.origin_service_id = v_lane
      AND de.epoch_id <= v_frontier
      AND (v_types IS NULL OR de.event_type = ANY(v_types))
    GROUP BY 1, 2
  ),
  open_fold AS (
    -- The live remainder. #1791: in the local lane only stamped rows fold. A row the stamper has not yet
    -- sequenced is not settled here: the windowed answer, integrity_settled_max and the stamp fence all wait for its
    -- sequence, and once it has one it folds where that sequence puts it, so it never falls through the crack.
    -- A received row keeps the NULL-sequence branch: it can lack the origin's sequence for good (published
    -- before its stamp, or received before 087), and the open window is the only place it can ever fold.
    SELECT COALESCE(es.scope ->> 't', '') AS b_tenant, es.event_type AS b_type,
           bit_xor(hashtextextended(es.event_id::text, 0)) AS lo,
           bit_xor(hashtextextended(es.event_id::text, 1)) AS hi,
           COUNT(*)::bigint AS cnt
    FROM __SCHEMA__.wh_event_store es
    WHERE ((p_origin_lane IS NULL AND es.origin_service_id IS NULL)
           OR es.origin_service_id = p_origin_lane)
      AND (CASE WHEN p_origin_lane IS NULL
                THEN es.commit_sequence IS NOT NULL AND es.commit_sequence >= v_open_from
                ELSE es.origin_commit_sequence IS NULL OR es.origin_commit_sequence >= v_open_from
           END)
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)  -- 216 (#1492)
      AND es.created_at < p_now - make_interval(secs => p_settle_seconds)
    GROUP BY 1, 2
  )
  SELECT u.b_tenant, u.b_type, bit_xor(u.lo), bit_xor(u.hi), SUM(u.cnt)::int
  FROM (SELECT * FROM sealed UNION ALL SELECT * FROM open_fold) u
  GROUP BY 1, 2
  ORDER BY 1, 2;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_type_digests_epoch IS
'Type-level digest answer served from sealed epochs + a live fold of only the open window. Sealed rows are authoritative (the self-sweep owns detecting a bad seal). Falls back to the plain full fold when the lane has no closed epochs. 214: the live fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). #1791: the local lane folds only stamped rows; a received row with no origin sequence still folds in the open window.';
