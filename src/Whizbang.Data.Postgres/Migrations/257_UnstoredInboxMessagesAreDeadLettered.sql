-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 257_UnstoredInboxMessagesAreDeadLettered.sql
-- Date: 2026-10-29
-- Description: Inbox messages the inbox batcher accepted and could not store are written to the dead-letter table
--              instead of being dropped (#1885).
--
--              The inbox batcher buffers an inbox message per stream and stores each stream's batch through
--              store_inbox_messages on a worker of its own, after the code that appended it has been told it was
--              accepted, and possibly after that code has settled the message with its broker. A batch whose store
--              failed was logged and dropped, so a message could vanish with no inbox row and no dead letter. The
--              batcher now retries a transient failure and, when the batch still cannot be stored, calls
--              wh_store_inbox_dead_letters.
--
--              wh_store_inbox_dead_letters writes one wh_inbox dead letter per message in the shape
--              move_to_dead_letters (162, 241) gives a stored inbox row's dead letter: the envelope holds the message's
--              envelope as event_data and its metadata, which is what dead-letter recovery reads to re-enter the
--              message through store_inbox_messages, exactly as a delivery of the same envelope is stored (236,
--              #1664). The message was never a row, so nothing is moved. Each message written is also logged in wh_log
--              (level 3, source 'inbox_flush', the message id in message_id), in the same transaction.
--
--              A message is skipped, not dead-lettered, when it is already stored (an inbox row, or a deduplication
--              row with its id: the store committed and only its reply was lost) or already has a dead letter not yet
--              recovered (the same batch reported twice). The retry budget is the message's, seeded from its earlier
--              dead letters in the same generation, as move_to_dead_letters does (#518).
--
-- Dependencies: 252 (last word of store_inbox_messages, whose input this function reads), 162 (the inbox dead
--               letter's envelope shape), 053 (compute_dead_letter_fingerprint, current_dead_letter_fingerprint_version),
--               028 (wh_log)
-- Objects: wh_store_inbox_dead_letters
-- Constants: the double-underscore tokens in this file (for example __ENVELOPE_FIELD_MESSAGE_ID__) are substituted
--            from Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_store_inbox_dead_letters');

-- <docs>extending/internals/inbox-batch-strategy</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxFlushDeadLetterTests.cs:StoreInboxDeadLetters_WritesARecoverableDeadLetterAndALogRowPerMessageAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxFlushDeadLetterTests.cs:StoreInboxDeadLetters_SkipsAMessageAlreadyStoredOrAlreadyDeadLetteredAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_store_inbox_dead_letters(
  p_messages         JSONB,     -- the batch as store_inbox_messages takes it
  p_dead_letter_ids  UUID[],    -- one per message, in the batch's order (caller generates UUIDv7)
  p_failure_reason   INTEGER,   -- MessageFailureReason
  p_error_text       TEXT,
  p_attempts         INTEGER,
  p_instance_id      UUID,
  p_generation       TEXT
) RETURNS INTEGER AS $$
DECLARE
  v_written INTEGER;
BEGIN
  IF p_messages IS NULL OR jsonb_array_length(p_messages) = 0 THEN
    RETURN 0;
  END IF;

  WITH batch AS (
    SELECT
      p_dead_letter_ids[e.ord] AS dead_letter_id,
      (e.elem->>__ENVELOPE_FIELD_MESSAGE_ID__)::UUID AS message_id,
      (e.elem->>__ENVELOPE_FIELD_STREAM_ID__)::UUID AS stream_id,
      e.elem->>'MessageType' AS message_type,
      COALESCE(e.elem->'Metadata', '{}'::JSONB) AS metadata,
      -- 162's inbox capture: the row's event_data (the envelope store_inbox_messages stores) and its metadata.
      jsonb_build_object('event_data', e.elem->'Envelope', 'metadata', e.elem->'Metadata') AS envelope
    FROM jsonb_array_elements(p_messages) WITH ORDINALITY AS e(elem, ord)
  ),
  unstored AS (
    SELECT b.*
    FROM batch b
    WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_inbox i WHERE i.message_id = b.message_id)
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = b.message_id)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_dead_letters dl
        WHERE dl.source_table = 'wh_inbox' AND dl.source_id = b.message_id AND dl.recovered_at IS NULL)
  ),
  written AS (
    INSERT INTO __SCHEMA__.wh_dead_letters (
      dead_letter_id,
      source_table,
      source_id,
      stream_id,
      message_type,
      destination,
      perspective_name,
      envelope,
      metadata,
      failure_reason,
      error_text,
      error_fingerprint,
      error_fingerprint_version,
      attempts_when_dlq,
      dead_lettered_by,
      generation,
      recovery_attempts
    )
    SELECT
      u.dead_letter_id,
      'wh_inbox',
      u.message_id,
      u.stream_id,
      u.message_type,
      NULL,
      NULL,
      u.envelope,
      u.metadata,
      p_failure_reason,
      p_error_text,
      __SCHEMA__.compute_dead_letter_fingerprint(p_error_text),
      CASE WHEN p_error_text IS NOT NULL THEN __SCHEMA__.current_dead_letter_fingerprint_version() ELSE NULL END,
      p_attempts,
      p_instance_id,
      p_generation,
      -- #518: the retry budget belongs to the message, across its dead letters in this generation.
      (SELECT COALESCE(SUM(dl.recovery_attempts), 0)
       FROM __SCHEMA__.wh_dead_letters dl
       WHERE dl.source_table = 'wh_inbox'
         AND dl.source_id = u.message_id
         AND dl.generation IS NOT DISTINCT FROM p_generation)
    FROM unstored u
    RETURNING source_id, dead_letter_id, message_type, stream_id
  ),
  logged AS (
    INSERT INTO __SCHEMA__.wh_log (log_level, source, message_id, event_type, error_message, metadata)
    SELECT
      3,  -- Error
      'inbox_flush',
      w.source_id,
      left(w.message_type, 500),
      'Inbox message was not stored and was written to wh_dead_letters; dead-letter recovery stores it through the inbox store',
      jsonb_build_object(
        'dead_letter_id', w.dead_letter_id,
        'message_type', w.message_type,
        'stream_id', w.stream_id,
        'failure_reason', p_failure_reason,
        'attempts', p_attempts,
        'instance_id', p_instance_id)
    FROM written w
    RETURNING 1
  )
  SELECT COUNT(*) INTO v_written FROM logged;

  RETURN v_written;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_store_inbox_dead_letters(JSONB, UUID[], INTEGER, TEXT, INTEGER, UUID, TEXT) IS
  'Writes inbox messages the inbox batcher could not store to wh_dead_letters as wh_inbox dead letters in 162''s shape, '
  'so dead-letter recovery re-enters them through store_inbox_messages, and logs each in wh_log; skips a message '
  'already stored or already dead-lettered. Returns the number written (257, #1885).';
