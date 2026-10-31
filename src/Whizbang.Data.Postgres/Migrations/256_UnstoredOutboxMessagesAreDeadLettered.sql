-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 256_UnstoredOutboxMessagesAreDeadLettered.sql
-- Date: 2026-10-29
-- Description: Outbox messages the producer-side batcher accepted and could not store are written to the dead-letter
--              table instead of being dropped (#1871).
--
--              The batcher buffers an outbox message per stream and stores each stream's batch through
--              store_outbox_messages on a worker of its own, after the caller (a receptor's LocalInvokeAsync, a
--              publish) has already returned. A batch whose store failed was logged and dropped, so an event a
--              receptor returned could vanish with no row anywhere. The batcher now retries a transient failure and,
--              when the batch still cannot be stored, calls wh_store_outbox_dead_letters.
--
--              wh_store_outbox_dead_letters writes one wh_outbox dead letter per message in the shape
--              move_to_dead_letters (241) gives a stored outbox row's dead letter: the envelope keeps every field
--              store_outbox_messages takes, so wh_recover_dead_letter re-enters the message through the store as if
--              it had just been sent. The message was never a row, so nothing is moved: the dead letter is built from
--              the batch the store was given, read with the same field names. Each message written is also logged in
--              wh_log (level 3, source 'outbox_flush', the message id in message_id), in the same transaction.
--
--              A message is skipped, not dead-lettered, when it is already stored (an outbox row or an event-store
--              event with its id: the store committed and only its reply was lost) or already has a dead letter not
--              yet recovered (the same batch reported twice). The retry budget is the message's, seeded from its
--              earlier dead letters in the same generation, as move_to_dead_letters does (#518).
--
-- Dependencies: 241 (the outbox dead letter's envelope shape, read by wh_recover_dead_letter), 232
--               (_wh_event_flags_from_names), 053 (compute_dead_letter_fingerprint,
--               current_dead_letter_fingerprint_version), 028 (wh_log)
-- Objects: wh_store_outbox_dead_letters
-- Constants: the double-underscore tokens in this file (for example __ENVELOPE_FIELD_MESSAGE_ID__) are substituted
--            from Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_store_outbox_dead_letters');

-- <docs>extending/internals/outbox-batch-strategy</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxFlushPoolExhaustionTests.cs:StoreOutboxDeadLetters_WritesARecoverableDeadLetterAndALogRowPerMessageAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxFlushPoolExhaustionTests.cs:StoreOutboxDeadLetters_SkipsAMessageAlreadyStoredOrAlreadyDeadLetteredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxFlushPoolExhaustionTests.cs:PerStreamFlushes_PoolExhausted_EveryAcceptedMessageReachesTheOutboxAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_store_outbox_dead_letters(
  p_messages         JSONB,     -- the batch as store_outbox_messages takes it
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
      e.elem->>'Destination' AS destination,
      COALESCE(e.elem->'Metadata', '{}'::JSONB) AS metadata,
      -- 241's outbox capture: every field store_outbox_messages takes, read as the store reads it.
      jsonb_build_object(
        'event_data', e.elem->'Envelope',
        'metadata', e.elem->'Metadata',
        'envelope_type', e.elem->>'EnvelopeType',
        'is_event', COALESCE((e.elem->>'IsEvent')::BOOLEAN, false),
        'flags', CASE
          WHEN e.elem->>__ENVELOPE_FIELD_FLAGS__ IS NULL OR e.elem->>__ENVELOPE_FIELD_FLAGS__ = '' THEN 0
          WHEN e.elem->>__ENVELOPE_FIELD_FLAGS__ ~ '^[0-9]+$' THEN (e.elem->>__ENVELOPE_FIELD_FLAGS__)::INTEGER
          ELSE __SCHEMA__._wh_event_flags_from_names(e.elem->>__ENVELOPE_FIELD_FLAGS__)
        END,
        'scope', e.elem->'Scope',
        'priority', COALESCE(NULLIF((e.elem->>'Priority')::INTEGER, 0), 150),
        'scheduled_for', NULLIF(e.elem->>'ScheduledFor', ''),
        'coalesce_group', NULLIF(e.elem->>'CoalesceGroup', '')) AS envelope
    FROM jsonb_array_elements(p_messages) WITH ORDINALITY AS e(elem, ord)
  ),
  unstored AS (
    SELECT b.*
    FROM batch b
    WHERE NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_outbox o WHERE o.message_id = b.message_id)
      AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = b.message_id)
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_dead_letters dl
        WHERE dl.source_table = 'wh_outbox' AND dl.source_id = b.message_id AND dl.recovered_at IS NULL)
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
      'wh_outbox',
      u.message_id,
      u.stream_id,
      u.message_type,
      u.destination,
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
       WHERE dl.source_table = 'wh_outbox'
         AND dl.source_id = u.message_id
         AND dl.generation IS NOT DISTINCT FROM p_generation)
    FROM unstored u
    RETURNING source_id, dead_letter_id, message_type, stream_id
  ),
  logged AS (
    INSERT INTO __SCHEMA__.wh_log (log_level, source, message_id, event_type, error_message, metadata)
    SELECT
      3,  -- Error
      'outbox_flush',
      w.source_id,
      left(w.message_type, 500),
      'Outbox message was not stored and was written to wh_dead_letters; dead-letter recovery stores it again',
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

COMMENT ON FUNCTION __SCHEMA__.wh_store_outbox_dead_letters(JSONB, UUID[], INTEGER, TEXT, INTEGER, UUID, TEXT) IS
  'Writes outbox messages the batcher could not store to wh_dead_letters as wh_outbox dead letters in 241''s shape, so '
  'wh_recover_dead_letter stores them through store_outbox_messages, and logs each in wh_log; skips a message already '
  'stored or already dead-lettered. Returns the number written (256, #1871).';
