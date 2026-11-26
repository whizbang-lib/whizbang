-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 597_TheEphemeralReapScansStartAtTheOldestBody.sql
-- Date: 2026-11-24
-- Description: The ephemeral reap's two candidate scans start at the oldest ephemeral event that still has a body,
--              instead of walking every ephemeral pointer the store has ever kept (#2574).
--
--              Every maintenance cycle asks which (stream, perspective) pairs need a snapshot before their bodies are
--              reaped (CoordinatorCatalogSql.EphemeralPairsNeedingSnapshotAsync) and which bodies this cycle will reap
--              (EphemeralDestructionSql.CollectAboutToReapAsync). Both walk idx_event_store_ephemeral_reap (163), which
--              is partial on the ephemeral flag alone, and probe wh_event_body for each entry. The reap deletes the body
--              and keeps the pointer (the rebuild guard's signal), and the pointer prune is opt-in, so the index holds
--              every ephemeral pointer ever written while almost none of them still has a body. On a deployed service:
--              10.3 million ephemeral pointers of 16.2 million events, about 200 bodies reaped a cycle, and the two
--              scans read all 10.3 million entries every cycle (about two minutes of three backends, the largest CPU
--              consumer of the database). The planner could not see it: it estimates (flags & 8) = 8 at the default
--              selectivity, about 31,500 rows a worker against ten million.
--
--              wh_ephemeral_reap_scan_floor() keeps a floor in wh_settings: an instant below which no ephemeral pointer
--              has a body. Each call walks the index forward from the stored floor and stops at the first pointer whose
--              body is still stored, so a pointer is walked past once after its body goes rather than once a cycle. Both
--              scans read the floor as an index condition (created_at >= floor).
--
--                the walk stops   at the oldest pointer with a body: the floor; with none, at the newest pointer it
--                                 walked past, so the floor never moves over a range it has not seen
--                a margin         the walk reads only pointers older than ephemeral_reap_scan_floor_margin_seconds
--                                 (default 3600), so an event stored by a transaction that commits late, with an
--                                 earlier created_at than events already visible, is still above the floor
--                a full rescan    every ephemeral_reap_scan_floor_rescan_seconds (default 86400) the walk starts from
--                                 the beginning again, so a body that reached the store below the floor by another
--                                 path (a restore that keeps an archived created_at) is found within a day
--
--              A body that stays (held by a hook, kept to its TTL, waiting on its perspective) holds the floor at its
--              own created_at, so the scans read what is above it; that is the set they have to re-examine anyway.
--
--              No index is added: the floor is a range on the key idx_event_store_ephemeral_reap already leads with.
--
--              Number 597: reserved for this change.
--
-- Reversal: reversible (the function goes; the two settings it wrote are left, and nothing reads them)
-- Dependencies: 163 (idx_event_store_ephemeral_reap), 072 (wh_event_body)
-- Objects: wh_ephemeral_reap_scan_floor
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>fundamentals/events/ephemeral-events#the-reap-scans-start-at-the-oldest-body</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapScanFloorSqlTests.cs:Floor_StopsAtTheOldestPointerWithABody_AndNeverPassesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapScanFloorSqlTests.cs:Floor_WithNoBodyLeft_RestsOnTheNewestPointerItWalkedPastAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapScanFloorSqlTests.cs:Floor_NeverWalksPointersInsideTheMarginAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapScanFloorSqlTests.cs:Floor_WithNothingOlderThanTheMargin_StaysAtTheBeginningAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapScanFloorSqlTests.cs:Floor_AfterTheRescanInterval_StartsFromTheBeginningAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReapScanFloorSqlTests.cs:Scans_ACycleAfterTheFirst_ReadOnlyThePointersAboveTheFloorAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_ephemeral_reap_scan_floor()
RETURNS TIMESTAMPTZ
LANGUAGE plpgsql
VOLATILE
SET timezone = 'UTC'
AS $$
DECLARE
  v_margin_seconds INTEGER;
  v_rescan_seconds INTEGER;
  v_cap TIMESTAMPTZ;
  v_floor TIMESTAMPTZ;
  v_rescanned_at TIMESTAMPTZ;
  v_next TIMESTAMPTZ;
BEGIN
  SELECT COALESCE(
    (SELECT CASE WHEN btrim(setting_value) ~ '^[0-9]{1,9}$' THEN btrim(setting_value)::INTEGER END
     FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_reap_scan_floor_margin_seconds'), 3600)
    INTO v_margin_seconds;
  SELECT COALESCE(
    (SELECT CASE WHEN btrim(setting_value) ~ '^[0-9]{1,9}$' THEN btrim(setting_value)::INTEGER END
     FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_reap_scan_floor_rescan_seconds'), 86400)
    INTO v_rescan_seconds;
  v_cap := NOW() - (v_margin_seconds * INTERVAL '1 second');

  SELECT setting_value::TIMESTAMPTZ INTO v_floor
    FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_reap_scan_floor';
  SELECT setting_value::TIMESTAMPTZ INTO v_rescanned_at
    FROM __SCHEMA__.wh_settings WHERE setting_key = 'ephemeral_reap_scan_floor_rescanned_at';
  IF v_floor IS NULL OR v_rescanned_at IS NULL
     OR v_rescanned_at < NOW() - (v_rescan_seconds * INTERVAL '1 second') THEN
    v_floor := '-infinity'::TIMESTAMPTZ;
    v_rescanned_at := NOW();
  END IF;

  -- The oldest pointer at or above the floor whose body is still stored, read through idx_event_store_ephemeral_reap in
  -- its own order, so the walk ends at the first such pointer.
  SELECT es.created_at INTO v_next
  FROM __SCHEMA__.wh_event_store es
  WHERE (es.flags & 8) = 8
    AND es.created_at >= v_floor
    AND es.created_at < v_cap
    AND EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = es.event_id)
  ORDER BY es.created_at
  LIMIT 1;

  IF v_next IS NULL THEN
    -- Every pointer below the margin has lost its body: the floor moves to the newest of them (one index entry, read
    -- backward), and stays where it was when there is none.
    SELECT es.created_at INTO v_next
    FROM __SCHEMA__.wh_event_store es
    WHERE (es.flags & 8) = 8
      AND es.created_at >= v_floor
      AND es.created_at < v_cap
    ORDER BY es.created_at DESC
    LIMIT 1;
  END IF;
  v_floor := COALESCE(v_next, v_floor);

  INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description)
  VALUES
    ('ephemeral_reap_scan_floor', v_floor::TEXT, 'timestamptz',
     'No ephemeral pointer older than this has a body; the reap scans start here (597, #2574).'),
    ('ephemeral_reap_scan_floor_rescanned_at', v_rescanned_at::TEXT, 'timestamptz',
     'When the ephemeral reap scan floor last walked from the beginning (597, #2574).')
  ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value, updated_at = NOW();

  RETURN v_floor;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_ephemeral_reap_scan_floor() IS
  '597 (#2574): the instant below which no ephemeral pointer has a body, advanced by walking '
  'idx_event_store_ephemeral_reap forward from the stored floor to the first pointer whose body is still stored. Reads '
  'only pointers older than ephemeral_reap_scan_floor_margin_seconds (3600), and walks from the beginning again every '
  'ephemeral_reap_scan_floor_rescan_seconds (86400). The reap''s candidate scans read created_at >= this floor.';
