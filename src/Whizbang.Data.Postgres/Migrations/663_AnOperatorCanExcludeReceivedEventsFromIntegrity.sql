-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 663_AnOperatorCanExcludeReceivedEventsFromIntegrity.sql
-- Date: 2026-11-27
-- Description: An operator can exclude received events from integrity checks, and reverse it (#2588).
--
--              An escape hatch, never a repair: an operator marks events a defect made unresolvable (or history
--              recorded under older rules) so the integrity engine stops comparing, asking, holding, healing and
--              alarming about them. Each mark has a reason, an author and a time, and is logged.
--
--                received only   a mark is refused for this service's own events (no origin, the empty origin or this
--                                service's id): its consumers compare against what it answers, so excluding one of
--                                its own events would turn into a mismatch at every consumer (#2588 decision D1). A
--                                row whose first hop names this service (594's _wh_first_hop_service) was written here
--                                whatever origin it carries, so it is refused too: 599 returns it to the local lane.
--                stored only     an event this service does not store is refused ('not_stored'): excluding events a
--                                service is missing is open question #2616 (decision D3).
--                superseding     a superseding event is refused: its digest counts its key's slot, not itself, so the
--                                origin's answer cannot be adjusted by the event alone.
--
--              Marking (wh_integrity_exclude) sets EventFlags.IntegrityExcluded (256) on the row, which the
--              eligibility rule (version 2, 662) reads, and refolds exactly what it changed, as
--              _wh_flag_at_most_once_occurrences does (453): the stream digest buckets it counted in (through the one
--              fold helper, emptied buckets removed) and the sealed epochs of its origin lane covering its origin
--              position. The held fold follows the flag through its trigger (452). The mark records where the origin
--              holds the event, which the comparison needs to remove it from the origin's answer:
--                1 held          the row carries the origin's commit sequence;
--                2 unknown       the origin already answered that it does not know the event (its recorded unknown
--                                answer is removed: the event is resolved by decision);
--                0 pending       neither: the event is asked for through the origin-position exchange
--                                (wh_integrity_exclusion_ask) and the answer settles it (wh_integrity_exclusion_settle).
--                                A bucket holding a pending mark is undecided until then (compared, never repaired).
--              The repair-ledger rows of the buckets it touched are removed: the next comparison, made after the
--              adjustment, records a divergence only if one remains, and the repair drain never re-asks for an
--              excluded event in between.
--
--              Unmarking (wh_integrity_unexclude) is the exact inverse: the flag goes, the event is folded back, the
--              epochs refold, the mark and the ledger rows go, and the event is resolved again from scratch (the held
--              walk asks for it again when it is unpositioned).
--
--              Selections (wh_integrity_exclusion_select): by origin, event types, a created_at range, and "currently
--              unresolved" (unknown answers, held rows, unhealed ledger buckets) before a time, walked by event id in
--              bounded batches. Requests (wh_integrity_exclusion_requests) are applied by the maintenance step
--              IntegrityExclusionStep one batch per cycle (wh_integrity_exclusion_apply_batch), as archive restore
--              requests are.
--
--              Hot paths: no index changes on wh_event_store, no join on any fold. The comparison reads
--              wh_integrity_exclusion_adjustment, one probe of idx_integrity_exclusions_origin that returns no rows for
--              an origin without marks.
--
--              The origin-position exchange (561): the held ask (wh_origin_position_ask) leaves an excluded row out, since
--              its mark is asked instead, and the origin's answer (wh_origin_positions_of) names the bucket it counts each
--              event in (tenant, type, stream: OriginPosition's optional fields), so a mark is removed from the right
--              bucket. Both copied whole from 561.
--
--              Every mark and unmark writes wh_integrity_exclusion_log (append-only): who, why, which request or
--              selection, how many per origin.
--
-- Reversal: reversible (the tables and functions go; flagged rows are unflagged and folded back first)
-- Dependencies: 087 (wh_stream_digests), 090 (wh_integrity_ledger), 336 (_wh_fold_stream_digests), 408
--               (_wh_refold_epochs_covering), 452 (the held fold's triggers), 561 (wh_origin_position_unknown), 594
--               (_wh_first_hop_service), 662 (the eligibility rule, version 2)
-- Objects: wh_integrity_exclusions, wh_integrity_exclusion_origins, wh_integrity_exclusion_log,
--          wh_integrity_exclusion_requests, _wh_integrity_exclusion_candidates, _wh_integrity_exclusion_recount,
--          wh_integrity_exclude, wh_integrity_unexclude, wh_integrity_exclusion_select, wh_integrity_exclusion_settle,
--          wh_integrity_exclusion_ask, wh_integrity_exclusion_adjustment, wh_integrity_exclusion_dry_run,
--          wh_integrity_exclusion_apply_batch, wh_integrity_exclusion_summary, wh_integrity_exclusion_roll_off,
--          wh_origin_position_ask, wh_origin_positions_of, _wh_event_flags_from_names
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- The marks: one row per excluded event id, received history only.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_exclusions (
  event_id               UUID        NOT NULL PRIMARY KEY,
  origin_service_id      UUID        NOT NULL,
  tenant_scope           TEXT        NOT NULL,    -- '' for an unscoped event, as the digest buckets key it
  event_type             TEXT        NOT NULL,
  stream_id              UUID        NOT NULL,
  origin_state           SMALLINT    NOT NULL,    -- 0 pending, 1 held at the origin, 2 unknown to the origin
  origin_commit_sequence BIGINT,                  -- where the origin holds it (state 1)
  local_present          BOOLEAN     NOT NULL DEFAULT true,  -- a wh_event_store row carries the flag
  reason                 TEXT        NOT NULL,
  marked_by              TEXT        NOT NULL,
  marked_at              TIMESTAMPTZ NOT NULL,
  selection_id           UUID,                    -- the request that marked it, for audit and bulk unmark
  asked_at               TIMESTAMPTZ              -- when a pending mark was last asked of its origin
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_exclusions IS
'663 (#2588): received events an operator excluded from integrity checks, one row per event, with where the origin holds it (0 pending, 1 held at origin_commit_sequence, 2 unknown to the origin). The comparison removes state 1 marks from the origin''s answer and treats a bucket with a state 0 mark as undecided.';

-- The comparison's read: an origin's marks by state, type and position; an origin with none answers from one probe.
CREATE INDEX IF NOT EXISTS idx_integrity_exclusions_origin
  ON __SCHEMA__.wh_integrity_exclusions (origin_service_id, origin_state, event_type, origin_commit_sequence);

-- The pending marks still to ask, least recently asked first.
CREATE INDEX IF NOT EXISTS idx_integrity_exclusions_pending
  ON __SCHEMA__.wh_integrity_exclusions (origin_service_id, asked_at NULLS FIRST, event_id) WHERE origin_state = 0;

CREATE INDEX IF NOT EXISTS idx_integrity_exclusions_selection
  ON __SCHEMA__.wh_integrity_exclusions (selection_id) WHERE selection_id IS NOT NULL;

-- Per origin, how many marks it has and how many are pending: the metrics' gauges, read in O(origins). An origin with no
-- marks has no row.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_exclusion_origins (
  origin_service_id UUID        NOT NULL PRIMARY KEY,
  excluded_count    BIGINT      NOT NULL,
  pending_count     BIGINT      NOT NULL,
  unknown_count     BIGINT      NOT NULL,
  updated_at        TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_exclusion_origins IS
'663 (#2588): per origin, the integrity exclusions it has (all states), those still pending an answer from the origin, and those the origin does not know. Maintained by every mark, unmark and settle; an origin with no marks has no row.';

-- The audit log: every mark and unmark, append-only. Rows older than the log retention are removed by the step.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_exclusion_log (
  log_id            BIGINT      GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
  logged_at         TIMESTAMPTZ NOT NULL,
  action            TEXT        NOT NULL,    -- 'exclude' | 'unexclude'
  origin_service_id UUID        NOT NULL,
  selection_id      UUID,                    -- the request applied, or NULL for an inline call
  event_count       INTEGER     NOT NULL,    -- events marked (or unmarked) for this origin
  reason            TEXT        NOT NULL,
  requested_by      TEXT        NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_integrity_exclusion_log_logged_at
  ON __SCHEMA__.wh_integrity_exclusion_log (logged_at);

COMMENT ON TABLE __SCHEMA__.wh_integrity_exclusion_log IS
'663 (#2588): the audit trail of integrity exclusions: every mark and unmark, per origin and batch, with who asked and why. Append-only; rows older than Whizbang:StreamIntegrity:Exclusions:LogRetention are removed by the exclusion step.';

-- The requests the exclusion step applies: by event ids above the inline limit, or by selection.
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_exclusion_requests (
  request_id      UUID        NOT NULL PRIMARY KEY,
  action          TEXT        NOT NULL,      -- 'exclude' | 'unexclude'
  event_ids       UUID[],                    -- by ids, or
  selection       JSONB,                     -- by selection (exclude), or
  of_selection_id UUID,                      -- every mark of an earlier request (unexclude)
  reason          TEXT        NOT NULL,
  requested_by    TEXT        NOT NULL,
  requested_at    TIMESTAMPTZ NOT NULL,
  status          TEXT        NOT NULL DEFAULT 'Pending',   -- Pending | Done | Failed
  cursor_event_id UUID,                      -- the walk's position: every candidate at or below it was applied
  applied         INTEGER     NOT NULL DEFAULT 0,
  refused         INTEGER     NOT NULL DEFAULT 0,
  completed_at    TIMESTAMPTZ,
  error           TEXT
);

CREATE INDEX IF NOT EXISTS idx_integrity_exclusion_requests_open
  ON __SCHEMA__.wh_integrity_exclusion_requests (requested_at) WHERE status = 'Pending';

COMMENT ON TABLE __SCHEMA__.wh_integrity_exclusion_requests IS
'663 (#2588): integrity exclusion requests the maintenance step applies one bounded batch per cycle, oldest first. Done and Failed requests are removed after the log retention.';

-- ── _wh_event_flags_from_names: last word 295, copied whole; 663 adds IntegrityExcluded (1 << 8) ──
-- <docs>fundamentals/messaging/collective-events</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsWrittenAsNames_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingEventReaperSqlTests.cs:Emit_FlagsWrittenAsNames_StampTheKeyOfASupersedingEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreInbox_FlagsWrittenAsNames_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsNamedUnderANamingPolicy_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsWrittenAsNamesAndANumber_KeepsEveryBitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StringEventFlagsStoreTests.cs:StoreOutbox_FlagsNameNoEventFlagsValueHas_RaisesAndStoresNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_event_flags_from_names(p_names TEXT)
RETURNS INTEGER AS $$
DECLARE
  v_token TEXT;
  v_name TEXT;
  v_bits INTEGER;
  v_flags INTEGER := 0;
BEGIN
  FOREACH v_token IN ARRAY string_to_array(p_names, ',') LOOP
    -- One spelling per name under every naming policy: atMostOnce, at_most_once, AT_MOST_ONCE, at-most-once.
    v_name := lower(regexp_replace(v_token, '[[:space:]_-]', '', 'g'));
    v_bits := CASE
      WHEN v_name ~ '^[0-9]+$' THEN v_name::INTEGER
      WHEN v_name = 'none' THEN 0             -- EventFlags.None
      WHEN v_name = 'collective' THEN 1       -- EventFlags.Collective (1 << 0)
      WHEN v_name = 'composite' THEN 2        -- EventFlags.Composite (1 << 1)
      WHEN v_name = 'norebroadcast' THEN 4    -- EventFlags.NoRebroadcast (1 << 2)
      WHEN v_name = 'ephemeral' THEN 8        -- EventFlags.Ephemeral (1 << 3)
      WHEN v_name = 'compacted' THEN 16       -- EventFlags.Compacted (1 << 4)
      WHEN v_name = 'atmostonce' THEN 32      -- EventFlags.AtMostOnce (1 << 5)
      WHEN v_name = 'terminal' THEN 64        -- EventFlags.Terminal (1 << 6), 266
      WHEN v_name = 'superseding' THEN 128    -- EventFlags.Superseding (1 << 7), 295
      WHEN v_name = 'integrityexcluded' THEN 256  -- EventFlags.IntegrityExcluded (1 << 8), 663
    END;
    IF v_bits IS NULL THEN
      RAISE EXCEPTION USING
        ERRCODE = 'invalid_text_representation',
        MESSAGE = format('Event flags %L name %L, which no EventFlags value has, so the event''s kind cannot be read '
          'and it is not stored (#1612).', p_names, btrim(v_token));
    END IF;
    v_flags := v_flags | v_bits;
  END LOOP;
  RETURN v_flags;
END;
$$ LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION __SCHEMA__._wh_event_flags_from_names(TEXT) IS
  'The bits of EventFlags written as a comma-separated name list (JsonStringEnumConverter''s form), matched without '
  'regard to case, spaces, underscores or hyphens; a number among the names is read as its bits. Raises 22P02 on a '
  'name EventFlags does not define. Read by store_outbox_messages and store_inbox_messages for a value that is not a '
  'number (232, #1612). 266: and Terminal (#1733). 295: and Superseding (#1729). 663: and IntegrityExcluded (#2588).';

-- ── wh_origin_position_ask: last word 561, copied whole; 663 leaves excluded rows out of the ask ──
SELECT __SCHEMA__.drop_all_overloads('wh_origin_position_ask');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_WalksOneOriginInBoundedBatches_AndRestartsTheLapOnlyAfterThePauseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_ServesTheOriginServedLeastRecently_AndLeavesOutUnknownAnswersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:Ask_OnALargeStore_ReadsOnlyItsBatchOfTheHeldIndexAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_position_ask(
  p_batch_size INTEGER,
  p_lap_pause INTERVAL,
  p_now TIMESTAMPTZ
) RETURNS TABLE(origin_service_id UUID, event_ids UUID[], lap_ended BOOLEAN) AS $$
#variable_conflict use_column
DECLARE
  v_origin UUID;
  v_after_at TIMESTAMPTZ;
  v_after_id UUID;
  v_ids UUID[];
  v_read INTEGER;
  v_last_at TIMESTAMPTZ;
  v_last_id UUID;
BEGIN
  -- Every origin holding a row without its position, one index probe per origin (a skip scan of the held index).
  INSERT INTO __SCHEMA__.wh_origin_position_walk (origin_service_id)
  WITH RECURSIVE o(id) AS (
    (SELECT es.origin_service_id FROM __SCHEMA__.wh_event_store es
     WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL
     ORDER BY es.origin_service_id LIMIT 1)
    UNION ALL
    SELECT (SELECT es.origin_service_id FROM __SCHEMA__.wh_event_store es
            WHERE es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL AND es.origin_service_id > o.id
            ORDER BY es.origin_service_id LIMIT 1)
    FROM o WHERE o.id IS NOT NULL
  )
  SELECT o.id FROM o WHERE o.id IS NOT NULL
  ON CONFLICT (origin_service_id) DO NOTHING;

  SELECT w.origin_service_id, w.after_created_at, w.after_event_id INTO v_origin, v_after_at, v_after_id
  FROM __SCHEMA__.wh_origin_position_walk w
  WHERE w.next_lap_at IS NULL OR w.next_lap_at <= p_now
  ORDER BY w.updated_at ASC NULLS FIRST, w.origin_service_id
  LIMIT 1
  FOR UPDATE SKIP LOCKED;
  IF v_origin IS NULL THEN
    RETURN;
  END IF;

  SELECT COALESCE(array_agg(b.event_id ORDER BY b.created_at, b.event_id)
                    FILTER (WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_origin_position_unknown u WHERE u.event_id = b.event_id)
                            -- 663 (#2588): an excluded row is not asked for; its mark is asked through wh_integrity_exclusion_ask.
                            AND NOT b.excluded),
                  ARRAY[]::UUID[]),
         count(*)::int,
         (array_agg(b.created_at ORDER BY b.created_at DESC, b.event_id DESC))[1],
         (array_agg(b.event_id ORDER BY b.created_at DESC, b.event_id DESC))[1]
    INTO v_ids, v_read, v_last_at, v_last_id
  FROM (
    -- not the digest rule: an excluded held row is read (so the cursor passes it) but not asked for
    SELECT es.event_id, es.created_at, COALESCE(es.flags, 0) & 256 <> 0 AS excluded
    FROM __SCHEMA__.wh_event_store es
    WHERE es.origin_service_id = v_origin
      AND es.origin_commit_sequence IS NULL
      AND (v_after_at IS NULL OR (es.created_at, es.event_id) > (v_after_at, v_after_id))
    ORDER BY es.created_at, es.event_id
    LIMIT GREATEST(p_batch_size, 1)
  ) b;

  IF v_read < GREATEST(p_batch_size, 1) THEN
    -- The lap reached the end: start over after the pause, so an event the origin has not stamped yet is asked again.
    UPDATE __SCHEMA__.wh_origin_position_walk w
    SET after_created_at = NULL, after_event_id = NULL, next_lap_at = p_now + p_lap_pause, updated_at = p_now
    WHERE w.origin_service_id = v_origin;
  ELSE
    UPDATE __SCHEMA__.wh_origin_position_walk w
    SET after_created_at = v_last_at, after_event_id = v_last_id, next_lap_at = NULL, updated_at = p_now
    WHERE w.origin_service_id = v_origin;
  END IF;

  RETURN QUERY SELECT v_origin, v_ids, v_read < GREATEST(p_batch_size, 1);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_position_ask(INTEGER, INTERVAL, TIMESTAMPTZ) IS
'561 (#2544), 663 (#2588): the next batch of received events held without a position to ask their origin for: at most p_batch_size held rows of the origin served least recently, after its cursor, without those it answered unknown or an operator excluded. A lap that reaches the end restarts after p_lap_pause.';

-- ── wh_origin_positions_of: last word 561, copied whole; 663 adds the bucket each answered event is counted in ──
SELECT __SCHEMA__.drop_all_overloads('wh_origin_positions_of');
-- <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:PositionsOf_AnswersStamped_Unstamped_Archived_AndUnknownAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_origin_positions_of(p_event_ids UUID[])
RETURNS TABLE(event_id UUID, state TEXT, commit_sequence BIGINT, tenant_scope TEXT, event_type TEXT, stream_id UUID) AS $$
  SELECT u.eid,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL AND es.commit_sequence IS NOT NULL THEN 'positioned'
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN 'unstamped'
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL AND a.commit_sequence IS NOT NULL THEN 'archived'
           ELSE 'unknown'
         END,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN es.commit_sequence
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL THEN a.commit_sequence
         END,
         -- 663 (#2588): the bucket the event is counted in here, for an asker that excluded it.
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN es.scope ->> 't'
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL AND a.commit_sequence IS NOT NULL THEN a.scope ->> 't'
         END,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN es.event_type
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL AND a.commit_sequence IS NOT NULL THEN a.event_type::text
         END,
         CASE
           WHEN es.event_id IS NOT NULL AND es.origin_service_id IS NULL THEN es.stream_id
           WHEN es.event_id IS NULL AND a.origin_service_id IS NULL AND a.commit_sequence IS NOT NULL THEN a.stream_id
         END
  FROM (SELECT DISTINCT x.eid FROM unnest(p_event_ids) AS x(eid)) u
  LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
  LEFT JOIN __SCHEMA__.wh_event_archive a ON a.event_id = u.eid
  ORDER BY u.eid;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_origin_positions_of(UUID[]) IS
'561 (#2544), 663 (#2588, with the bucket each event is counted in): per asked event id, this service''s commit sequence for an event it wrote: positioned (from the store), unstamped (held, not stamped yet: ask again), archived (from wh_event_archive), or unknown (never written here, received from another service, or no longer held with a sequence).';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- What marking each asked event would do, read once for the mark and the dry run alike: 'mark' or why not.
SELECT __SCHEMA__.drop_all_overloads('_wh_integrity_exclusion_candidates');
-- <docs>resilience/integrity-exclusions#what-can-be-excluded</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Exclude_RefusesOwnEventsEventsWrittenHereMissingAndSupersedingOnes_AndMarksTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_integrity_exclusion_candidates(p_event_ids UUID[], p_service_name TEXT)
RETURNS TABLE(event_id UUID, outcome TEXT, origin_service_id UUID, tenant_scope TEXT, event_type TEXT, stream_id UUID,
              origin_commit_sequence BIGINT, was_eligible BOOLEAN, disowned BOOLEAN) AS $$
  WITH asked AS (
    SELECT DISTINCT x.eid FROM unnest(COALESCE(p_event_ids, ARRAY[]::UUID[])) AS x(eid)
  ),
  local_id AS (
    SELECT sc.service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1
  )
  SELECT
    a.eid,
    CASE
      WHEN es.event_id IS NULL THEN 'not_stored'
      WHEN es.origin_service_id IS NULL OR es.origin_service_id = __EMPTY_UUID__::uuid
           OR es.origin_service_id = (SELECT l.service_id FROM local_id l) THEN 'own_event'
      WHEN x.event_id IS NOT NULL THEN 'already_excluded'
      WHEN es.supersede_key IS NOT NULL THEN 'superseding'
      WHEN NULLIF(lower(btrim(COALESCE(p_service_name, ''))), '') IS NOT NULL
           AND __SCHEMA__._wh_first_hop_service(es.event_id, eb.metadata) = lower(btrim(p_service_name)) THEN 'written_here'
      ELSE 'mark'
    END,
    es.origin_service_id,
    COALESCE(es.scope ->> 't', ''),
    es.event_type,
    es.stream_id,
    es.origin_commit_sequence,
    COALESCE(__DIGEST_ELIGIBLE_V2__(es), false),
    EXISTS (SELECT 1 FROM __SCHEMA__.wh_origin_position_unknown u WHERE u.event_id = a.eid)
  FROM asked a
  LEFT JOIN __SCHEMA__.wh_event_store es ON es.event_id = a.eid
  LEFT JOIN __SCHEMA__.wh_event_body eb ON eb.event_id = a.eid
  LEFT JOIN __SCHEMA__.wh_integrity_exclusions x ON x.event_id = a.eid
  ORDER BY a.eid;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_integrity_exclusion_candidates(UUID[], TEXT) IS
'663 (#2588): per asked event, whether it can be excluded (''mark'') or why not: not_stored (#2616), own_event (decision D1), already_excluded, superseding, or written_here (its first hop names this service: 599 returns it to the local lane). Primary-key probes only.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Rewrites the per-origin counts of the given origins from their marks; an origin left with none loses its row.
SELECT __SCHEMA__.drop_all_overloads('_wh_integrity_exclusion_recount');
-- <docs>resilience/integrity-exclusions#metrics</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Summary_CountsEachOriginsMarksByState_AndAnOriginWithNoneHasNoRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_integrity_exclusion_recount(p_origins UUID[], p_now TIMESTAMPTZ)
RETURNS VOID AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_exclusion_origins AS o
    (origin_service_id, excluded_count, pending_count, unknown_count, updated_at)
  SELECT x.origin_service_id, count(*), count(*) FILTER (WHERE x.origin_state = 0),
         count(*) FILTER (WHERE x.origin_state = 2), p_now
  FROM __SCHEMA__.wh_integrity_exclusions x
  WHERE x.origin_service_id = ANY(p_origins)
  GROUP BY x.origin_service_id
  ON CONFLICT (origin_service_id) DO UPDATE
    SET excluded_count = EXCLUDED.excluded_count, pending_count = EXCLUDED.pending_count,
        unknown_count = EXCLUDED.unknown_count, updated_at = EXCLUDED.updated_at;

  DELETE FROM __SCHEMA__.wh_integrity_exclusion_origins o
  WHERE o.origin_service_id = ANY(p_origins)
    AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_integrity_exclusions x WHERE x.origin_service_id = o.origin_service_id);
END;
$$ LANGUAGE plpgsql;

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Marks the asked events that can be excluded, and says per asked event what it did.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclude');
-- <docs>resilience/integrity-exclusions#marking-and-unmarking</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Exclude_RefusesOwnEventsEventsWrittenHereMissingAndSupersedingOnes_AndMarksTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Exclude_TakesTheEventOutOfItsDigestEpochAndHeldFold_RecordsWhereTheOriginHoldsIt_AndForgetsItsLedgerRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Unexclude_IsTheExactInverseOfExclude_AndBothAreLoggedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclude(
  p_event_ids UUID[],
  p_reason TEXT,
  p_requested_by TEXT,
  p_selection_id UUID,
  p_service_name TEXT,
  p_now TIMESTAMPTZ
) RETURNS TABLE(event_id UUID, outcome TEXT, origin_service_id UUID) AS $$
#variable_conflict use_column
DECLARE
  c_flag_integrity_excluded CONSTANT INTEGER := 256;  -- EventFlags.IntegrityExcluded (1 << 8)
  v_ids UUID[];
  v_streams UUID[];
  v_origins UUID[];
BEGIN
  IF NULLIF(btrim(COALESCE(p_reason, '')), '') IS NULL OR NULLIF(btrim(COALESCE(p_requested_by, '')), '') IS NULL THEN
    RAISE EXCEPTION 'wh_integrity_exclude: a reason and who asked are required';
  END IF;

  CREATE TEMP TABLE IF NOT EXISTS _wh_exclusion_batch (
    event_id UUID PRIMARY KEY, outcome TEXT, origin_service_id UUID, tenant_scope TEXT, event_type TEXT, stream_id UUID,
    origin_commit_sequence BIGINT, was_eligible BOOLEAN, disowned BOOLEAN) ON COMMIT DROP;
  TRUNCATE _wh_exclusion_batch;
  INSERT INTO _wh_exclusion_batch SELECT * FROM __SCHEMA__._wh_integrity_exclusion_candidates(p_event_ids, p_service_name);

  SELECT array_agg(b.event_id ORDER BY b.event_id), array_agg(DISTINCT b.stream_id), array_agg(DISTINCT b.origin_service_id)
    INTO v_ids, v_streams, v_origins
  FROM _wh_exclusion_batch b WHERE b.outcome = 'mark';

  IF v_ids IS NOT NULL THEN
    -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
    PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
    FROM (SELECT DISTINCT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY 1) s;

    -- The flag. The held fold's trigger (452) takes a held row out of the held set.
    UPDATE __SCHEMA__.wh_event_store es
    SET flags = COALESCE(es.flags, 0) | c_flag_integrity_excluded
    WHERE es.event_id = ANY(v_ids);

    -- Only a row the rule counted is in a bucket; XOR is self-inverse, so folding its hashes again takes it out.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(b.origin_service_id), array_agg(b.tenant_scope), array_agg(b.event_type), array_agg(b.stream_id),
      array_agg(b.event_id), 'unfold', p_now)
    FROM _wh_exclusion_batch b
    WHERE b.outcome = 'mark' AND b.was_eligible;
    DELETE FROM __SCHEMA__.wh_stream_digests d
    WHERE d.stream_id = ANY(v_streams) AND d.origin_service_id = ANY(v_origins) AND d.event_count <= 0;

    -- The sealed epochs of each origin lane that counted a marked row refold without it (_wh_epoch_buckets reads the
    -- rule). Received lanes only, so no origin generation of this service moves.
    PERFORM __SCHEMA__._wh_refold_epochs_covering(r.lane, r.mn, r.mx)
    FROM (
      SELECT b.origin_service_id AS lane, MIN(b.origin_commit_sequence) AS mn, MAX(b.origin_commit_sequence) AS mx
      FROM _wh_exclusion_batch b
      WHERE b.outcome = 'mark' AND b.was_eligible AND b.origin_commit_sequence IS NOT NULL
      GROUP BY b.origin_service_id
    ) r;

    -- The event is resolved by decision: its recorded unknown answer is kept on the mark, not in the ask's table.
    DELETE FROM __SCHEMA__.wh_origin_position_unknown u WHERE u.event_id = ANY(v_ids);

    INSERT INTO __SCHEMA__.wh_integrity_exclusions
      (event_id, origin_service_id, tenant_scope, event_type, stream_id, origin_state, origin_commit_sequence,
       local_present, reason, marked_by, marked_at, selection_id)
    SELECT b.event_id, b.origin_service_id, b.tenant_scope, b.event_type, b.stream_id,
           CASE WHEN b.origin_commit_sequence IS NOT NULL THEN 1 WHEN b.disowned THEN 2 ELSE 0 END,
           b.origin_commit_sequence, true, btrim(p_reason), btrim(p_requested_by), p_now, p_selection_id
    FROM _wh_exclusion_batch b
    WHERE b.outcome = 'mark';

    -- The repair ledger's rows of the buckets it touched: the next comparison, made after the adjustment, records a
    -- divergence only if one remains, and the repair drain never re-asks for an excluded event in between.
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    USING (
      SELECT DISTINCT b.origin_service_id, b.tenant_scope, b.event_type, b.stream_id FROM _wh_exclusion_batch b WHERE b.outcome = 'mark'
      UNION
      SELECT DISTINCT b.origin_service_id, b.tenant_scope, b.event_type, __EMPTY_UUID__::uuid FROM _wh_exclusion_batch b WHERE b.outcome = 'mark'
    ) k(origin_service_id, tenant_scope, event_type, stream_id)
    WHERE l.origin_service_id = k.origin_service_id AND l.tenant_scope = k.tenant_scope
      AND l.event_type = k.event_type AND l.stream_id = k.stream_id;

    INSERT INTO __SCHEMA__.wh_integrity_exclusion_log
      (logged_at, action, origin_service_id, selection_id, event_count, reason, requested_by)
    SELECT p_now, 'exclude', b.origin_service_id, p_selection_id, count(*)::int, btrim(p_reason), btrim(p_requested_by)
    FROM _wh_exclusion_batch b WHERE b.outcome = 'mark'
    GROUP BY b.origin_service_id;

    PERFORM __SCHEMA__._wh_integrity_exclusion_recount(v_origins, p_now);
  END IF;

  RETURN QUERY
  SELECT b.event_id, CASE WHEN b.outcome = 'mark' THEN 'marked' ELSE b.outcome END, b.origin_service_id
  FROM _wh_exclusion_batch b
  ORDER BY b.event_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclude(UUID[], TEXT, TEXT, UUID, TEXT, TIMESTAMPTZ) IS
'663 (#2588): excludes the asked received events from integrity: sets EventFlags.IntegrityExcluded, unfolds them from their stream digests, refolds the sealed epochs that counted them, records where their origin holds them, removes their unknown answers and their buckets'' ledger rows, logs the mark per origin. Returns per asked event marked or why not.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Reverses marks: the exact inverse of wh_integrity_exclude. Says per asked event whether it was unmarked.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_unexclude');
-- <docs>resilience/integrity-exclusions#marking-and-unmarking</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Unexclude_IsTheExactInverseOfExclude_AndBothAreLoggedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_unexclude(
  p_event_ids UUID[],
  p_reason TEXT,
  p_requested_by TEXT,
  p_selection_id UUID,
  p_now TIMESTAMPTZ
) RETURNS TABLE(event_id UUID, outcome TEXT, origin_service_id UUID) AS $$
#variable_conflict use_column
DECLARE
  c_flag_integrity_excluded CONSTANT INTEGER := 256;  -- EventFlags.IntegrityExcluded (1 << 8)
  v_streams UUID[];
  v_origins UUID[];
BEGIN
  IF NULLIF(btrim(COALESCE(p_reason, '')), '') IS NULL OR NULLIF(btrim(COALESCE(p_requested_by, '')), '') IS NULL THEN
    RAISE EXCEPTION 'wh_integrity_unexclude: a reason and who asked are required';
  END IF;

  CREATE TEMP TABLE IF NOT EXISTS _wh_unexclusion_batch (
    event_id UUID PRIMARY KEY, origin_service_id UUID, tenant_scope TEXT, event_type TEXT, stream_id UUID,
    origin_commit_sequence BIGINT, now_eligible BOOLEAN NOT NULL DEFAULT false) ON COMMIT DROP;
  TRUNCATE _wh_unexclusion_batch;
  INSERT INTO _wh_unexclusion_batch (event_id, origin_service_id, tenant_scope, event_type, stream_id)
  SELECT x.event_id, x.origin_service_id, x.tenant_scope, x.event_type, x.stream_id
  FROM __SCHEMA__.wh_integrity_exclusions x
  WHERE x.event_id = ANY(COALESCE(p_event_ids, ARRAY[]::UUID[]));

  SELECT array_agg(DISTINCT b.stream_id), array_agg(DISTINCT b.origin_service_id) INTO v_streams, v_origins
  FROM _wh_unexclusion_batch b;

  IF v_origins IS NOT NULL THEN
    PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
    FROM (SELECT DISTINCT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY 1) s;

    -- The flag goes; the held fold's trigger (452) puts a held row back in the held set.
    WITH cleared AS (
      UPDATE __SCHEMA__.wh_event_store es
      SET flags = COALESCE(es.flags, 0) & ~c_flag_integrity_excluded
      FROM _wh_unexclusion_batch b
      WHERE es.event_id = b.event_id
      RETURNING es.event_id, es.event_type, es.flags, es.origin_commit_sequence
    )
    UPDATE _wh_unexclusion_batch b
    SET origin_commit_sequence = c.origin_commit_sequence, now_eligible = COALESCE(__DIGEST_ELIGIBLE_V2__(c), false)
    FROM cleared c
    WHERE b.event_id = c.event_id;

    -- Every row the rule counts again joins its bucket, through the one fold helper.
    PERFORM __SCHEMA__._wh_fold_stream_digests(
      array_agg(b.origin_service_id), array_agg(b.tenant_scope), array_agg(b.event_type), array_agg(b.stream_id),
      array_agg(b.event_id), 'fold', p_now)
    FROM _wh_unexclusion_batch b
    WHERE b.now_eligible;

    PERFORM __SCHEMA__._wh_refold_epochs_covering(r.lane, r.mn, r.mx)
    FROM (
      SELECT b.origin_service_id AS lane, MIN(b.origin_commit_sequence) AS mn, MAX(b.origin_commit_sequence) AS mx
      FROM _wh_unexclusion_batch b
      WHERE b.now_eligible AND b.origin_commit_sequence IS NOT NULL
      GROUP BY b.origin_service_id
    ) r;

    -- The buckets changed on this side: whatever the ledger knew of them is stale, and the next comparison decides.
    DELETE FROM __SCHEMA__.wh_integrity_ledger l
    USING (
      SELECT DISTINCT b.origin_service_id, b.tenant_scope, b.event_type, b.stream_id FROM _wh_unexclusion_batch b
      UNION
      SELECT DISTINCT b.origin_service_id, b.tenant_scope, b.event_type, __EMPTY_UUID__::uuid FROM _wh_unexclusion_batch b
    ) k(origin_service_id, tenant_scope, event_type, stream_id)
    WHERE l.origin_service_id = k.origin_service_id AND l.tenant_scope = k.tenant_scope
      AND l.event_type = k.event_type AND l.stream_id = k.stream_id;

    DELETE FROM __SCHEMA__.wh_integrity_exclusions x USING _wh_unexclusion_batch b WHERE x.event_id = b.event_id;

    INSERT INTO __SCHEMA__.wh_integrity_exclusion_log
      (logged_at, action, origin_service_id, selection_id, event_count, reason, requested_by)
    SELECT p_now, 'unexclude', b.origin_service_id, p_selection_id, count(*)::int, btrim(p_reason), btrim(p_requested_by)
    FROM _wh_unexclusion_batch b
    GROUP BY b.origin_service_id;

    PERFORM __SCHEMA__._wh_integrity_exclusion_recount(v_origins, p_now);
  END IF;

  RETURN QUERY
  SELECT a.eid, CASE WHEN b.event_id IS NULL THEN 'not_excluded' ELSE 'unmarked' END, b.origin_service_id
  FROM (SELECT DISTINCT y.eid FROM unnest(COALESCE(p_event_ids, ARRAY[]::UUID[])) AS y(eid)) a
  LEFT JOIN _wh_unexclusion_batch b ON b.event_id = a.eid
  ORDER BY a.eid;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_unexclude(UUID[], TEXT, TEXT, UUID, TIMESTAMPTZ) IS
'663 (#2588): reverses integrity exclusions, the exact inverse of wh_integrity_exclude: clears the flag, folds each event back into its stream digest, refolds the sealed epochs, removes the marks and the buckets'' ledger rows, logs the unmark per origin. Returns per asked event unmarked or not_excluded.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The next batch of a selection's events, walked by event id after p_after. A selection names an origin, event types, a
-- created_at range (from inclusive, until exclusive) and, as a bit set, what is currently unresolved: 1 events the
-- origin answered unknown (wh_origin_position_unknown, recorded before until), 2 events held without a position, 4 the
-- stored events of repair-ledger stream buckets marked unhealable or exhausted (first seen before until). Without the
-- unresolved bits it reads at most p_scan rows of the store's primary key per call, so a sparse selection advances
-- without reading the whole store in one statement; with them it reads those sources' own indexes. Received history
-- only, never a row already excluded.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_select');
-- <docs>resilience/integrity-exclusions#selections</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Select_ByOriginTypeAndTime_WalksInBoundedBatches_AndNeverPicksOwnEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Select_Unresolved_PicksUnknownAnswersHeldRowsAndUnhealedBuckets_BeforeTheCutoffAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_select(
  p_selection JSONB,
  p_after UUID,
  p_limit INTEGER,
  p_scan INTEGER
) RETURNS TABLE(event_ids UUID[], next_after UUID, done BOOLEAN) AS $$
#variable_conflict use_column
DECLARE
  c_unknown_answers CONSTANT INTEGER := 1;  -- IntegrityUnresolved.UnknownAnswers
  c_undecided CONSTANT INTEGER := 2;        -- IntegrityUnresolved.Undecided
  c_unhealed CONSTANT INTEGER := 4;         -- IntegrityUnresolved.Unhealed
  v_origin UUID := (p_selection ->> 'originServiceId')::uuid;
  v_types TEXT[] := CASE WHEN jsonb_typeof(p_selection -> 'eventTypes') = 'array'
                         THEN ARRAY(SELECT jsonb_array_elements_text(p_selection -> 'eventTypes')) END;
  v_from TIMESTAMPTZ := (p_selection ->> 'from')::timestamptz;
  v_until TIMESTAMPTZ := (p_selection ->> 'until')::timestamptz;
  v_unresolved INTEGER := COALESCE((p_selection ->> 'unresolved')::integer, 0);
  v_local UUID := (SELECT sc.service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1);
  v_after UUID := COALESCE(p_after, __EMPTY_UUID__::uuid);
  v_ids UUID[];
  v_last UUID;
  v_read INTEGER;
BEGIN
  IF v_unresolved = 0 THEN
    SELECT array_agg(w.event_id ORDER BY w.event_id) FILTER (WHERE w.picked),
           (array_agg(w.event_id ORDER BY w.event_id DESC))[1], count(*)::int
      INTO v_ids, v_last, v_read
    FROM (
      SELECT es.event_id,
             (es.origin_service_id IS NOT NULL AND es.origin_service_id <> __EMPTY_UUID__::uuid
              AND es.origin_service_id IS DISTINCT FROM v_local
              AND (v_origin IS NULL OR es.origin_service_id = v_origin)
              AND (v_types IS NULL OR es.event_type = ANY(v_types))
              AND (v_from IS NULL OR es.created_at >= v_from)
              AND (v_until IS NULL OR es.created_at < v_until)
              -- not the digest rule: a row already excluded is not picked again
              AND COALESCE(es.flags, 0) & 256 = 0) AS picked
      FROM __SCHEMA__.wh_event_store es
      WHERE es.event_id > v_after
      ORDER BY es.event_id
      LIMIT GREATEST(p_scan, 1)
    ) w;
    RETURN QUERY SELECT COALESCE(v_ids, ARRAY[]::UUID[]), COALESCE(v_last, v_after), COALESCE(v_read, 0) < GREATEST(p_scan, 1);
    RETURN;
  END IF;

  SELECT array_agg(c.event_id ORDER BY c.event_id), (array_agg(c.event_id ORDER BY c.event_id DESC))[1], count(*)::int
    INTO v_ids, v_last, v_read
  FROM (
    SELECT DISTINCT s.event_id
    FROM (
      SELECT u.event_id FROM __SCHEMA__.wh_origin_position_unknown u
      WHERE v_unresolved & c_unknown_answers <> 0 AND u.event_id > v_after
        AND (v_origin IS NULL OR u.origin_service_id = v_origin) AND (v_until IS NULL OR u.recorded_at < v_until)
      UNION ALL
      SELECT es.event_id FROM __SCHEMA__.wh_event_store es
      WHERE v_unresolved & c_undecided <> 0 AND es.event_id > v_after
        AND es.origin_service_id IS NOT NULL AND es.origin_commit_sequence IS NULL
        AND (v_origin IS NULL OR es.origin_service_id = v_origin) AND (v_until IS NULL OR es.created_at < v_until)
      UNION ALL
      SELECT es.event_id
      FROM __SCHEMA__.wh_integrity_ledger l
      JOIN __SCHEMA__.wh_event_store es
        ON es.stream_id = l.stream_id AND es.origin_service_id = l.origin_service_id AND es.event_type = l.event_type
       AND COALESCE(es.scope ->> 't', '') = l.tenant_scope
      WHERE v_unresolved & c_unhealed <> 0 AND es.event_id > v_after
        AND l.stream_id <> __EMPTY_UUID__::uuid
        AND (l.unhealable_at IS NOT NULL OR l.exhausted_at IS NOT NULL)
        AND (v_origin IS NULL OR l.origin_service_id = v_origin) AND (v_until IS NULL OR l.first_seen_at < v_until)
    ) s
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = s.event_id
    WHERE es.origin_service_id IS NOT NULL AND es.origin_service_id <> __EMPTY_UUID__::uuid
      AND es.origin_service_id IS DISTINCT FROM v_local
      AND (v_types IS NULL OR es.event_type = ANY(v_types))
      AND (v_from IS NULL OR es.created_at >= v_from)
      -- not the digest rule: a row already excluded is not picked again
      AND COALESCE(es.flags, 0) & 256 = 0
    ORDER BY s.event_id
    LIMIT GREATEST(p_limit, 1)
  ) c;
  RETURN QUERY SELECT COALESCE(v_ids, ARRAY[]::UUID[]), COALESCE(v_last, v_after), COALESCE(v_read, 0) < GREATEST(p_limit, 1);
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_select(JSONB, UUID, INTEGER, INTEGER) IS
'663 (#2588): the next batch of received events a selection names, after p_after by event id: by origin, types and created_at range, or what is currently unresolved (1 unknown answers, 2 held rows, 4 unhealed ledger stream buckets) before the until time. Returns the ids, where the walk got to, and whether it is done.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Settles pending marks from an origin's position answer: a position makes a mark held (1), an unknown answer makes it
-- unknown (2); an unstamped event stays pending and is asked again. Returns every answered id that is an exclusion of
-- this origin, whatever its state, so the caller leaves those out of the position fill.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_settle');
-- <docs>resilience/integrity-exclusions#where-the-origin-holds-it</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:APendingMark_IsAskedOnce_AndTheAnswerSettlesIt_HeldOrUnknownAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_settle(
  p_origin UUID,
  p_event_ids UUID[],
  p_sequences BIGINT[],
  p_unknown_ids UUID[],
  p_tenants TEXT[],
  p_event_types TEXT[],
  p_stream_ids UUID[],
  p_now TIMESTAMPTZ
) RETURNS UUID[] AS $$
DECLARE
  v_marks UUID[];
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_sequences) THEN
    RAISE EXCEPTION 'wh_integrity_exclusion_settle: % event ids and % sequences must pair by position',
      cardinality(p_event_ids), cardinality(p_sequences);
  END IF;

  SELECT array_agg(x.event_id ORDER BY x.event_id) INTO v_marks
  FROM __SCHEMA__.wh_integrity_exclusions x
  WHERE x.origin_service_id = p_origin
    AND (x.event_id = ANY(COALESCE(p_event_ids, ARRAY[]::UUID[])) OR x.event_id = ANY(COALESCE(p_unknown_ids, ARRAY[]::UUID[])));
  IF v_marks IS NULL THEN
    RETURN ARRAY[]::UUID[];
  END IF;

  -- An origin that names the bucket it holds the event in (OriginPosition's optional fields) is believed: the
  -- adjustment removes the event from the bucket the origin counts it in. An older origin names none.
  UPDATE __SCHEMA__.wh_integrity_exclusions x
  SET origin_state = 1, origin_commit_sequence = m.seq,
      tenant_scope = CASE WHEN m.ty IS NULL THEN x.tenant_scope ELSE COALESCE(m.te, '') END,
      event_type = COALESCE(m.ty, x.event_type),
      stream_id = CASE WHEN m.ty IS NULL THEN x.stream_id ELSE COALESCE(m.st, x.stream_id) END
  FROM unnest(p_event_ids, p_sequences,
              COALESCE(p_tenants, array_fill(NULL::text, ARRAY[cardinality(p_event_ids)])),
              COALESCE(p_event_types, array_fill(NULL::text, ARRAY[cardinality(p_event_ids)])),
              COALESCE(p_stream_ids, array_fill(NULL::uuid, ARRAY[cardinality(p_event_ids)]))) AS m(eid, seq, te, ty, st)
  WHERE x.event_id = m.eid AND x.origin_service_id = p_origin AND x.origin_state = 0 AND m.seq > 0;

  UPDATE __SCHEMA__.wh_integrity_exclusions x
  SET origin_state = 2
  WHERE x.event_id = ANY(COALESCE(p_unknown_ids, ARRAY[]::UUID[])) AND x.origin_service_id = p_origin AND x.origin_state = 0;

  PERFORM __SCHEMA__._wh_integrity_exclusion_recount(ARRAY[p_origin], p_now);
  RETURN v_marks;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_settle(UUID, UUID[], BIGINT[], UUID[], TEXT[], TEXT[], UUID[], TIMESTAMPTZ) IS
'663 (#2588): settles pending integrity exclusions from an origin''s position answer (held at a position, or unknown to the origin). Returns the answered ids that are exclusions of the origin, for the caller to leave out of the position fill.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- The next batch of pending marks to ask one origin about: the origin asked least recently first, its marks not asked
-- within p_lap_pause, at most p_batch_size of them. Marks them asked. One row, or none when nothing is due.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_ask');
-- <docs>resilience/integrity-exclusions#where-the-origin-holds-it</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:APendingMark_IsAskedOnce_AndTheAnswerSettlesIt_HeldOrUnknownAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_ask(
  p_batch_size INTEGER,
  p_lap_pause INTERVAL,
  p_now TIMESTAMPTZ
) RETURNS TABLE(origin_service_id UUID, event_ids UUID[]) AS $$
#variable_conflict use_column
DECLARE
  v_origin UUID;
  v_ids UUID[];
BEGIN
  SELECT x.origin_service_id INTO v_origin
  FROM __SCHEMA__.wh_integrity_exclusions x
  WHERE x.origin_state = 0 AND (x.asked_at IS NULL OR x.asked_at <= p_now - p_lap_pause)
  ORDER BY x.asked_at NULLS FIRST, x.origin_service_id
  LIMIT 1;
  IF v_origin IS NULL THEN
    RETURN;
  END IF;

  WITH due AS (
    SELECT x.event_id
    FROM __SCHEMA__.wh_integrity_exclusions x
    WHERE x.origin_service_id = v_origin AND x.origin_state = 0
      AND (x.asked_at IS NULL OR x.asked_at <= p_now - p_lap_pause)
    ORDER BY x.asked_at NULLS FIRST, x.event_id
    LIMIT GREATEST(p_batch_size, 1)
    FOR UPDATE SKIP LOCKED
  ),
  asked AS (
    UPDATE __SCHEMA__.wh_integrity_exclusions x SET asked_at = p_now
    FROM due d WHERE x.event_id = d.event_id
    RETURNING x.event_id
  )
  SELECT array_agg(a.event_id ORDER BY a.event_id) INTO v_ids FROM asked a;

  IF v_ids IS NULL THEN
    RETURN;
  END IF;
  RETURN QUERY SELECT v_origin, v_ids;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_ask(INTEGER, INTERVAL, TIMESTAMPTZ) IS
'663 (#2588): the next batch of pending integrity exclusions to ask their origin about through RequestOriginPositions, least recently asked first; a mark is asked again after p_lap_pause while its origin has not stamped it.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- What an origin's answer holds that this side excluded, per bucket: the XOR of the marks' lanes and their count, for
-- the marks the origin holds (state 1) inside the window [p_since, p_until) of its commit sequences (no window: all of
-- them), and how many marks of the bucket are still pending (any position, so they leave every window undecided). By
-- stream, or per (tenant, type) with the empty stream. An origin without marks answers no rows from one index probe.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_adjustment');
-- <docs>resilience/integrity-exclusions#cross-service-agreement</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Adjustment_IsTheXorAndCountOfTheHeldMarksInTheWindow_AndCountsPendingOnesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_adjustment(
  p_origin UUID,
  p_event_types TEXT[],
  p_stream_ids UUID[],
  p_since BIGINT,
  p_until BIGINT,
  p_by_stream BOOLEAN
) RETURNS TABLE(tenant_scope TEXT, event_type TEXT, stream_id UUID, digest_lo BIGINT, digest_hi BIGINT, id_lo BIGINT,
                id_hi BIGINT, event_count INTEGER, pending_count INTEGER) AS $$
  SELECT g.tenant_scope, g.event_type, g.stream_id,
         COALESCE(bit_xor(g.l0) FILTER (WHERE g.counted), 0), COALESCE(bit_xor(g.l1) FILTER (WHERE g.counted), 0),
         COALESCE(bit_xor(g.l2) FILTER (WHERE g.counted), 0), COALESCE(bit_xor(g.l3) FILTER (WHERE g.counted), 0),
         (count(*) FILTER (WHERE g.counted))::int, (count(*) FILTER (WHERE g.origin_state = 0))::int
  FROM (
    SELECT x.tenant_scope, x.event_type,
           CASE WHEN p_by_stream THEN x.stream_id ELSE __EMPTY_UUID__::uuid END AS stream_id,
           x.origin_state,
           x.origin_state = 1 AND (p_since IS NULL OR (x.origin_commit_sequence >= p_since
                                                     AND (p_until IS NULL OR x.origin_commit_sequence < p_until))) AS counted,
           __SCHEMA__._wh_digest_lane(x.event_id, 0) AS l0, __SCHEMA__._wh_digest_lane(x.event_id, 1) AS l1,
           __SCHEMA__._wh_digest_lane(x.event_id, 2) AS l2, __SCHEMA__._wh_digest_lane(x.event_id, 3) AS l3
    FROM __SCHEMA__.wh_integrity_exclusions x
    WHERE x.origin_service_id = p_origin
      AND x.origin_state IN (0, 1)
      AND (p_event_types IS NULL OR x.event_type = ANY(p_event_types))
      AND (p_stream_ids IS NULL OR x.stream_id = ANY(p_stream_ids))
  ) g
  GROUP BY g.tenant_scope, g.event_type, g.stream_id
  HAVING count(*) FILTER (WHERE g.counted) > 0 OR count(*) FILTER (WHERE g.origin_state = 0) > 0
  ORDER BY g.tenant_scope, g.event_type, g.stream_id;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_adjustment(UUID, TEXT[], UUID[], BIGINT, BIGINT, BOOLEAN) IS
'663 (#2588): per bucket (by stream, or per tenant and type), what the origin''s answer counts that this side excluded: the XOR of the held marks'' four digest lanes and their count inside the window [p_since, p_until) of origin commit sequences (all of them without one), and the pending marks, which leave the bucket undecided.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- What a request would do, marking nothing: per origin and outcome, how many events. A selection is walked through
-- wh_integrity_exclusion_select until it is done or p_cap events were picked (capped).
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_dry_run');
-- <docs>resilience/integrity-exclusions#a-dry-run-first</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:DryRun_CountsWhatWouldBeMarkedAndRefused_AndMarksNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_dry_run(
  p_event_ids UUID[],
  p_selection JSONB,
  p_service_name TEXT,
  p_cap INTEGER,
  p_scan INTEGER
) RETURNS TABLE(origin_service_id UUID, outcome TEXT, event_count INTEGER, capped BOOLEAN) AS $$
#variable_conflict use_column
DECLARE
  v_ids UUID[] := COALESCE(p_event_ids, ARRAY[]::UUID[]);
  v_batch UUID[];
  v_after UUID;
  v_done BOOLEAN := p_selection IS NULL;
BEGIN
  WHILE NOT v_done AND cardinality(v_ids) < p_cap LOOP
    SELECT s.event_ids, s.next_after, s.done INTO v_batch, v_after, v_done
    FROM __SCHEMA__.wh_integrity_exclusion_select(p_selection, v_after, p_cap - cardinality(v_ids), p_scan) s;
    v_ids := v_ids || v_batch;
  END LOOP;

  RETURN QUERY
  SELECT c.origin_service_id, CASE WHEN c.outcome = 'mark' THEN 'marked' ELSE c.outcome END, count(*)::int,
         NOT v_done OR cardinality(v_ids) > p_cap
  FROM __SCHEMA__._wh_integrity_exclusion_candidates(v_ids[1:p_cap], p_service_name) c
  GROUP BY c.origin_service_id, c.outcome
  ORDER BY c.origin_service_id NULLS FIRST, c.outcome;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_dry_run(UUID[], JSONB, TEXT, INTEGER, INTEGER) IS
'663 (#2588): what excluding the ids or the selection would do, marking nothing: per origin and outcome (marked, or why not), how many events. A selection is walked until done or p_cap events were picked; capped says the counts stop there.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Applies the next batch of the oldest open request: at most p_batch events by ids, by selection, or (unexclude) of an
-- earlier request's marks. Advances the request's cursor and completes it when its walk is done. Returns per origin
-- what the batch did; a request whose batch failed is marked Failed with the error, and the other requests go on.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_apply_batch');
-- <docs>resilience/integrity-exclusions#requests-and-the-maintenance-step</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:ARequest_IsAppliedInBoundedBatchesFromItsCursor_AndCompletesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:AnUnexcludeRequest_ForAnEarlierRequest_UnmarksEveryMarkItMadeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_apply_batch(
  p_batch INTEGER,
  p_scan INTEGER,
  p_service_name TEXT,
  p_now TIMESTAMPTZ
) RETURNS TABLE(request_id UUID, action TEXT, origin_service_id UUID, applied INTEGER, refused INTEGER, done BOOLEAN,
                error TEXT) AS $$
#variable_conflict use_column
DECLARE
  v_request __SCHEMA__.wh_integrity_exclusion_requests%ROWTYPE;
  v_ids UUID[];
  v_next UUID;
  v_done BOOLEAN;
  v_origin UUID := (NULL::uuid);
  v_types TEXT[];
BEGIN
  SELECT * INTO v_request
  FROM __SCHEMA__.wh_integrity_exclusion_requests r
  WHERE r.status = 'Pending'
  ORDER BY r.requested_at, r.request_id
  LIMIT 1
  FOR UPDATE SKIP LOCKED;
  IF v_request.request_id IS NULL THEN
    RETURN;
  END IF;

  BEGIN
    IF v_request.event_ids IS NOT NULL THEN
      SELECT array_agg(u.eid ORDER BY u.eid) INTO v_ids
      FROM (SELECT DISTINCT y.eid FROM unnest(v_request.event_ids) AS y(eid)
            WHERE y.eid > COALESCE(v_request.cursor_event_id, __EMPTY_UUID__::uuid)
            ORDER BY y.eid LIMIT GREATEST(p_batch, 1)) u;
      v_next := v_ids[cardinality(v_ids)];
      v_done := COALESCE(cardinality(v_ids), 0) < GREATEST(p_batch, 1);
    ELSIF v_request.action = 'exclude' THEN
      SELECT s.event_ids, s.next_after, s.done INTO v_ids, v_next, v_done
      FROM __SCHEMA__.wh_integrity_exclusion_select(v_request.selection, v_request.cursor_event_id, p_batch, p_scan) s;
    ELSE
      v_origin := (v_request.selection ->> 'originServiceId')::uuid;
      v_types := CASE WHEN jsonb_typeof(v_request.selection -> 'eventTypes') = 'array'
                      THEN ARRAY(SELECT jsonb_array_elements_text(v_request.selection -> 'eventTypes')) END;
      SELECT array_agg(m.event_id ORDER BY m.event_id) INTO v_ids
      FROM (SELECT x.event_id FROM __SCHEMA__.wh_integrity_exclusions x
            WHERE x.event_id > COALESCE(v_request.cursor_event_id, __EMPTY_UUID__::uuid)
              AND (v_request.of_selection_id IS NULL OR x.selection_id = v_request.of_selection_id)
              AND (v_origin IS NULL OR x.origin_service_id = v_origin)
              AND (v_types IS NULL OR x.event_type = ANY(v_types))
            ORDER BY x.event_id LIMIT GREATEST(p_batch, 1)) m;
      v_next := v_ids[cardinality(v_ids)];
      v_done := COALESCE(cardinality(v_ids), 0) < GREATEST(p_batch, 1);
    END IF;

    CREATE TEMP TABLE IF NOT EXISTS _wh_exclusion_applied (
      event_id UUID, outcome TEXT, origin_service_id UUID) ON COMMIT DROP;
    TRUNCATE _wh_exclusion_applied;
    IF COALESCE(cardinality(v_ids), 0) > 0 THEN
      IF v_request.action = 'exclude' THEN
        INSERT INTO _wh_exclusion_applied
        SELECT e.event_id, e.outcome, e.origin_service_id
        FROM __SCHEMA__.wh_integrity_exclude(v_ids, v_request.reason, v_request.requested_by, v_request.request_id,
                                             p_service_name, p_now) e;
      ELSE
        INSERT INTO _wh_exclusion_applied
        SELECT e.event_id, e.outcome, e.origin_service_id
        FROM __SCHEMA__.wh_integrity_unexclude(v_ids, v_request.reason, v_request.requested_by, v_request.request_id, p_now) e;
      END IF;
    END IF;

    UPDATE __SCHEMA__.wh_integrity_exclusion_requests r
    SET cursor_event_id = COALESCE(v_next, r.cursor_event_id),
        applied = r.applied + (SELECT count(*) FROM _wh_exclusion_applied a WHERE a.outcome IN ('marked', 'unmarked')),
        refused = r.refused + (SELECT count(*) FROM _wh_exclusion_applied a
                               WHERE a.outcome NOT IN ('marked', 'unmarked', 'already_excluded', 'not_excluded')),
        status = CASE WHEN v_done THEN 'Done' ELSE r.status END,
        completed_at = CASE WHEN v_done THEN p_now ELSE r.completed_at END
    WHERE r.request_id = v_request.request_id;

    RETURN QUERY
    SELECT v_request.request_id, v_request.action, g.origin, g.applied, g.refused, v_done, NULL::text
    FROM (
      SELECT a.origin_service_id AS origin,
             (count(*) FILTER (WHERE a.outcome IN ('marked', 'unmarked')))::int AS applied,
             (count(*) FILTER (WHERE a.outcome NOT IN ('marked', 'unmarked', 'already_excluded', 'not_excluded')))::int AS refused
      FROM _wh_exclusion_applied a
      GROUP BY a.origin_service_id
    ) g
    UNION ALL
    SELECT v_request.request_id, v_request.action, NULL::uuid, 0, 0, v_done, NULL::text
    WHERE NOT EXISTS (SELECT 1 FROM _wh_exclusion_applied);
  EXCEPTION WHEN OTHERS THEN
    UPDATE __SCHEMA__.wh_integrity_exclusion_requests r
    SET status = 'Failed', error = SQLERRM, completed_at = p_now
    WHERE r.request_id = v_request.request_id;
    RETURN QUERY SELECT v_request.request_id, v_request.action, NULL::uuid, 0, 0, true, SQLERRM;
  END;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_apply_batch(INTEGER, INTEGER, TEXT, TIMESTAMPTZ) IS
'663 (#2588): applies one bounded batch of the oldest open integrity exclusion request (by ids, by selection, or for unexclude of an earlier request''s marks), advancing its cursor and completing it when done. Returns per origin what the batch did; a batch that fails marks its request Failed with the error.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Per origin, the marks it has by state and when the last was made, and the requests still open.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_summary');
-- <docs>resilience/integrity-exclusions#the-summary</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:Summary_CountsEachOriginsMarksByState_AndAnOriginWithNoneHasNoRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_summary(p_origin UUID)
RETURNS TABLE(origin_service_id UUID, excluded_count BIGINT, pending_count BIGINT, unknown_count BIGINT,
              last_marked_at TIMESTAMPTZ, open_requests BIGINT) AS $$
  SELECT o.origin_service_id, o.excluded_count, o.pending_count, o.unknown_count,
         (SELECT max(x.marked_at) FROM __SCHEMA__.wh_integrity_exclusions x WHERE x.origin_service_id = o.origin_service_id),
         (SELECT count(*) FROM __SCHEMA__.wh_integrity_exclusion_requests r WHERE r.status = 'Pending')
  FROM __SCHEMA__.wh_integrity_exclusion_origins o
  WHERE p_origin IS NULL OR o.origin_service_id = p_origin
  ORDER BY o.origin_service_id;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_summary(UUID) IS
'663 (#2588): per origin (or the one asked), the integrity exclusions it has, how many are pending an answer and how many the origin does not know, when the last was made, and the requests still open.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────────────────
-- Removes the audit log rows and the finished requests older than p_before. Returns how many of each went.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_exclusion_roll_off');
-- <docs>resilience/integrity-exclusions#the-audit-log</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionSqlTests.cs:RollOff_RemovesOnlyLogRowsAndFinishedRequestsOlderThanTheRetentionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_exclusion_roll_off(p_before TIMESTAMPTZ)
RETURNS TABLE(log_rows INTEGER, requests INTEGER) AS $$
DECLARE
  v_log INTEGER;
  v_requests INTEGER;
BEGIN
  DELETE FROM __SCHEMA__.wh_integrity_exclusion_log l WHERE l.logged_at < p_before;
  GET DIAGNOSTICS v_log = ROW_COUNT;
  DELETE FROM __SCHEMA__.wh_integrity_exclusion_requests r WHERE r.status <> 'Pending' AND r.completed_at < p_before;
  GET DIAGNOSTICS v_requests = ROW_COUNT;
  RETURN QUERY SELECT v_log, v_requests;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_exclusion_roll_off(TIMESTAMPTZ) IS
'663 (#2588): removes the integrity exclusion audit log rows and the finished requests older than p_before.';
