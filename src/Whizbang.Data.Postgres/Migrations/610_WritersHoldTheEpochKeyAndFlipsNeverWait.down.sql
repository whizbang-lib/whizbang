-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 610_WritersHoldTheEpochKeyAndFlipsNeverWait.sql (#2577): the epoch is no longer a key column, and each
-- function goes back to its last word before 610.

DROP INDEX IF EXISTS __SCHEMA__.ux_wh_partition_ownership_epoch;


-- ---------------------------------------------------------------------------------------------
-- wh_hold_partition_epoch: its last word before 610 (577).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_hold_partition_epoch');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWritesAreFencedTests.cs:TheSink_AgainstAFlipThatCommitsWhileItWrites_WaitsForIt_AndIsFencedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWriteHoldSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_hold_partition_epoch(
  p_partition_number INTEGER,
  p_epoch BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
  IF p_epoch IS NULL THEN
    RETURN TRUE;
  END IF;
  -- FOR SHARE: held until the write's transaction ends, so the flip's UPDATE waits for it; under READ COMMITTED a row
  -- a flip changed after the snapshot is re-read, and its new epoch no longer matches.
  PERFORM 1
  FROM __SCHEMA__.wh_partition_ownership o
  WHERE o.partition_number = p_partition_number % 10000 AND o.epoch = p_epoch
  FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'partition % is no longer owned at epoch %; the write is fenced', p_partition_number % 10000, p_epoch
      USING ERRCODE = 'WHF02';
  END IF;
  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_hold_partition_epoch(INTEGER, BIGINT) IS
  '577 (#2556): holds a partition at the epoch a write''s work was claimed under, until the write commits: the ownership '
  'row FOR SHARE, which a flip waits for. TRUE for a write that names no epoch; raises WHF02 when the epoch moved on.';

-- ---------------------------------------------------------------------------------------------
-- wh_epoch_is_current: its last word before 610 (580).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_epoch_is_current');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:EpochIsCurrent_OnlyForThePartitionsCurrentEpoch_AndAlwaysForAWriteThatNamesNoneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_epoch_is_current(p_partition_number INTEGER, p_epoch BIGINT)
RETURNS BOOLEAN
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
  IF p_epoch IS NULL THEN
    RETURN TRUE;
  END IF;
  IF p_partition_number IS NULL THEN
    RETURN p_epoch = 0;
  END IF;
  -- 580 (#2556): the partition is held at the epoch until the write's transaction ends, so a flip waits for the write;
  -- one that committed first leaves the row unlocked, and the write is fenced.
  PERFORM 1 FROM __SCHEMA__.wh_partition_ownership o
  WHERE o.partition_number = p_partition_number % 10000 AND o.epoch = p_epoch
  FOR SHARE OF o;
  RETURN FOUND;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_epoch_is_current(INTEGER, BIGINT) IS
  '570 (#2556): whether a write made under p_epoch may still land on the partition: its epoch is still p_epoch (in '
  'either state). TRUE for a write that names no epoch; for work without a partition, only epoch 0. One primary-key probe. 580: the partition is held FOR SHARE at the epoch until the write commits.';

-- ---------------------------------------------------------------------------------------------
-- _wh_fence_outbox_ids: its last word before 610 (580).
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
  -- 580 (#2556): each named partition is held at its claimed epoch until the write's transaction ends, in partition order
  -- (one lock order for every writer). A flip waits for the write; one that committed first leaves the row unlocked here,
  -- and the check below, on a fresh snapshot, fences the write.
  PERFORM 1
  FROM __SCHEMA__.wh_partition_ownership y
  WHERE (y.partition_number, y.epoch) IN (
    SELECT x.partition_number % 10000, i.epoch
    FROM unnest(p_ids, p_epochs) AS i(id, epoch)
    JOIN __SCHEMA__.wh_outbox x ON x.message_id = i.id
    WHERE i.epoch IS NOT NULL AND x.partition_number IS NOT NULL)
  ORDER BY y.partition_number
  FOR SHARE OF y;
  -- An id whose row is gone (completed already) is kept: its write affects nothing either way.
  SELECT COALESCE(array_agg(n.id ORDER BY n.ord) FILTER (WHERE n.kept), ARRAY[]::UUID[]),
         count(*) FILTER (WHERE NOT n.kept)
    INTO v_kept, v_fenced
  FROM (
    -- wh_epoch_is_current's rule, joined rather than called per id (a per-row call is not inlined). Each lookup is a
    -- LATERAL with LIMIT 1, which the planner never flattens, so every id is one primary-key probe of the outbox and
    -- one of the ownership table, whatever the statistics: never a scan of the outbox.
    SELECT i.id, i.ord,
           o.present IS NULL OR i.epoch IS NULL OR (o.partition_number IS NULL AND i.epoch = 0) OR w.hit IS NOT NULL AS kept
    FROM unnest(p_ids, p_epochs) WITH ORDINALITY AS i(id, epoch, ord)
    LEFT JOIN LATERAL (
      SELECT TRUE AS present, x.partition_number FROM __SCHEMA__.wh_outbox x WHERE x.message_id = i.id LIMIT 1) o ON TRUE
    LEFT JOIN LATERAL (
      SELECT TRUE AS hit FROM __SCHEMA__.wh_partition_ownership y
      WHERE y.partition_number = o.partition_number % 10000 AND y.epoch = i.epoch LIMIT 1) w ON TRUE
    WHERE i.id IS NOT NULL
  ) n;
  PERFORM __SCHEMA__._wh_report_fenced('outbox_completion', v_fenced);
  RETURN v_kept;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fence_outbox_ids(UUID[], BIGINT[]) IS
  '570 (#2556), 580: the outbox ids whose row''s partition is still at the epoch named for it by position (or that name none, '
  'or whose row is gone); the rest are reported fenced (outbox_completion). 580: the partitions are held FOR SHARE at their epochs until the write commits.';

-- ---------------------------------------------------------------------------------------------
-- _wh_fence_perspective_work_ids: its last word before 610 (580).
-- ---------------------------------------------------------------------------------------------
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
  -- 580 (#2556): each named partition is held at its claimed epoch until the write's transaction ends, in partition order
  -- (one lock order for every writer). A flip waits for the write; one that committed first leaves the row unlocked here,
  -- and the check below, on a fresh snapshot, fences the write.
  PERFORM 1
  FROM __SCHEMA__.wh_partition_ownership y
  WHERE (y.partition_number, y.epoch) IN (
    SELECT x.partition_number % 10000, i.epoch
    FROM unnest(p_ids, p_epochs) AS i(id, epoch)
    JOIN __SCHEMA__.wh_perspective_events x ON x.event_work_id = i.id
    WHERE i.epoch IS NOT NULL AND x.partition_number IS NOT NULL)
  ORDER BY y.partition_number
  FOR SHARE OF y;
  SELECT COALESCE(array_agg(n.id ORDER BY n.ord) FILTER (WHERE n.kept), ARRAY[]::UUID[]),
         count(*) FILTER (WHERE NOT n.kept)
    INTO v_kept, v_fenced
  FROM (
    -- As _wh_fence_outbox_ids: one primary-key probe of each table per id, never a scan.
    SELECT i.id, i.ord,
           e.present IS NULL OR i.epoch IS NULL OR (e.partition_number IS NULL AND i.epoch = 0) OR w.hit IS NOT NULL AS kept
    FROM unnest(p_ids, p_epochs) WITH ORDINALITY AS i(id, epoch, ord)
    LEFT JOIN LATERAL (
      SELECT TRUE AS present, x.partition_number FROM __SCHEMA__.wh_perspective_events x WHERE x.event_work_id = i.id LIMIT 1) e ON TRUE
    LEFT JOIN LATERAL (
      SELECT TRUE AS hit FROM __SCHEMA__.wh_partition_ownership y
      WHERE y.partition_number = e.partition_number % 10000 AND y.epoch = i.epoch LIMIT 1) w ON TRUE
    WHERE i.id IS NOT NULL
  ) n;
  PERFORM __SCHEMA__._wh_report_fenced('perspective_event_completion', v_fenced);
  RETURN v_kept;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fence_perspective_work_ids(UUID[], BIGINT[]) IS
  '570 (#2556): the perspective work ids whose row''s partition is still at the epoch named for it by position (or that '
  'name none, or whose row is gone); the rest are reported fenced (perspective_event_completion). 580: the partitions are held FOR SHARE at their epochs until the write commits.';

-- ---------------------------------------------------------------------------------------------
-- _wh_fence_items: its last word before 610 (580).
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
  -- 580 (#2556): each named partition is held at its claimed epoch until the write's transaction ends, in partition order
  -- (one lock order for every writer). A flip waits for the write; one that committed first leaves the row unlocked here,
  -- and the check below, on a fresh snapshot, fences the write.
  PERFORM 1
  FROM __SCHEMA__.wh_partition_ownership y
  WHERE (y.partition_number, y.epoch) IN (
    SELECT (i.item ->> 'PartitionNumber')::INTEGER % 10000, (i.item ->> 'PartitionEpoch')::BIGINT
    FROM jsonb_array_elements(p_items) AS i(item)
    WHERE i.item ->> 'PartitionEpoch' IS NOT NULL AND i.item ->> 'PartitionNumber' IS NOT NULL)
  ORDER BY y.partition_number
  FOR SHARE OF y;
  SELECT COALESCE(jsonb_agg(n.item ORDER BY n.ord) FILTER (WHERE n.kept), '[]'::JSONB),
         count(*) FILTER (WHERE NOT n.kept)
    INTO v_kept, v_fenced
  FROM (
    -- wh_epoch_is_current's rule, joined rather than called per item: a per-row call is not inlined.
    SELECT i.item, i.ord,
           t.epoch IS NULL OR (t.partition_number IS NULL AND t.epoch = 0) OR w.hit IS NOT NULL AS kept
    FROM jsonb_array_elements(p_items) WITH ORDINALITY AS i(item, ord)
    CROSS JOIN LATERAL (SELECT (i.item ->> 'PartitionNumber')::INTEGER AS partition_number,
                               (i.item ->> 'PartitionEpoch')::BIGINT AS epoch) t
    LEFT JOIN LATERAL (
      SELECT TRUE AS hit FROM __SCHEMA__.wh_partition_ownership y
      WHERE y.partition_number = t.partition_number % 10000 AND y.epoch = t.epoch LIMIT 1) w ON TRUE
  ) n;
  PERFORM __SCHEMA__._wh_report_fenced(p_kind, v_fenced);
  RETURN v_kept;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fence_items(JSONB, TEXT) IS
  '570 (#2556): the items of a JSON array whose (PartitionNumber, PartitionEpoch) is still current (or that name no '
  'epoch); the rest are reported fenced under p_kind. 580: the partitions are held FOR SHARE at their epochs until the write commits.';

-- ---------------------------------------------------------------------------------------------
-- flush_completions: its last word before 610 (581).
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
  -- 581 (#2557): every partition this flush names is held at its epoch first, in one partition-ordered pass, so the
  -- fences below re-take only rows already held, and a flip, which locks in the same order, cannot deadlock against it.
  IF __SCHEMA__.wh_partition_ownership_by_map() THEN
    PERFORM 1
    FROM __SCHEMA__.wh_partition_ownership y
    WHERE (y.partition_number, y.epoch) IN (
      SELECT x.partition_number % 10000, i.epoch
      FROM unnest(p_outbox_ids, p_outbox_epochs) AS i(id, epoch)
      JOIN __SCHEMA__.wh_outbox x ON x.message_id = i.id
      WHERE i.epoch IS NOT NULL AND x.partition_number IS NOT NULL
      UNION
      SELECT x.partition_number % 10000, i.epoch
      FROM unnest(p_perspective_event_work_ids, p_perspective_event_epochs) AS i(id, epoch)
      JOIN __SCHEMA__.wh_perspective_events x ON x.event_work_id = i.id
      WHERE i.epoch IS NOT NULL AND x.partition_number IS NOT NULL
      UNION
      SELECT (c.item ->> 'PartitionNumber')::INTEGER % 10000, (c.item ->> 'PartitionEpoch')::BIGINT
      FROM jsonb_array_elements(
        CASE WHEN jsonb_typeof(p_perspective_cursors) = 'array' THEN p_perspective_cursors ELSE '[]'::JSONB END) AS c(item)
      WHERE c.item ->> 'PartitionEpoch' IS NOT NULL AND c.item ->> 'PartitionNumber' IS NOT NULL
      UNION
      SELECT (i.item ->> 'PartitionNumber')::INTEGER % 10000, (i.item ->> 'PartitionEpoch')::BIGINT
      FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_failures) = 'array' THEN p_failures ELSE '[]'::JSONB END) AS g(elem)
      CROSS JOIN LATERAL jsonb_array_elements(
        CASE WHEN jsonb_typeof(g.elem->'Items') = 'array' THEN g.elem->'Items' ELSE '[]'::JSONB END) AS i(item)
      WHERE i.item ->> 'PartitionEpoch' IS NOT NULL AND i.item ->> 'PartitionNumber' IS NOT NULL)
    ORDER BY y.partition_number
    FOR SHARE OF y;
  END IF;

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
         -- 579 (#2551): the rest of the run is the rows after the failed one in emission order, as process_outbox_failures
         -- releases them; they are still locked in the canonical (stream_id, created_at, message_id) order below.
         AND t.emission_position > f.emission_position
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
-- commit_handler_batch: its last word before 610 (581).
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
    -- 581 (#2557): every item's partition is held at its epoch first, in one partition-ordered pass, so the per-item
    -- fence below re-takes only rows already held, and a flip cannot deadlock against the batch.
    PERFORM 1
    FROM __SCHEMA__.wh_partition_ownership y
    WHERE (y.partition_number, y.epoch) IN (
      SELECT s.partition_number % 10000, (h.elem ->> 'partition_epoch')::BIGINT
      FROM jsonb_array_elements(p_results) AS h(elem)
      JOIN __SCHEMA__.wh_inbox_state s ON s.message_id = (h.elem -> 'inbox_completion' ->> __ENVELOPE_FIELD_MESSAGE_ID__)::UUID
      WHERE h.elem ? 'partition_epoch'
        AND jsonb_typeof(h.elem -> 'inbox_completion') = 'object'
        AND s.partition_number IS NOT NULL)
    ORDER BY y.partition_number
    FOR SHARE OF y;
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
    -- #573: the fallback is legitimate, the silence was not. Capture WHY Tier 1 failed:
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
  -- pre-Option-D body: preserves the per-handler success/failure isolation
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
    ORDER BY (elem -> 'inbox_completion' ->> __ENVELOPE_FIELD_MESSAGE_ID__) ASC NULLS FIRST,
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
'Two-tier (Option D) batched commit orchestrator. Tier 1: optimistic bulk attempt via commit_handler_batch_bulk: no savepoint overhead on the happy path. Tier 2: on any error, falls back to per-handler SAVEPOINT loop preserving isolated per-handler success/failure reporting. Returns per-handler (handler_id, success, error_message) for the C# flusher. Single fsync at outer commit on the happy path; bulk-then-loop on the rare failure path. 570 (#2556): in map mode, an item whose partition_epoch is no longer its inbox row''s partition''s commits nothing and is returned with tier 0 (fenced).';

-- ---------------------------------------------------------------------------------------------
-- wh_acknowledge_handover: its last word before 610 (581).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_acknowledge_handover');

-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Ack_FlipsToTheNextOwnerAtEpochPlusOne_AndReleasesTheOldOwnersUnstartedRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Ack_UnderAStaleEpoch_OrByAnInstanceThatIsNotTheOwner_FlipsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Ack_OfAnActivePartition_FlipsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Order_TheNewOwnerTakesTheStreamAfterTheOldOwnersCompletedEvent_AndNotBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_acknowledge_handover(
  p_instance_id UUID,
  p_partitions INTEGER[],
  p_epochs BIGINT[]
) RETURNS TABLE(partition_number INTEGER, epoch BIGINT, owner_instance_id UUID)
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
#variable_conflict use_column
DECLARE
  v_partitions INTEGER[];
  v_epochs BIGINT[];
  v_owners UUID[];
BEGIN
  -- 581 (#2557): the rows are locked in partition order first, the order every fenced write holds them in, so the flip
  -- waits for a write holding nothing the write needs from it, and the two cannot deadlock.
  PERFORM 1
  FROM __SCHEMA__.wh_partition_ownership o
  JOIN unnest(p_partitions, p_epochs) AS a(p, e) ON o.partition_number = a.p
  WHERE o.epoch = a.e
    AND o.owner_instance_id = p_instance_id
    AND o.state = 'handing_over'
  ORDER BY o.partition_number
  FOR NO KEY UPDATE OF o;

  -- The flip, fenced on the old epoch and the caller's ownership: only a partition this instance still owns, handing
  -- over at exactly the epoch it names, moves. The ownership rows are written first, then the work rows, the order the
  -- expiry uses too.
  WITH flipped AS (
    UPDATE __SCHEMA__.wh_partition_ownership o
    SET owner_instance_id = o.next_owner_instance_id,
        epoch = o.epoch + 1,
        state = 'active',
        next_owner_instance_id = NULL,
        handover_deadline = NULL
    FROM unnest(p_partitions, p_epochs) AS a(p, e)
    WHERE o.partition_number = a.p
      AND o.epoch = a.e
      AND o.owner_instance_id = p_instance_id
      AND o.state = 'handing_over'
    RETURNING o.partition_number, o.epoch, o.owner_instance_id
  )
  SELECT array_agg(f.partition_number ORDER BY f.partition_number), array_agg(f.epoch ORDER BY f.partition_number),
         array_agg(f.owner_instance_id ORDER BY f.partition_number)
    INTO v_partitions, v_epochs, v_owners
  FROM flipped f;

  -- What the old owner still holds there was never started (it finished its in-flight batch before acknowledging), so
  -- it goes back with its attempt refunded, to be claimed by the new owner.
  PERFORM __SCHEMA__._wh_release_partition_leases(p_instance_id, v_partitions, TRUE);

  RETURN QUERY
  SELECT u.p, u.e, u.o FROM unnest(v_partitions, v_epochs, v_owners) AS u(p, e, o);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_acknowledge_handover(UUID, INTEGER[], BIGINT[]) IS
  '571 (#2557): the old owner acknowledges the handover of the partitions it has finished: each one it owns, handing '
  'over at the epoch named for it, flips to its next owner at epoch + 1, and the caller''s unstarted rows there are '
  'released (attempt refunded). Returns the partitions flipped with their new epoch and owner; anything else flips nothing.';

-- ---------------------------------------------------------------------------------------------
-- wh_complete_expired_handovers: its last word before 610 (571).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_complete_expired_handovers');

-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:ExpiredHandover_PastTheDeadline_TheAssignerFlips_AndReleasesTheOldOwnersRowsWithoutRefundAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:ExpiredHandover_BeforeTheDeadline_FlipsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:ExpiredHandover_ByADeposedAssigner_IsRefusedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_complete_expired_handovers(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT
) RETURNS INTEGER
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_flipped INTEGER := 0;
  v_old RECORD;
BEGIN
  -- Only the current holder of the role, at the epoch it was granted, completes a handover (WHF01 otherwise).
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  FOR v_old IN
    WITH expired AS (
      SELECT x.partition_number, x.owner_instance_id AS old_owner
      FROM __SCHEMA__.wh_partition_ownership x
      WHERE x.state = 'handing_over'
        AND x.handover_deadline <= clock_timestamp()
      ORDER BY x.partition_number
      FOR UPDATE OF x
    ), flipped AS (
      UPDATE __SCHEMA__.wh_partition_ownership o
      SET owner_instance_id = o.next_owner_instance_id,
          epoch = o.epoch + 1,
          state = 'active',
          next_owner_instance_id = NULL,
          handover_deadline = NULL
      FROM expired e
      WHERE o.partition_number = e.partition_number
      RETURNING e.partition_number, e.old_owner
    )
    SELECT f.old_owner, array_agg(f.partition_number ORDER BY f.partition_number) AS partitions
    FROM flipped f
    GROUP BY f.old_owner
    ORDER BY f.old_owner
  LOOP
    -- The old owner may have started these rows, so their attempts stand; its held models are dropped on its side.
    PERFORM __SCHEMA__._wh_release_partition_leases(v_old.old_owner, v_old.partitions, FALSE);
    v_flipped := v_flipped + cardinality(v_old.partitions);
  END LOOP;

  RETURN v_flipped;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_complete_expired_handovers(TEXT, UUID, BIGINT) IS
  '571 (#2557): the partition assigner, fenced by its role epoch, flips every partition whose handover deadline has '
  'passed on the database''s clock to its next owner at epoch + 1 and releases what the old owner held there (attempts '
  'kept). Returns how many partitions it flipped.';

-- ---------------------------------------------------------------------------------------------
-- wh_publish_partition_assignment: its last word before 610 (581).
-- ---------------------------------------------------------------------------------------------
SELECT __SCHEMA__.drop_all_overloads('wh_publish_partition_assignment');

-- <docs>fundamentals/work-coordinator/partition-assignment</docs>
-- <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByTheRoleHolder_WritesTheAssignmentAndAnnouncesItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByADeposedAssigner_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_AgainInTheSameTenure_AdvancesTheRevisionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignmentSqlTests.cs:Publish_ByANewTenure_RestartsTheRevisionAtTheNewEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_WritesOneActiveRowPerPartition_OwnedByTheMembersInTurn_AtEpochOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Republish_AdvancesTheEpochOfExactlyThePartitionsWhoseOwnerChangedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Republish_OfTheSameMembers_LeavesEveryEpochAsItWasAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_WithNoMembers_LeavesTheOwnershipAsItWasAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_ByADeposedAssigner_WritesNoOwnershipAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_InMapMode_LeavesOutAMemberThatDoesNotSupportTheModeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithAMap_StoresIt_AndTheReadReturnsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_WithoutAMap_StoresNone_SoTheClaimsRankByModuloAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_AMapOfAnotherLength_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionMapClaimSqlTests.cs:Publish_AMapNamingAMemberItDoesNotList_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_WritesOwnershipFromTheMap_NotTheResidueAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Republish_OfTheSameMap_WritesNothing_AndAMovedPartitionAdvancesAloneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionOwnershipSqlTests.cs:Publish_InMapMode_AMapNamingAMemberThatDoesNotSupportTheMode_IsRefusedAsync</tests>
-- <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_InMapMode_AMovedPartitionWithALiveOwner_HandsOver_KeepingTheOwnerAndEpochAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_InMapMode_TheDeadlineIsTheOneThePublishNamesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_InMapMode_AMovedPartitionWhoseOwnerIsNotLive_HandsOverWithAnImmediateDeadline_AndTheSweepFlipsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Publish_SwitchOff_MovesAPartitionAtOnce_AsBeforeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Republish_GivingAPartitionBackToItsOwnerWhileHandingOver_CancelsTheHandoverAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Republish_NamingAnotherNextOwnerWhileHandingOver_RedirectsTheHandoverAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionHandoverSqlTests.cs:Claims_APartitionHandingOver_IsClaimableByNeitherSide_UntilTheFlipAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_publish_partition_assignment(
  p_role TEXT,
  p_instance_id UUID,
  p_epoch BIGINT,
  p_members UUID[],
  p_lease INTERVAL,
  -- 564 (#2558): the partition map, 10,000 member indexes. NULL publishes the older shape, whose claimers rank by modulo.
  p_owners SMALLINT[] DEFAULT NULL,
  -- 571 (#2557): how long a moved partition may hand over before the assigner completes the flip itself.
  p_handover_deadline INTERVAL DEFAULT INTERVAL '30 seconds'
) RETURNS BIGINT
LANGUAGE plpgsql
SET timezone = 'UTC'
AS $$
DECLARE
  v_revision BIGINT;
  -- 565: the members and the map as published, and the ones published before, so the ownership table is written only on
  -- a change.
  v_members UUID[] := COALESCE(p_members, ARRAY[]::UUID[]);
  v_previous UUID[];
  v_previous_owners SMALLINT[];
  -- 571 (#2557): whether this service owns by map, and its live instances, read once and only when the ownership changes.
  v_map BOOLEAN;
  v_live UUID[] := ARRAY[]::UUID[];
BEGIN
  -- The fence where the assignment is written: only the current holder of the role, at the epoch it was granted,
  -- may publish. A deposed assigner gets WHF01 and writes nothing. The assert takes the role row FOR SHARE, so a
  -- vote cannot hand the role on between the check and the write below.
  PERFORM __SCHEMA__.wh_assert_role_epoch(p_role, p_instance_id, p_epoch);

  -- 564 (#2558): a map must give every one of the 10,000 partitions to a listed member, or some partition would be
  -- claimed by nobody. Refused before anything is written. One pass over the map, on a publish, never on a claim.
  IF p_owners IS NOT NULL AND (
       cardinality(p_owners) <> 10000
       OR EXISTS (SELECT 1 FROM unnest(p_owners) o
                  WHERE o IS NULL OR o < 0 OR o >= cardinality(v_members))) THEN
    RAISE EXCEPTION 'a partition map must give each of the 10000 partitions to one of the % listed members',
      cardinality(v_members)
      USING ERRCODE = '22023';
  END IF;

  -- 565 (#2555): once the service owns by map, an instance that does not support the mode owns no partition, so an
  -- older instance that joins after the switch owns nothing and claims nothing. A map's indexes name its members, so a
  -- map is never re-indexed here: the candidates leave such an instance out (wh_partition_assignment_candidates), and a
  -- map that names one anyway is refused. A members-only publish (an assigner of an earlier release) leaves it out of
  -- the members, rank order kept.
  IF __SCHEMA__.wh_partition_ownership_by_map() THEN
    IF p_owners IS NULL THEN
      SELECT COALESCE(array_agg(m.instance_id ORDER BY m.ordinal), ARRAY[]::UUID[])
      INTO v_members
      FROM unnest(v_members) WITH ORDINALITY AS m(instance_id, ordinal)
      WHERE EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = m.instance_id AND __SCHEMA__.wh_supports_ownership_by_map(si.metadata));
    ELSIF EXISTS (
      SELECT 1 FROM unnest(v_members) AS m(instance_id)
      WHERE NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = m.instance_id AND __SCHEMA__.wh_supports_ownership_by_map(si.metadata))) THEN
      RAISE EXCEPTION 'a partition map published under ownership by map must name only instances that support it'
        USING ERRCODE = '22023';
    END IF;
  END IF;

  SELECT pa.members, pa.partition_owners INTO v_previous, v_previous_owners
  FROM __SCHEMA__.wh_published_partition_assignment pa WHERE pa.singleton;

  INSERT INTO __SCHEMA__.wh_published_partition_assignment AS pa
    (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at,
     partition_owners)
  VALUES (TRUE, p_role, p_epoch, 1, p_instance_id, v_members, clock_timestamp(), p_lease, clock_timestamp() + p_lease,
          p_owners)
  ON CONFLICT (singleton) DO UPDATE SET
    role = EXCLUDED.role,
    epoch = EXCLUDED.epoch,
    revision = CASE WHEN pa.epoch = EXCLUDED.epoch THEN pa.revision + 1 ELSE 1 END,
    assigner_instance_id = EXCLUDED.assigner_instance_id,
    members = EXCLUDED.members,
    published_at = EXCLUDED.published_at,
    lease = EXCLUDED.lease,
    lease_expires_at = EXCLUDED.lease_expires_at,
    partition_owners = EXCLUDED.partition_owners
  -- Epochs only rise, so an assignment never goes back to an earlier tenure's.
  WHERE pa.epoch <= EXCLUDED.epoch
  RETURNING pa.revision INTO v_revision;

  IF v_revision IS NULL THEN
    RAISE EXCEPTION 'partition assignment at epoch % is older than the published one', p_epoch
      USING ERRCODE = 'WHF01';
  END IF;

  -- 565 (#2555): the ownership table follows the map, written in this statement so the role epoch fences it too.
  -- Partition p is owned by the member the map gives it (members[owners[p + 1] + 1], #2558); a members-only publish
  -- states no map, and its claimers rank by modulo, so p is owned by members[p mod n], the first map's answer either way.
  -- Only a partition whose owner changes is written, and its epoch advances by one; a new partition starts at epoch 1.
  -- A map with no members owns nothing new, so the table keeps its owners (none of them is live, so none claims) until a
  -- map with members is published. The 10,000 rows are compared only when the members or the map changed: a backstop
  -- republish of the same map writes nothing.
  IF cardinality(v_members) > 0
     AND (v_previous IS DISTINCT FROM v_members
          OR v_previous_owners IS DISTINCT FROM p_owners
          OR NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_partition_ownership o WHERE o.partition_number = 0)) THEN
    -- 571 (#2557): in map mode a partition moving away from a live owner hands over instead of moving at once: it goes
    -- 'handing_over' with the next owner and a deadline, keeping its owner and epoch, until the old owner acknowledges
    -- (wh_acknowledge_handover) or the deadline passes (wh_complete_expired_handovers). A partition handing over that the
    -- map gives back to its owner returns to 'active'; one the map gives to yet another instance is redirected, its
    -- deadline kept. An owner that is not live finishes nothing, so its partitions move at once, epoch raised. Off, every
    -- move is at once, as 565 wrote it.
    v_map := __SCHEMA__.wh_partition_ownership_by_map();
    IF v_map THEN
      SELECT COALESCE(array_agg(l.instance_id), ARRAY[]::UUID[]) INTO v_live FROM __SCHEMA__.wh_live_instance_ids() l;
    END IF;

    INSERT INTO __SCHEMA__.wh_partition_ownership AS o (partition_number, owner_instance_id, epoch, state)
    SELECT g.p, v_members[1 + COALESCE(p_owners[g.p + 1]::INTEGER, g.p % cardinality(v_members))], 1, 'active'
    FROM generate_series(0, 9999) AS g(p)
    -- 581 (#2557): in partition order, so the rows are locked in the order every fenced write holds them in.
    ORDER BY g.p
    ON CONFLICT (partition_number) DO UPDATE SET
      owner_instance_id = CASE WHEN v_map AND o.owner_instance_id = ANY(v_live) THEN o.owner_instance_id
                               ELSE EXCLUDED.owner_instance_id END,
      epoch = CASE WHEN o.owner_instance_id = EXCLUDED.owner_instance_id OR (v_map AND o.owner_instance_id = ANY(v_live))
                   THEN o.epoch ELSE o.epoch + 1 END,
      state = CASE WHEN o.owner_instance_id <> EXCLUDED.owner_instance_id AND v_map AND o.owner_instance_id = ANY(v_live)
                   THEN 'handing_over' ELSE 'active' END,
      next_owner_instance_id = CASE WHEN o.owner_instance_id <> EXCLUDED.owner_instance_id AND v_map
                                         AND o.owner_instance_id = ANY(v_live)
                                    THEN EXCLUDED.owner_instance_id END,
      handover_deadline = CASE WHEN o.owner_instance_id <> EXCLUDED.owner_instance_id AND v_map
                                    AND o.owner_instance_id = ANY(v_live)
                               THEN CASE WHEN o.state = 'handing_over' THEN o.handover_deadline
                                         ELSE clock_timestamp() + p_handover_deadline END END
    WHERE (o.owner_instance_id IS DISTINCT FROM EXCLUDED.owner_instance_id OR o.state = 'handing_over')
      -- A partition already handing over to the owner the map names is left as it is.
      AND NOT (o.state = 'handing_over'
               AND o.next_owner_instance_id = EXCLUDED.owner_instance_id
               AND v_map
               AND o.owner_instance_id = ANY(v_live));
  END IF;

  -- Claimers cache the assignment and refresh it on this; the payload says which version to expect.
  -- 330 (#2046): published on the signal bus. A publish is a decision: recorded in the durable signal log and
  -- announced on wh_partition_assignment at commit, the channel and payload an older release listens for.
  PERFORM __SCHEMA__.wh_publish_signal(
    'wh_partition_assignment', p_epoch::TEXT || ':' || v_revision::TEXT, 'partition-assignment-published');
  RETURN v_revision;
END;
$$;
COMMENT ON FUNCTION __SCHEMA__.wh_publish_partition_assignment(TEXT, UUID, BIGINT, UUID[], INTERVAL, SMALLINT[], INTERVAL) IS
  '203 (#1254): publishes the partition assignment. Fenced by wh_assert_role_epoch (WHF01 for a deposed assigner); '
  'the revision advances within an epoch and restarts at 1 for a new one; an older epoch is refused. Returns the '
  'revision. 330 (#2046): announces epoch:revision on the signal bus (wh_partition_assignment), recorded durably. '
  '564 (#2558): stores the partition map (p_owners), refused with 22023 unless it gives each of the 10,000 partitions '
  'to a listed member; NULL publishes the older shape, whose claimers rank by modulo. 565 (#2555): writes '
  'wh_partition_ownership from the map (members[owners[p + 1] + 1], or members[p mod n] with no map), the epoch of '
  'each changed partition advancing and nothing written when neither the members nor the map changed; once the service '
  'owns by map, a members-only publish leaves out members that do not support the mode and a map naming one is refused. 571 (#2557): in map mode a partition moving away from a live owner hands over (state handing_over, next owner, deadline p_handover_deadline) instead of moving at once; one given back to its owner returns to active; an owner that is not live loses its partitions at once.';
