-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 580_FencedWritesHoldTheirPartitionEpoch.sql (#2556): the fences go back to their last words before 580
-- (570), which compare the epoch without holding the partition.

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
       WHERE s.message_id = (p_request -> 'inbox_completion' ->> __ENVELOPE_FIELD_MESSAGE_ID__)::UUID
         AND NOT __SCHEMA__.wh_epoch_is_current(s.partition_number, (p_request ->> 'partition_epoch')::BIGINT))
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_handler_commit_is_fenced(JSONB) IS
  '570 (#2556): whether, in map mode, a handler commit''s partition_epoch is no longer the epoch of its inbox row''s '
  'partition, so the whole commit lands nowhere.';

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
  'name none, or whose row is gone); the rest are reported fenced (perspective_event_completion).';

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
  'epoch); the rest are reported fenced under p_kind.';
