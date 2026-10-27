-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 245_FlushCompletionsLocksItsOutboxRowsInOnePass.sql
-- Date: 2026-10-26
-- Description: flush_completions takes every outbox row it will write in one sorted pass, before it writes any of
--              them (#1785).
--
--              flush_completions (029) completes outbox rows, then completes perspective work, then reports each
--              category's failures, all in one transaction. Each outbox step locks its rows in the canonical
--              (stream_id, created_at, message_id) order (172), but they are two passes: complete_outbox_published
--              takes the completed rows, and process_outbox_failures later takes the failed rows and the rest of their
--              runs. Two ascending passes in one transaction are not one order (rule 5 in
--              ai-docs/load-under-bulk-import.md). With R0 before R1 in that order:
--                * this flush completes R1 (holding it) and fails R0;
--                * another transaction that takes R0 and then R1 (another instance's completion of both) holds R0
--                  and waits on R1;
--                * this flush's failure step then waits on R0, and the two deadlock.
--              It is reachable on the at-least-once re-lease path: a row completed by two instances, or failed by one
--              and completed by another. The flush workers (OutboxCompletionFlushWorker, FailureFlushWorker) send one
--              statement per transaction and are not affected; flush_completions is public on both drivers.
--
--              Now the function first locks, in one statement ordered by (stream_id, created_at, message_id), every
--              outbox row its steps will write: the completed rows (still open), the failed rows, and the rest of each
--              failed row's run that process_outbox_failures releases (the later rows of its stream its holder still
--              leases; none for an at-most-once schedule occurrence, which is parked without a release). Each lock
--              re-asserts the predicate that chose the row. The steps then write rows the transaction already holds.
--              The key set is the batch's ids plus one idx_outbox_stream_run range scan per failed row, so the pass is
--              priced by the batch, never the table.
--
--              The steps also run in the canonical table order now: outbox completions and outbox failures, then inbox
--              failures, then perspective completions and perspective failures. Before, outbox and inbox failures ran
--              after the perspective completions, so the transaction returned to wh_outbox and wh_inbox after
--              wh_perspective_events. Each step is unchanged and they write different rows, so the outcome is the same;
--              an unknown category still raises from report_failures and rolls the whole flush back.
--
--              Kept as it was: the signature, so both drivers' FlushCompletionsAsync are unchanged, and every step
--              function. complete_outbox_published is called, not copied, so whichever migration has the last word on
--              it (172 here, 241 on a branch that redefines it) is the one that runs.
--
-- Dependencies: 029 (flush_completions, report_failures, complete_perspective), 172 (complete_outbox_published,
--               process_outbox_failures: the order, the run release this pass locks ahead of)
-- Objects: flush_completions
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('flush_completions');

-- <docs>fundamentals/work-coordinator/batched-flushers</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FlushCompletionsSqlTests.cs:FlushCompletions_OutboxAndPerspective_AppliesBothInOneCallAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:FlushCompletions_FailingARowAnotherTransactionCompletesFirst_NeverDeadlocksAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxStreamRunDeadlockSqlTests.cs:FlushCompletions_ReleasingARunAnotherTransactionCompletesFirst_NeverDeadlocksAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.flush_completions(
  p_outbox_ids UUID[],
  p_perspective_cursors JSONB,
  p_perspective_event_work_ids UUID[],
  p_failures JSONB           -- [{Category: the work category, Items: [...]}]
) RETURNS VOID AS $$
DECLARE
  v_failure_group RECORD;
  -- 245: the failed outbox rows' ids, read from the request without touching a table.
  v_outbox_failed UUID[];
  v_has_failures BOOLEAN := p_failures IS NOT NULL AND jsonb_array_length(p_failures) > 0;
BEGIN
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
      COALESCE(p_perspective_event_work_ids, ARRAY[]::UUID[])
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

COMMENT ON FUNCTION __SCHEMA__.flush_completions(UUID[], JSONB, UUID[], JSONB) IS
'Composite single-round-trip flusher: complete_outbox_published + report_failures (per category) + complete_perspective in one transaction. 245 (#1785): every outbox row it writes (completed rows, failed rows, the runs the failures release) is locked first in one (stream_id, created_at, message_id) pass, and the steps run in the canonical table order (outbox, inbox, perspective work), so it cannot deadlock against another writer that takes outbox rows in that order.';
