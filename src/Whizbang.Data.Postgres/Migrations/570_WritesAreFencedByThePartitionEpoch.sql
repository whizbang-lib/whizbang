-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 570_WritesAreFencedByThePartitionEpoch.sql
-- Date: 2026-11-23
-- Description: Every write that commits a result is fenced by the partition epoch it was made under (#2556; phase 4 of
--              #2540).
--
--              A partition's epoch rises by one each time its owner changes (565). An instance that claims work learns
--              the epoch it holds the partition at (wh_held_epoch, read in the claim's own statement), carries it with the
--              work, and names it on every write that commits a result: an outbox completion or publish mark, a
--              perspective work completion or cursor advance, a failure, and a handler's commit. The write lands only
--              while that epoch is still the partition's (wh_epoch_is_current, one primary-key probe). Otherwise it
--              affects no row, the work stays for the current owner, and the write is reported, never silent: a
--              NOTICE 'whizbang.fenced' with DETAIL '<kind>=<count>' per kind, which the coordinator turns into the
--              fenced metric and a debug log naming what was fenced.
--
--              * flush_completions and complete_outbox_published take the epochs of their outbox ids by position;
--                complete_perspective those of its work ids; a cursor and a failure item name their own
--                (PartitionNumber, PartitionEpoch); a handler commit names partition_epoch, checked against its inbox
--                row's partition, and a fenced one commits nothing, neither the completion nor what the handler emitted.
--                commit_handler_batch returns a fenced item with tier 0, distinct from a failure.
--              * The epoch is still current while the partition hands over (565's 'handing_over' keeps the owner and
--                the epoch), so the old owner finishes its in-flight batch; the flip that ends the handover raises the
--                epoch, and every later write of the old owner is fenced.
--              * A write that names no epoch (an older caller, an inline path that claimed nothing) is not fenced, and
--                with the service's switch off (wh_partition_ownership_by_map) nothing is read and every write is
--                exactly as before.
--              * The perspective row-version check is untouched: it protects concurrent edits, the epoch protects
--                ownership.
--
--              Cost: off, or naming no epoch, nothing more than the switch's one probe of wh_settings. On, one primary-key
--              probe of wh_partition_ownership per id named, inside the call that writes it; the rows' partitions are
--              read by the same primary-key lookups the writes make.
--
-- Reversal: reversible (the down file restores each function's previous last word and drops the helpers)
-- Dependencies: 565 (wh_partition_ownership, wh_partition_ownership_by_map), 245 (flush_completions), 241
--               (complete_outbox_published), 374 (complete_perspective), 369 (commit_handler_result), 029
--               (commit_handler_batch)
-- Objects: wh_held_epoch, wh_epoch_is_current, _wh_report_fenced, _wh_fence_outbox_ids, _wh_fence_perspective_work_ids,
--          _wh_fence_items, _wh_fence_failures, _wh_handler_commit_is_fenced, flush_completions, complete_outbox_published,
--          complete_perspective, commit_handler_result, commit_handler_batch
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_OUTBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- ---------------------------------------------------------------------------------------------
-- wh_held_epoch: the epoch a claim learns.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_held_epoch');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:HeldEpoch_IsTheOwnersEpochInEitherState_AndMinusOneForAnyoneElseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:HeldEpoch_WithoutAPartition_IsZero_AndAPartitionPastTheTable_IsReadThroughItsRemainderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:HeldEpoch_WithTheSwitchOff_IsNullAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_held_epoch(p_partition_number INTEGER, p_instance_id UUID)
RETURNS BIGINT
LANGUAGE sql
STABLE
AS $$
  -- The owner holds its epoch in either state: while handing over it still finishes its batch, and its writes land
  -- until the flip raises the epoch. -1 is no tenure at all, which no write can match.
  SELECT CASE
    WHEN NOT __SCHEMA__.wh_partition_ownership_by_map() THEN NULL::BIGINT
    WHEN p_partition_number IS NULL THEN 0::BIGINT
    ELSE COALESCE((
      SELECT o.epoch
      FROM __SCHEMA__.wh_partition_ownership o
      WHERE o.partition_number = p_partition_number % 10000
        AND o.owner_instance_id = p_instance_id), -1::BIGINT)
  END
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_held_epoch(INTEGER, UUID) IS
  '570 (#2556): the epoch at which p_instance_id owns the partition, active or handing over; -1 when it does not own it; '
  '0 for work without a partition; NULL with the service''s switch off. A claim reads it in its own statement and the '
  'worker names it on every write that commits a result.';

-- ---------------------------------------------------------------------------------------------
-- wh_epoch_is_current: the fence.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_epoch_is_current');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:EpochIsCurrent_OnlyForThePartitionsCurrentEpoch_AndAlwaysForAWriteThatNamesNoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_epoch_is_current(p_partition_number INTEGER, p_epoch BIGINT)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  SELECT CASE
    WHEN p_epoch IS NULL THEN TRUE
    WHEN p_partition_number IS NULL THEN p_epoch = 0
    ELSE EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_partition_ownership o
      WHERE o.partition_number = p_partition_number % 10000 AND o.epoch = p_epoch)
  END
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_epoch_is_current(INTEGER, BIGINT) IS
  '570 (#2556): whether a write made under p_epoch may still land on the partition: its epoch is still p_epoch (in '
  'either state). TRUE for a write that names no epoch; for work without a partition, only epoch 0. One primary-key probe.';

-- ---------------------------------------------------------------------------------------------
-- _wh_report_fenced: a fenced write is never silent.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_report_fenced');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AnOutboxCompletionUnderAStaleEpoch_AffectsNoRow_AndIsReportedFencedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_report_fenced(p_kind TEXT, p_count INTEGER)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  IF p_count > 0 THEN
    RAISE NOTICE 'whizbang.fenced' USING DETAIL = p_kind || '=' || p_count;
  END IF;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_report_fenced(TEXT, INTEGER) IS
  '570 (#2556): raises NOTICE whizbang.fenced with DETAIL kind=count when a write fenced anything; the coordinator '
  'counts it (the fenced metric) and logs what was fenced.';

-- ---------------------------------------------------------------------------------------------
-- _wh_fence_outbox_ids, _wh_fence_perspective_work_ids: the ids whose epoch is still current.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_fence_outbox_ids');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AnOutboxCompletionUnderAStaleEpoch_AffectsNoRow_AndIsReportedFencedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:PublishMark_UnderAStaleEpoch_AffectsNoRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fence_outbox_ids(p_ids UUID[], p_epochs BIGINT[])
RETURNS UUID[]
LANGUAGE plpgsql
AS $$
DECLARE
  v_kept UUID[];
  v_fenced INTEGER;
BEGIN
  IF p_ids IS NULL OR p_epochs IS NULL THEN
    RETURN p_ids;
  END IF;
  -- An id whose row is gone (completed already) is kept: its write affects nothing either way.
  SELECT COALESCE(array_agg(n.id ORDER BY n.ord) FILTER (WHERE n.kept), ARRAY[]::UUID[]),
         count(*) FILTER (WHERE NOT n.kept)
    INTO v_kept, v_fenced
  FROM (
    SELECT i.id, i.ord,
           o.message_id IS NULL OR __SCHEMA__.wh_epoch_is_current(o.partition_number, i.epoch) AS kept
    FROM unnest(p_ids, p_epochs) WITH ORDINALITY AS i(id, epoch, ord)
    LEFT JOIN __SCHEMA__.wh_outbox o ON o.message_id = i.id
    WHERE i.id IS NOT NULL
  ) n;
  PERFORM __SCHEMA__._wh_report_fenced('outbox_completion', v_fenced);
  RETURN v_kept;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fence_outbox_ids(UUID[], BIGINT[]) IS
  '570 (#2556): the outbox ids whose row''s partition is still at the epoch named for it by position (or that name none, '
  'or whose row is gone); the rest are reported fenced (outbox_completion).';

SELECT __SCHEMA__.drop_all_overloads('_wh_fence_perspective_work_ids');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_APerspectiveEventCompletionUnderAStaleEpoch_AffectsNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:CompletePerspective_UnderAStaleEpoch_AffectsNoRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fence_perspective_work_ids(p_ids UUID[], p_epochs BIGINT[])
RETURNS UUID[]
LANGUAGE plpgsql
AS $$
DECLARE
  v_kept UUID[];
  v_fenced INTEGER;
BEGIN
  IF p_ids IS NULL OR p_epochs IS NULL THEN
    RETURN p_ids;
  END IF;
  SELECT COALESCE(array_agg(n.id ORDER BY n.ord) FILTER (WHERE n.kept), ARRAY[]::UUID[]),
         count(*) FILTER (WHERE NOT n.kept)
    INTO v_kept, v_fenced
  FROM (
    SELECT i.id, i.ord,
           e.event_work_id IS NULL OR __SCHEMA__.wh_epoch_is_current(e.partition_number, i.epoch) AS kept
    FROM unnest(p_ids, p_epochs) WITH ORDINALITY AS i(id, epoch, ord)
    LEFT JOIN __SCHEMA__.wh_perspective_events e ON e.event_work_id = i.id
    WHERE i.id IS NOT NULL
  ) n;
  PERFORM __SCHEMA__._wh_report_fenced('perspective_event_completion', v_fenced);
  RETURN v_kept;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fence_perspective_work_ids(UUID[], BIGINT[]) IS
  '570 (#2556): the perspective work ids whose row''s partition is still at the epoch named for it by position (or that '
  'name none, or whose row is gone); the rest are reported fenced (perspective_event_completion).';

-- ---------------------------------------------------------------------------------------------
-- _wh_fence_items, _wh_fence_failures: JSON items that name their own (PartitionNumber, PartitionEpoch).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_fence_items');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_ACursorAdvanceUnderAStaleEpoch_LeavesTheCursorAsItWasAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_ACursorAdvanceUnderTheCurrentEpoch_AdvancesAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AFailureUnderAStaleEpoch_IsNotRecordedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fence_items(p_items JSONB, p_kind TEXT)
RETURNS JSONB
LANGUAGE plpgsql
AS $$
DECLARE
  v_kept JSONB;
  v_fenced INTEGER;
BEGIN
  IF p_items IS NULL OR jsonb_typeof(p_items) <> 'array' THEN
    RETURN p_items;
  END IF;
  SELECT COALESCE(jsonb_agg(n.item ORDER BY n.ord) FILTER (WHERE n.kept), '[]'::JSONB),
         count(*) FILTER (WHERE NOT n.kept)
    INTO v_kept, v_fenced
  FROM (
    SELECT i.item, i.ord,
           __SCHEMA__.wh_epoch_is_current((i.item ->> 'PartitionNumber')::INTEGER, (i.item ->> 'PartitionEpoch')::BIGINT) AS kept
    FROM jsonb_array_elements(p_items) WITH ORDINALITY AS i(item, ord)
  ) n;
  PERFORM __SCHEMA__._wh_report_fenced(p_kind, v_fenced);
  RETURN v_kept;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fence_items(JSONB, TEXT) IS
  '570 (#2556): the items of a JSON array whose (PartitionNumber, PartitionEpoch) is still current (or that name no '
  'epoch); the rest are reported fenced under p_kind.';

SELECT __SCHEMA__.drop_all_overloads('_wh_fence_failures');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AFailureUnderAStaleEpoch_IsNotRecordedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fence_failures(p_failures JSONB)
RETURNS JSONB
LANGUAGE sql
AS $$
  -- Each category's items fenced under '<category>_failure'; a group left with no item is dropped.
  SELECT COALESCE(jsonb_agg(f.grp ORDER BY f.ord) FILTER (WHERE jsonb_array_length(f.grp -> 'Items') > 0
                                                            OR jsonb_typeof(f.grp -> 'Items') IS DISTINCT FROM 'array'),
                  '[]'::JSONB)
  FROM (
    SELECT CASE WHEN jsonb_typeof(g.elem -> 'Items') = 'array'
                THEN jsonb_set(g.elem, '{Items}',
                               __SCHEMA__._wh_fence_items(g.elem -> 'Items', (g.elem ->> 'Category') || '_failure'))
                ELSE g.elem END AS grp,
           g.ord
    FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS g(elem, ord)
  ) f
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fence_failures(JSONB) IS
  '570 (#2556): the failure groups with each item whose (PartitionNumber, PartitionEpoch) is no longer current left out '
  'and reported fenced (<category>_failure); a group left empty is dropped.';

-- ---------------------------------------------------------------------------------------------
-- _wh_handler_commit_is_fenced: whether a handler's commit names a stale epoch for its inbox row.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('_wh_handler_commit_is_fenced');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:HandlerCommit_UnderAStaleEpoch_CommitsNothing_NeitherTheCompletionNorWhatTheHandlerEmittedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:HandlerBatch_AStaleItemIsReturnedFenced_AndTheRestCommitAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_handler_commit_is_fenced(p_request JSONB)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
  -- Fenced only in map mode, for a request that names an epoch and completes an inbox row that is still there on a
  -- partition whose epoch has moved on. A row already gone is no one's to fence, as before.
  SELECT p_request ? 'partition_epoch'
     AND jsonb_typeof(p_request -> 'inbox_completion') = 'object'
     AND __SCHEMA__.wh_partition_ownership_by_map()
     AND EXISTS (
       SELECT 1 FROM __SCHEMA__.wh_inbox_state s
       WHERE s.message_id = (p_request -> 'inbox_completion' ->> 'MessageId')::UUID
         AND NOT __SCHEMA__.wh_epoch_is_current(s.partition_number, (p_request ->> 'partition_epoch')::BIGINT))
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_handler_commit_is_fenced(JSONB) IS
  '570 (#2556): whether, in map mode, a handler commit''s partition_epoch is no longer the epoch of its inbox row''s '
  'partition, so the whole commit lands nowhere.';

-- ---------------------------------------------------------------------------------------------
-- flush_completions: 245's definition, with the 570 delta: the epochs and the fence.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('flush_completions');

-- <docs>fundamentals/work-coordinator/batched-flushers</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FlushCompletionsSqlTests.cs:FlushCompletions_OutboxAndPerspective_AppliesBothInOneCallAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:FlushCompletions_FailingARowAnotherTransactionCompletesFirst_NeverDeadlocksAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:FlushCompletions_ReleasingARunAnotherTransactionCompletesFirst_NeverDeadlocksAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AnOutboxCompletionUnderAStaleEpoch_AffectsNoRow_AndIsReportedFencedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AnOutboxCompletionUnderTheCurrentEpoch_CompletesAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_APerspectiveEventCompletionUnderAStaleEpoch_AffectsNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_ACursorAdvanceUnderAStaleEpoch_LeavesTheCursorAsItWasAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_AFailureUnderAStaleEpoch_IsNotRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:SwitchOff_AnEpochIsIgnored_AndTheWriteLandsAsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:MapMode_AWriteThatNamesNoEpoch_LandsAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.flush_completions(
  p_outbox_ids UUID[],
  p_perspective_cursors JSONB,
  p_perspective_event_work_ids UUID[],
  p_failures JSONB,          -- [{Category: the work category, Items: [...]}]
  -- 570 (#2556): the partition epoch each outbox id and each perspective work id was claimed under, by position. NULL
  -- (an older caller, an inline path) names none, and nothing is fenced.
  p_outbox_epochs BIGINT[] DEFAULT NULL,
  p_perspective_event_epochs BIGINT[] DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_failure_group RECORD;
  -- 245: the failed outbox rows' ids, read from the request without touching a table.
  v_outbox_failed UUID[];
  v_has_failures BOOLEAN := p_failures IS NOT NULL AND jsonb_array_length(p_failures) > 0;
BEGIN
  -- 570 (#2556): in map mode a completion or failure made under a partition epoch that is no longer current lands
  -- nowhere: it is dropped from the request before any row is written, and counted (whizbang.fenced). Perspective work
  -- and cursors are fenced by complete_perspective, which receives the epochs. Off, or naming no epoch, nothing changes.
  IF (p_outbox_epochs IS NOT NULL OR v_has_failures) AND __SCHEMA__.wh_partition_ownership_by_map() THEN
    p_outbox_ids := __SCHEMA__._wh_fence_outbox_ids(p_outbox_ids, p_outbox_epochs);
    IF v_has_failures THEN
      p_failures := __SCHEMA__._wh_fence_failures(p_failures);
      v_has_failures := jsonb_array_length(p_failures) > 0;
    END IF;
  END IF;

  IF v_has_failures THEN
    SELECT array_agg((i.item->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID)
      INTO v_outbox_failed
      FROM jsonb_array_elements(p_failures) AS g(elem)
      CROSS JOIN LATERAL jsonb_array_elements(
        CASE WHEN jsonb_typeof(g.elem->'Items') = 'array' THEN g.elem->'Items' ELSE '[]'::JSONB END) AS i(item)
     WHERE g.elem->>'Category' = __CATEGORY_OUTBOX__
       AND i.item->>__ENVELOPE_FIELD_MESSAGE_ID__ IS NOT NULL;
  END IF;

  -- 245: every outbox row the steps below write, locked in one (stream_id, created_at, message_id) pass before any of
  -- them is written. kind 1: a completion, which complete_outbox_published writes only while the row is open. kind 2:
  -- a failed row, which process_outbox_failures writes whatever its state. kind 3: the rest of a failed row's run,
  -- which process_outbox_failures releases while its holder still leases it and it is pending.
  IF array_length(p_outbox_ids, 1) IS NOT NULL OR v_outbox_failed IS NOT NULL THEN
    PERFORM 1
    FROM (
      SELECT o.message_id
      FROM __SCHEMA__.wh_outbox o
      JOIN (
        SELECT c.id AS k_id, 1 AS k_kind, NULL::UUID AS k_holder
        FROM unnest(p_outbox_ids) AS c(id)
        UNION ALL
        SELECT f.id, 2, NULL::UUID
        FROM unnest(v_outbox_failed) AS f(id)
        UNION ALL
        SELECT t.message_id, 3, f.instance_id
        FROM __SCHEMA__.wh_outbox f
        JOIN __SCHEMA__.wh_outbox t
          ON t.stream_id = f.stream_id
         AND (t.created_at, t.message_id) > (f.created_at, f.message_id)
         AND t.instance_id = f.instance_id
        WHERE f.message_id = ANY(v_outbox_failed)
          AND f.instance_id IS NOT NULL
          AND f.stream_id IS NOT NULL
          AND f.stream_id <> __EMPTY_UUID__::uuid
          AND NOT (f.metadata->>'scheduleId' IS NOT NULL
                   AND COALESCE((f.metadata->>'deliveryGuarantee')::INTEGER, 0) = 1)
          AND t.processed_at IS NULL
          AND t.published_at IS NULL
          AND t.coalesce_group IS NULL
      ) k ON o.message_id = k.k_id
      WHERE (k.k_kind = 1 AND o.processed_at IS NULL)
         OR k.k_kind = 2
         OR (k.k_kind = 3
             AND o.instance_id = k.k_holder
             AND o.processed_at IS NULL
             AND o.published_at IS NULL
             AND o.coalesce_group IS NULL)
      ORDER BY o.stream_id, o.created_at, o.message_id
      FOR UPDATE OF o
    ) locked;
  END IF;

  IF p_outbox_ids IS NOT NULL AND array_length(p_outbox_ids, 1) IS NOT NULL THEN
    PERFORM __SCHEMA__.complete_outbox_published(p_outbox_ids);
  END IF;

  -- 245: the failures in the canonical table order, outbox then inbox, before the perspective work.
  IF v_has_failures THEN
    FOR v_failure_group IN
      SELECT elem->>'Category' AS category, elem->'Items' AS items
      FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS e(elem, ord)
      WHERE elem->>'Category' IN (__CATEGORY_OUTBOX__, __CATEGORY_INBOX__)
      ORDER BY (elem->>'Category' = __CATEGORY_INBOX__), ord
    LOOP
      PERFORM __SCHEMA__.report_failures(v_failure_group.category, v_failure_group.items);
    END LOOP;
  END IF;

  IF (p_perspective_cursors IS NOT NULL AND jsonb_array_length(p_perspective_cursors) > 0)
     OR (p_perspective_event_work_ids IS NOT NULL AND array_length(p_perspective_event_work_ids, 1) IS NOT NULL) THEN
    PERFORM __SCHEMA__.complete_perspective(
      COALESCE(p_perspective_cursors, '[]'::JSONB),
      COALESCE(p_perspective_event_work_ids, ARRAY[]::UUID[]),
      FALSE,
      -- 570 (#2556): the work ids' epochs; the cursors carry their own.
      p_perspective_event_epochs
    );
  END IF;

  -- Perspective failures, and any category report_failures does not know (it raises, as before).
  IF v_has_failures THEN
    FOR v_failure_group IN
      SELECT elem->>'Category' AS category, elem->'Items' AS items
      FROM jsonb_array_elements(p_failures) WITH ORDINALITY AS e(elem, ord)
      WHERE elem->>'Category' IS DISTINCT FROM __CATEGORY_OUTBOX__
        AND elem->>'Category' IS DISTINCT FROM __CATEGORY_INBOX__
      ORDER BY ord
    LOOP
      PERFORM __SCHEMA__.report_failures(v_failure_group.category, v_failure_group.items);
    END LOOP;
  END IF;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.flush_completions(UUID[], JSONB, UUID[], JSONB, BIGINT[], BIGINT[]) IS
'Composite single-round-trip flusher: complete_outbox_published + report_failures (per category) + complete_perspective in one transaction. 245 (#1785): every outbox row it writes (completed rows, failed rows, the runs the failures release) is locked first in one (stream_id, created_at, message_id) pass, and the steps run in the canonical table order (outbox, inbox, perspective work), so it cannot deadlock against another writer that takes outbox rows in that order. 570 (#2556): in map mode, a completion, failure or cursor named under a partition epoch that is no longer current is dropped before anything is written and reported as fenced (NOTICE whizbang.fenced, DETAIL kind=count).';

-- ---------------------------------------------------------------------------------------------
-- complete_outbox_published: 241's definition, with the 570 delta.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('complete_outbox_published');

-- <docs>fundamentals/work-coordinator/per-stream-drain</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:CompleteOutboxPublished_DeletesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:CompleteOutboxPublished_InDebugMode_UpdatesItsRowsInStreamOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CompleteOutboxPublishedSqlTests.cs:CompleteOutboxPublished_Production_DeletesRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredOutboxDeadLetterPublishTests.cs:ARecoveredOutboxEvent_IsPublishedAsTheEventItWasStoredAs_AndReadsRecoveredOncePublishedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:PublishMark_UnderAStaleEpoch_AffectsNoRowAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.complete_outbox_published(
  p_ids UUID[],
  p_debug_mode BOOLEAN DEFAULT FALSE,
  -- 570 (#2556): the partition epoch each id was claimed under, by position; NULL names none.
  p_epochs BIGINT[] DEFAULT NULL
) RETURNS INTEGER AS $$
DECLARE
  v_affected INTEGER;
  v_recovered UUID[];         -- 241: the dead letters the completed rows were recovered from
  v_dead_letter_id UUID;
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN 0;
  END IF;

  -- 570 (#2556): in map mode a publish mark made under a partition epoch that is no longer current marks nothing.
  IF p_epochs IS NOT NULL AND __SCHEMA__.wh_partition_ownership_by_map() THEN
    p_ids := __SCHEMA__._wh_fence_outbox_ids(p_ids, p_epochs);
  END IF;

  IF p_debug_mode THEN
    -- Debug mode: retain the row for forensics; stamp published_at + processed_at
    -- and set the Published bit. eligible_outbox filters published_at IS NULL so
    -- claim_work treats the row as if deleted on subsequent polls.
    -- 241: counted and collected from the statement's own rows, so the settle below reads nothing again.
    WITH completed AS (
      UPDATE __SCHEMA__.wh_outbox o
      SET processed_at = NOW(),
          published_at = NOW(),
          status = o.status | 4,         -- Published flag
          instance_id = NULL,
          lease_expiry = NULL
      FROM (
        -- 172: locked in (stream_id, created_at, message_id) order; see the production branch.
        SELECT l.message_id
        FROM __SCHEMA__.wh_outbox l
        WHERE l.message_id = ANY(p_ids)
          AND l.processed_at IS NULL
        ORDER BY l.stream_id, l.created_at, l.message_id
        FOR UPDATE OF l
      ) locked
      WHERE o.message_id = locked.message_id
        AND o.processed_at IS NULL
      RETURNING o.recovered_from_dead_letter_id
    )
    SELECT count(*), array_agg(c.recovered_from_dead_letter_id) FILTER (WHERE c.recovered_from_dead_letter_id IS NOT NULL)
    INTO v_affected, v_recovered
    FROM completed c;
  ELSE
    -- Production mode: row exits the table on success, structurally immune to
    -- claim_work re-issuing it on the next poll cycle.
    --
    -- 172: the rows are locked in (stream_id, created_at, message_id) order before they are deleted.
    -- The bare DELETE locked them in primary-key order, which is not the order any other writer of
    -- the outbox uses; a completion flush that also reports failures holds these rows while
    -- process_outbox_failures takes more, so the two passes must run the same way. Primary-key
    -- lookups, then a sort of the batch: priced by the batch.
    WITH completed AS (
      DELETE FROM __SCHEMA__.wh_outbox o
      USING (
        SELECT l.message_id
        FROM __SCHEMA__.wh_outbox l
        WHERE l.message_id = ANY(p_ids)
          AND l.processed_at IS NULL
        ORDER BY l.stream_id, l.created_at, l.message_id
        FOR UPDATE OF l
      ) locked
      WHERE o.message_id = locked.message_id
        AND o.processed_at IS NULL
      RETURNING o.recovered_from_dead_letter_id
    )
    SELECT count(*), array_agg(c.recovered_from_dead_letter_id) FILTER (WHERE c.recovered_from_dead_letter_id IS NOT NULL)
    INTO v_affected, v_recovered
    FROM completed c;
  END IF;

  -- 241 (#1722): a recovered message that has been published settles the dead letter it re-entered from (Recovered).
  -- After the outbox rows, so the outbox keeps its place first in the lock order.
  IF v_recovered IS NOT NULL THEN
    FOREACH v_dead_letter_id IN ARRAY v_recovered LOOP
      PERFORM __SCHEMA__._wh_settle_recovered_dead_letter(v_dead_letter_id, TRUE, NOW());
    END LOOP;
  END IF;

  RETURN v_affected;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.complete_outbox_published(UUID[], BOOLEAN, BIGINT[]) IS
'Completes outbox rows after successful transport publish. Production (p_debug_mode=FALSE, default): DELETEs the row, structurally immune to claim_work re-issuing it. Debug mode (p_debug_mode=TRUE): retains the row, stamps published_at + processed_at + Published bit; eligible_outbox filters published_at IS NULL so claim_work skips it. Coalesced + batched by C# OutboxCompletionFlushWorker. Idempotent: unknown ids silently no-op. Returns rows-affected (deletions in prod, updates in debug). 172: rows are locked in (stream_id, created_at, message_id) order. 241 (#1722): a completed row recovered from a dead letter settles that dead letter Recovered. 570 (#2556): in map mode, an id named under a partition epoch that is no longer current is not marked, and is reported as fenced.';

-- ---------------------------------------------------------------------------------------------
-- complete_perspective: 374's definition, with the 570 delta.
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('complete_perspective');

-- <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_TheCursorFlushedBeforeItsWork_ClearsTheRewindFlagWhenTheWorkIsRetiredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveRewindFlagSettleSqlTests.cs:CompletePerspective_DebugMode_ClearsTheRewindFlagWhenTheLastRowIsStampedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Cursors/PerspectiveCursorAdvanceSqlTests.cs:CompletePerspective_ProductionMode_AdvancesAnExistingCursorAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:CompletePerspective_UnderAStaleEpoch_AffectsNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:Flush_ACursorAdvanceUnderTheCurrentEpoch_AdvancesAsBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.complete_perspective(
  p_cursors JSONB,                          -- [{StreamId, PerspectiveName}] for cursor advancement
  p_event_work_ids UUID[],                  -- event_work_id rows to mark complete
  p_debug_mode BOOLEAN DEFAULT FALSE,       -- production: DELETE rows; debug: retain with processed_at stamped
  -- 570 (#2556): the partition epoch each work id was claimed under, by position; NULL names none. A cursor names its
  -- own (PartitionNumber, PartitionEpoch).
  p_event_work_epochs BIGINT[] DEFAULT NULL
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_completions JSONB;
  v_retired_streams UUID[];
  v_retired_perspectives TEXT[];
BEGIN
  -- 570 (#2556): in map mode a work completion or cursor named under a partition epoch that is no longer current lands
  -- nowhere, and is reported as fenced.
  IF (p_event_work_epochs IS NOT NULL OR (p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0))
     AND __SCHEMA__.wh_partition_ownership_by_map() THEN
    p_event_work_ids := __SCHEMA__._wh_fence_perspective_work_ids(p_event_work_ids, p_event_work_epochs);
    p_cursors := __SCHEMA__._wh_fence_items(p_cursors, 'perspective_cursor');
  END IF;

  -- Build the JSONB array of completions from the work-id list. StatusFlags = 1 = Completed.
  IF p_event_work_ids IS NOT NULL AND array_length(p_event_work_ids, 1) IS NOT NULL THEN
    SELECT jsonb_agg(jsonb_build_object('EventWorkId', wid, 'StatusFlags', 1))
    INTO v_completions
    FROM unnest(p_event_work_ids) AS wid;

    -- 374: the retired rows' pairs are kept, so a flagged cursor whose completion an earlier flush carried can settle.
    SELECT array_agg(r.stream_id), array_agg(r.perspective_name::TEXT)
    INTO v_retired_streams, v_retired_perspectives
    FROM __SCHEMA__.process_perspective_event_completions(
      COALESCE(v_completions, '[]'::JSONB),
      v_now,
      p_debug_mode  -- production: DELETE; debug: stamp processed_at and retain
    ) r;
  END IF;

  -- Advance cursors for the (StreamId, PerspectiveName) pairs in p_cursors.
  IF p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0 THEN
    PERFORM __SCHEMA__.update_perspective_cursors(p_cursors, p_debug_mode);
  END IF;

  -- 374: a pair whose last pending row this flush retired has nothing left for a rewind to replay. When its cursor's
  -- completion came in an earlier flush, update_perspective_cursors saw the row pending and kept RewindRequired; settle
  -- it now. A pair with work still pending keeps its flag.
  PERFORM __SCHEMA__._wh_settle_rewind_flags(v_retired_streams, v_retired_perspectives);

  -- Slice 27: routed NOTIFY for downstream wakeups (e.g., perspective-sync awaiters
  -- watching for cursor advancement). Resolve stream IDs from the cursor payload and
  -- route to each stream's owning instance only. Polling backstop catches streams
  -- whose ownership isn't currently known in wh_active_streams.
  IF p_cursors IS NOT NULL AND jsonb_array_length(p_cursors) > 0 THEN
    DECLARE
      v_cursor_stream_ids UUID[];
    BEGIN
      SELECT array_agg(DISTINCT (elem ->> __ENVELOPE_FIELD_STREAM_ID__)::UUID)
      INTO v_cursor_stream_ids
      FROM jsonb_array_elements(p_cursors) AS elem
      WHERE elem ->> __ENVELOPE_FIELD_STREAM_ID__ IS NOT NULL;

      IF v_cursor_stream_ids IS NOT NULL THEN
        PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_cursor_stream_ids);
      END IF;
    END;
  END IF;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.complete_perspective IS
'Batched perspective completion. Production (p_debug_mode=FALSE, default): DELETEs event-work rows and advances cursors. Debug (p_debug_mode=TRUE): retains rows with processed_at stamped (eligible_perspective_events filters processed_at IS NULL → row treated as deleted by claim_work). Single round-trip. Coalesced flush from C# PerspectiveCompletionFlushWorker. Rings the owners of the cursors'' streams so peers know cursors moved. 374: a retired pair with nothing left pending settles a cursor still flagged RewindRequired, whichever flush carried its completion. 570 (#2556): in map mode, a work id or cursor named under a partition epoch that is no longer current lands nowhere and is reported as fenced.';

-- ---------------------------------------------------------------------------------------------
-- commit_handler_result: 369's definition, with the 570 delta. Signature unchanged.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/work-coordinator/handler-commit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:CommitHandlerResult_AChildOnAStreamWithNoPendingWork_RingsItsOwnerOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:CommitHandlerResult_AChildAlreadySeen_RingsNoOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceiveDoorbellCostSqlTests.cs:CommitHandlerResult_AChildOnAStreamWithPendingWork_RingsNoOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HappyPath_MarksInboxProcessedAndStoresOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HandlerEmitsEvent_StoresInEventStoreAndCreatesPerspectiveEventAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:HandlerCommit_UnderAStaleEpoch_CommitsNothing_NeitherTheCompletionNorWhatTheHandlerEmittedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_result(
  p_request JSONB
) RETURNS VOID AS $$
DECLARE
  v_instance_id UUID := (p_request ->> 'instance_id')::UUID;
  v_now TIMESTAMPTZ := NOW();
  v_lease_expiry TIMESTAMPTZ := v_now + INTERVAL '300 seconds';
  v_partition_count INTEGER := COALESCE((p_request ->> 'partition_count')::INTEGER, 10000);
  -- Production: completions DELETE rows. Debug: retain with processed_at stamped.
  -- Caller (C# OutboxDrainWorker / InboxDrainWorker / completion flushers) reads from
  -- IWorkCoordinatorStrategy.DebugMode and sets this on the request.
  v_debug_mode BOOLEAN := COALESCE((p_request ->> 'debug_mode')::BOOLEAN, FALSE);
  v_inbox_completion JSONB := p_request -> 'inbox_completion';
  v_new_outbox JSONB := COALESCE(p_request -> 'new_outbox_messages', '[]'::JSONB);
  v_new_inbox JSONB := COALESCE(p_request -> 'new_inbox_messages', '[]'::JSONB);
  v_outbox_inserted INTEGER := 0;
  -- Slice 27: instance-routed NOTIFY. Collect stream IDs from the new outbox/inbox
  -- payloads so we can route NOTIFYs only to the owning instance(s) of those streams
  -- instead of broadcasting on a global wh_work channel.
  v_outbox_stream_ids UUID[];
BEGIN
  -- 570 (#2556): a handler's result is one write. In map mode, when the inbox row it completes is on a partition whose
  -- epoch has moved on from the one the request names, nothing commits (neither the completion nor what the handler
  -- emitted) and it is reported as fenced; the row stays for the current owner.
  IF __SCHEMA__._wh_handler_commit_is_fenced(p_request) THEN
    PERFORM __SCHEMA__._wh_report_fenced('handler_commit', 1);
    RETURN;
  END IF;

  -- 1. Mark inbox completion (if present). process_inbox_completions takes a JSONB array;
  --    wrap the single completion in an array.
  IF v_inbox_completion IS NOT NULL AND v_inbox_completion::TEXT != 'null' THEN
    PERFORM __SCHEMA__.process_inbox_completions(
      jsonb_build_array(v_inbox_completion),
      v_now,
      v_debug_mode  -- production: DELETE; debug: stamp processed_at and retain
    );
  END IF;

  -- 2. Store new outbox messages emitted by the handler.
  IF jsonb_array_length(v_new_outbox) > 0 THEN
    PERFORM __SCHEMA__.store_outbox_messages(
      v_new_outbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
    v_outbox_inserted := jsonb_array_length(v_new_outbox);

    -- Collect stream_ids for slice 27's routed NOTIFY.
    SELECT array_agg(DISTINCT (elem ->> __ENVELOPE_FIELD_STREAM_ID__)::UUID)
    INTO v_outbox_stream_ids
    FROM jsonb_array_elements(v_new_outbox) AS elem
    WHERE elem ->> __ENVELOPE_FIELD_STREAM_ID__ IS NOT NULL;

    -- 2.5. Event-store auto-create chain. For events with stream_id, copy into
    -- wh_event_store and auto-create wh_perspective_events rows for matching
    -- perspectives. Without this step, handler-emitted events sit in wh_outbox
    -- but never reach perspective consumers.
    DECLARE
      v_emitted_ids UUID[];
    BEGIN
      SELECT array_agg((elem ->> __ENVELOPE_FIELD_MESSAGE_ID__)::UUID)
      INTO v_emitted_ids
      FROM jsonb_array_elements(v_new_outbox) AS elem
      WHERE (elem ->> 'IsEvent')::BOOLEAN = true
        AND elem ->> __ENVELOPE_FIELD_STREAM_ID__ IS NOT NULL;

      IF v_emitted_ids IS NOT NULL AND cardinality(v_emitted_ids) > 0 THEN
        PERFORM __SCHEMA__._emit_event_store_chain(
          v_emitted_ids, v_instance_id, v_lease_expiry, v_now, v_partition_count
        );
      END IF;
    END;
  END IF;

  -- 3. Store new inbox messages emitted by the handler (a composite's children, a redelivery bundle's among them).
  --    369: the store rings exactly the streams that need it (114: a stream with no pending work; a duplicate is no
  --    new work), as on every other inbox path, so the commit rings nothing more for them.
  IF jsonb_array_length(v_new_inbox) > 0 THEN
    PERFORM __SCHEMA__.store_inbox_messages(
      v_new_inbox,
      v_instance_id,
      v_lease_expiry,
      v_now,
      v_partition_count
    );
  END IF;

  -- 4. NOTIFY signal types via slice 27's instance-routed helper. Listeners on each
  --    instance subscribe to wh_work_i_<their_instance_id> only; non-owners never
  --    wake. pg_notify still deduplicates (channel, payload) within a transaction at
  --    COMMIT, so 10 000 inserts targeting the same owner collapse to one delivered
  --    notification per (owner, category) pair. Streams without a known owner in
  --    wh_active_streams emit zero NOTIFYs; polling backstop catches that work.
  IF v_outbox_inserted > 0 AND v_outbox_stream_ids IS NOT NULL THEN
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_OUTBOX__, v_outbox_stream_ids);
    -- Event-store auto-create chain may have queued perspective events for the same
    -- streams. Route the perspective wake to the same owners.
    PERFORM __SCHEMA__.notify_instance_owners(__CATEGORY_PERSPECTIVE__, v_outbox_stream_ids);
  END IF;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.commit_handler_result(JSONB) IS
'Atomic transactional bundle for inbox handler completion. Marks the inbox completion AND stores the new outbox/inbox messages the handler emitted in one transaction; if any step fails the whole bundle rolls back. Handler-emitted events with a stream_id flow through _emit_event_store_chain which copies them into wh_event_store and creates matching wh_perspective_events rows so perspective consumers see them. Rings the owners of the outbox streams (outbox and perspective). 369: new inbox messages are rung by store_inbox_messages alone, which rings only a stream with no pending work and never a duplicate; the commit ringing every inbox stream again doubled the ring and woke owners for duplicates and busy streams. Phase A of work-pump decomposition. 570 (#2556): in map mode, a request whose partition_epoch is no longer the inbox row''s partition''s commits nothing and is reported as fenced.';

-- ---------------------------------------------------------------------------------------------
-- commit_handler_batch: 029's definition, with the 570 delta. Signature unchanged.
-- ---------------------------------------------------------------------------------------------

-- <docs>fundamentals/work-coordinator/handler-commit</docs>
-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:HandlerBatch_AStaleItemIsReturnedFenced_AndTheRestCommitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerBatchSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.commit_handler_batch(
  p_results JSONB
) RETURNS TABLE(
  handler_id UUID,
  success BOOLEAN,
  error_message TEXT,
  tier INTEGER,
  bulk_error TEXT
) AS $$
DECLARE
  r RECORD;
  v_handler_id UUID;
  v_bulk_state TEXT;
  v_bulk_msg TEXT;
  v_bulk_error TEXT;
  -- 570 (#2556): how many items were fenced.
  v_fenced INTEGER;
BEGIN
  -- 570 (#2556): in map mode an item whose partition epoch has moved on commits nothing. It is returned with tier 0, the
  -- fenced outcome (not a failure), and left out of both tiers below.
  IF jsonb_array_length(p_results) > 0 AND __SCHEMA__.wh_partition_ownership_by_map() THEN
    RETURN QUERY
    SELECT (f.elem ->> 'handler_id')::UUID, FALSE, 'fenced: the partition''s ownership has moved on'::TEXT, 0, NULL::TEXT
    FROM jsonb_array_elements(p_results) AS f(elem)
    WHERE __SCHEMA__._wh_handler_commit_is_fenced(f.elem);
    GET DIAGNOSTICS v_fenced = ROW_COUNT;
    IF v_fenced > 0 THEN
      PERFORM __SCHEMA__._wh_report_fenced('handler_commit', v_fenced);
      SELECT COALESCE(jsonb_agg(k.elem ORDER BY k.ord), '[]'::JSONB) INTO p_results
      FROM jsonb_array_elements(p_results) WITH ORDINALITY AS k(elem, ord)
      WHERE NOT __SCHEMA__._wh_handler_commit_is_fenced(k.elem);
    END IF;
  END IF;

  IF jsonb_array_length(p_results) = 0 THEN
    RETURN;
  END IF;

  -- Tier 1: optimistic bulk attempt. Subtransaction scope means a raise inside
  -- commit_handler_batch_bulk rolls back cleanly without aborting the outer
  -- transaction; control falls through to the savepoint loop.
  BEGIN
    PERFORM __SCHEMA__.commit_handler_batch_bulk(p_results);
    RETURN QUERY
    SELECT (elem ->> 'handler_id')::UUID AS handler_id,
           TRUE                          AS success,
           NULL::TEXT                    AS error_message,
           1                             AS tier,
           NULL::TEXT                    AS bulk_error
    FROM jsonb_array_elements(p_results) AS elem;
    RETURN;
  EXCEPTION WHEN OTHERS THEN
    -- #573: the fallback is legitimate, the silence was not. Capture WHY Tier 1 failed —
    -- a deployment running permanently on the slow per-handler path was indistinguishable
    -- from a healthy one, and the diagnosis (the SQLSTATE) was discarded on every call.
    -- The reason rides on every Tier-2 row (the C# caller logs and counts it) and lands
    -- durably in wh_log for after-the-fact forensics.
    GET STACKED DIAGNOSTICS v_bulk_state = RETURNED_SQLSTATE, v_bulk_msg = MESSAGE_TEXT;
    v_bulk_error := v_bulk_state || ': ' || v_bulk_msg;
    PERFORM __SCHEMA__.log_event(2, 'commit_handler_batch',
      'bulk tier failed; falling back to per-handler savepoints: ' || v_bulk_error);
  END;

  -- Tier 2: per-handler SAVEPOINT loop (rare path). Identical to the
  -- pre-Option-D body — preserves the per-handler success/failure isolation
  -- contract for the C# flusher.
  -- Same deterministic lock order as Tier 1 (see commit_handler_batch_bulk). The fallback loop takes
  -- the same row locks, so it must take them in the same sequence; sorting only the fast path would
  -- leave the rare path free to deadlock against a concurrent bulk batch.
  --
  -- Reordering is safe for the caller: the flusher maps results by the handler_id carried in each
  -- returned row (`rows.Select(r => new HandlerBatchResult(r.HandlerId, ...))`), never positionally,
  -- so per-handler success/failure reporting is unaffected by emission order.
  FOR r IN
    SELECT elem
    FROM jsonb_array_elements(p_results) AS elem
    ORDER BY (elem -> 'inbox_completion' ->> 'MessageId') ASC NULLS FIRST,
             (elem ->> 'handler_id') ASC NULLS FIRST
  LOOP
    v_handler_id := (r.elem ->> 'handler_id')::UUID;

    BEGIN
      -- Implicit SAVEPOINT scope: any exception inside this BEGIN..EXCEPTION block
      -- rolls back ONLY this iteration's writes, then control jumps to the EXCEPTION
      -- branch and the loop continues with the next handler.
      PERFORM __SCHEMA__.commit_handler_result(r.elem);
      RETURN QUERY SELECT v_handler_id AS handler_id, TRUE AS success, NULL::TEXT AS error_message,
                          2 AS tier, v_bulk_error AS bulk_error;
    EXCEPTION WHEN OTHERS THEN
      RETURN QUERY SELECT v_handler_id AS handler_id, FALSE AS success, SQLERRM::TEXT AS error_message,
                          2 AS tier, v_bulk_error AS bulk_error;
    END;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
COMMENT ON FUNCTION __SCHEMA__.commit_handler_batch(JSONB) IS
'Two-tier (Option D) batched commit orchestrator. Tier 1: optimistic bulk attempt via commit_handler_batch_bulk — no savepoint overhead on the happy path. Tier 2: on any error, falls back to per-handler SAVEPOINT loop preserving isolated per-handler success/failure reporting. Returns per-handler (handler_id, success, error_message) for the C# flusher. Single fsync at outer commit on the happy path; bulk-then-loop on the rare failure path. 570 (#2556): in map mode, an item whose partition_epoch is no longer its inbox row''s partition''s commits nothing and is returned with tier 0 (fenced).';
