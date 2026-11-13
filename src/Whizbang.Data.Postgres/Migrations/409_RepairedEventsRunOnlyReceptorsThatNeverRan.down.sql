-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 409_RepairedEventsRunOnlyReceptorsThatNeverRan.sql (#2273): process_inbox_completions as its last word
-- before 409 defined it (236), so a completion records nothing, then the ledger's functions and tables dropped. The
-- handled records are lost; a later forward run starts the coverage afresh, so a repair never reads the gap as covered.

-- process_inbox_completions: 236's definition, whole.
-- <docs>fundamentals/work-coordinator/handler-commit</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_HappyPath_MarksInboxProcessedAndStoresOutboxAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_DebugModeInRequest_RetainsInboxRowEvenWhenEventStoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitHandlerResultSqlTests.cs:CommitHandlerResult_ProductionMode_DeletesInboxRowOnEventStoredCompletionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredMessage_CompletedUnprocessedByAGate_IsHeldForReview_WithTheOutcomeAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredDeadLetterReentrySqlTests.cs:ARecoveredCommand_CompletedAsProcessed_IsRecoveredAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.process_inbox_completions(
  p_completions JSONB,
  p_now TIMESTAMPTZ,
  p_debug_mode BOOLEAN DEFAULT FALSE
) RETURNS TABLE(
  message_id UUID,
  stream_id UUID,
  was_deleted BOOLEAN
) AS $$
DECLARE
  v_completion RECORD;
  v_current_status INTEGER;
  v_new_status INTEGER;
  v_stream_id UUID;
  v_recovered_from UUID;   -- 236: the dead letter a recovered message re-entered from
  v_is_event BOOLEAN;      -- 236
BEGIN
  IF jsonb_array_length(p_completions) = 0 THEN RETURN; END IF;

  FOR v_completion IN
    SELECT
      (elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID as msg_id,
      (elem->>'Status')::INTEGER as status_flags
    FROM jsonb_array_elements(p_completions) as elem
  LOOP
    -- Get current status and stream_id
    -- 162: both columns are on the state table now (stream_id as an immutable copy), so the
    -- lookup never touches the wide row.
    -- 236: and the recovery columns, from the same row this lookup already reads.
    SELECT ist.status, ist.stream_id, ist.recovered_from_dead_letter_id, ist.is_event
    INTO v_current_status, v_stream_id, v_recovered_from, v_is_event
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.message_id = v_completion.msg_id;

    -- Skip if message not found (already deleted or never existed)
    IF NOT FOUND THEN
      CONTINUE;
    END IF;

    v_new_status := v_current_status | v_completion.status_flags;

    -- 236 (#1664): a recovered message settles its dead letter when it completes. It was processed when the
    -- dispatch completed it as processed (EventStored), or when it is an event the inbox copy already appended
    -- (a gate after the copy declines only the receptors). Anything else is a gate completing it unprocessed.
    IF v_recovered_from IS NOT NULL THEN
      PERFORM __SCHEMA__._wh_settle_recovered_dead_letter(
        v_recovered_from,
        (v_new_status & 2) = 2
          OR (v_is_event AND EXISTS (
            SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = v_completion.msg_id)),
        p_now);
    END IF;

    IF p_debug_mode THEN
      -- Debug mode: Retain message for troubleshooting
      -- 162: status is work state too, so this stays one statement against one table.
      UPDATE __SCHEMA__.wh_inbox_state ist
      SET status = v_new_status,
          processed_at = p_now,
          instance_id = NULL,
          lease_expiry = NULL
      WHERE ist.message_id = v_completion.msg_id;

      RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;

    ELSE
      -- Production: Delete if EventStored flag set (inbox completion = event stored)
      IF (v_new_status & 2) = 2 THEN
        -- 162: unchanged. The lease row goes with it through ON DELETE CASCADE, which is why the
        -- foreign key exists rather than being tidiness: a lease left behind for a deleted message
        -- is a row every reclaim path would keep considering forever.
        DELETE FROM __SCHEMA__.wh_inbox i WHERE i.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, TRUE AS was_deleted;
      ELSE
        -- Event not yet stored, retain with updated status
        -- 162: same single statement as the debug branch above.
        UPDATE __SCHEMA__.wh_inbox_state ist
        SET status = v_new_status,
            processed_at = p_now,
            instance_id = NULL,
            lease_expiry = NULL
        WHERE ist.message_id = v_completion.msg_id;
        RETURN QUERY SELECT v_completion.msg_id AS message_id, v_stream_id AS stream_id, FALSE AS was_deleted;
      END IF;
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.process_inbox_completions(JSONB, TIMESTAMPTZ, BOOLEAN) IS
  'Applies inbox completions: deletes a row completed as EventStored, otherwise records the status (162: on wh_inbox_state). 236 (#1664): a recovered message settles the dead letter it re-entered from, Recovered when processed and HoldForReview with the outcome when a gate completed it unprocessed.';

DROP FUNCTION IF EXISTS __SCHEMA__.wh_receptor_handled_sweep(TIMESTAMPTZ, INTERVAL);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_receptor_handled_lookup(UUID[]);
DROP TABLE IF EXISTS __SCHEMA__.wh_receptor_handled_coverage;
DROP TABLE IF EXISTS __SCHEMA__.wh_receptor_handled;
