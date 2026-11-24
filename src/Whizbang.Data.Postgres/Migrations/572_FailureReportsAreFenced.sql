-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 572_FailureReportsAreFenced.sql
-- Date: 2026-11-24
-- Description: A failure reported on its own is fenced by the partition epoch its work was claimed under, as one
--              reported through flush_completions already is (#2556, phase 4 of #2540).
--
--              Workers report failures through report_failures directly as well as through the composite flush. Its
--              signature is kept: its earlier definitions clear no overloads, so a second parameter list beside them
--              would leave two functions of one name after a replay (OverloadGuardsCoverEveryDefinitionSiteTests). The
--              fenced report is its own function. In map mode it leaves out every failure whose (PartitionNumber,
--              PartitionEpoch) is no longer current, reports them fenced ('<category>_failure'), and hands the rest to
--              report_failures. Off, or naming no epoch, it is report_failures.
--
--              Cost: off, one probe of wh_settings; on, one primary-key probe of wh_partition_ownership per failure.
--
-- Reversal: reversible (the down file drops the function)
-- Dependencies: 029 (report_failures), 565 (wh_partition_ownership_by_map), 570 (_wh_fence_items)
-- Objects: wh_report_failures_fenced

SELECT __SCHEMA__.drop_all_overloads('wh_report_failures_fenced');

-- <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorNamesTheClaimedEpochTests.cs:AFailure_OfWorkFetchedBeforeThePartitionMoved_IsNotRecordedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OwnershipFencingSqlTests.cs:ReportedFailure_UnderAStaleEpoch_IsNotRecorded_AndOneThatNamesNoEpochIsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_report_failures_fenced(
  p_category TEXT,
  p_failures JSONB
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  IF jsonb_typeof(p_failures) = 'array' AND jsonb_array_length(p_failures) > 0
     AND __SCHEMA__.wh_partition_ownership_by_map() THEN
    p_failures := __SCHEMA__._wh_fence_items(p_failures, p_category || '_failure');
    IF jsonb_array_length(p_failures) = 0 THEN
      RETURN;
    END IF;
  END IF;
  PERFORM __SCHEMA__.report_failures(p_category, p_failures);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_report_failures_fenced(TEXT, JSONB) IS
  '572 (#2556): report_failures for failures that name the partition epoch their work was claimed under: in map mode a '
  'failure whose epoch is no longer its partition''s is left out and reported fenced (<category>_failure); the rest '
  'are reported as report_failures reports them.';
