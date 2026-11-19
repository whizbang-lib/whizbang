-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 350_ExhaustedInboxRowsAreDropped.sql
-- Date: 2026-11-07
-- Description: An inbox row past its attempt ceiling that is dropped rather than dead-lettered can be dropped without a
--              lease (#2117).
--
--              The dispatcher enforces the attempt ceiling, so a row the claim keeps charging that never reaches it is
--              retried for ever. The maintenance cycle now concludes those rows itself: a domain message is moved to the
--              dead-letter store (move_to_dead_letters, which keeps its last recorded error), and control-plane traffic,
--              which is never stored (DeadLetterDropPolicy), is dropped here. Only rows still unprocessed and still past
--              the ceiling are touched, so a row that completed or was concluded by its dispatcher in the meantime is
--              left alone. In debug mode the row is kept for forensics with processed_at stamped, as the completion
--              functions keep it; otherwise it is deleted, its state row going with it through ON DELETE CASCADE.
--
--              Number 350: the next free number after 349.
--
-- Dependencies: 162 (wh_inbox_state)
-- Objects: wh_drop_exhausted_inbox_rows

-- <docs>operations/observability/stuck-row-sentinel#exhausted-rows-are-concluded</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ExhaustedInboxDropSqlTests.cs</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperFleetRecoveryTests.cs:DropExhaustedInboxRows_DropsOnlyTheGivenRowsPastTheCeilingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_drop_exhausted_inbox_rows(
  p_message_ids UUID[],
  p_max_attempts INTEGER
) RETURNS INTEGER AS $$
DECLARE
  v_debug_mode BOOLEAN;
  v_rows INTEGER;
BEGIN
  IF p_message_ids IS NULL OR cardinality(p_message_ids) = 0 THEN
    RETURN 0;
  END IF;

  SELECT COALESCE(
    (SELECT setting_value::BOOLEAN FROM __SCHEMA__.wh_settings WHERE setting_key = 'debug_mode'),
    FALSE
  ) INTO v_debug_mode;

  IF v_debug_mode THEN
    UPDATE __SCHEMA__.wh_inbox_state s
    SET processed_at = NOW(), instance_id = NULL, lease_expiry = NULL
    WHERE s.message_id = ANY(p_message_ids)
      AND s.processed_at IS NULL
      AND s.attempts > p_max_attempts;
  ELSE
    DELETE FROM __SCHEMA__.wh_inbox i
    USING __SCHEMA__.wh_inbox_state s
    WHERE s.message_id = i.message_id
      AND i.message_id = ANY(p_message_ids)
      AND s.processed_at IS NULL
      AND s.attempts > p_max_attempts;
  END IF;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_drop_exhausted_inbox_rows(UUID[], INTEGER) IS
  '350 (#2117): drops the given inbox rows that are still unprocessed and past p_max_attempts (stamped processed_at in '
  'debug mode, deleted otherwise) and returns how many. The maintenance cycle calls it for exhausted control-plane rows, '
  'which are never stored in the dead-letter table, when no dispatcher concluded them.';
