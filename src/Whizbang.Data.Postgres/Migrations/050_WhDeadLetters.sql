-- Migration: 050_WhDeadLetters.sql
-- Date: 2026-06-03 (v0.502 slice C.1)
-- Description: Creates wh_dead_letters table and move_to_dead_letters() function for the
--              Whizbang DLQ recovery subsystem. See plans/dlq-recovery.md for the full
--              design rationale.
-- Dependencies: 001-049

CREATE TABLE IF NOT EXISTS wh_dead_letters (
  -- identity
  dead_letter_id     UUID PRIMARY KEY,                       -- generated; survives re-emission
  source_table       TEXT NOT NULL,                          -- 'wh_outbox' | 'wh_inbox' | 'wh_perspective_events'
  source_id          UUID NOT NULL,                          -- original message_id / event_work_id
  stream_id          UUID,                                   -- nullable for single-source messages
  message_type       TEXT NOT NULL,
  destination        TEXT,                                   -- routing destination (outbox source)
  perspective_name   TEXT,                                   -- perspective source

  -- payload (forensic preservation)
  envelope           JSONB NOT NULL,                         -- full envelope at time of failure
  metadata           JSONB NOT NULL DEFAULT '{}'::JSONB,

  -- failure provenance
  failure_reason     INTEGER NOT NULL,                       -- MessageFailureReason enum
  error_text         TEXT,
  attempts_when_dlq  INTEGER NOT NULL,                       -- attempts at time of dead-lettering
  dead_lettered_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  dead_lettered_by   UUID,                                   -- instance_id

  -- recovery state
  recovery_status    INTEGER NOT NULL DEFAULT 0,             -- DeadLetterRecoveryStatus enum
  recovery_attempts  INTEGER NOT NULL DEFAULT 0,
  last_recovery_at   TIMESTAMPTZ,
  next_recovery_at   TIMESTAMPTZ,
  recovered_at       TIMESTAMPTZ,                            -- non-null when permanently recovered

  -- generation tagging (deploy-aware auto-replay)
  generation                TEXT NOT NULL,                   -- e.g., "0.502.0-alpha.1+a consumer-1.42.0"
  retried_on_generations    TEXT[] NOT NULL DEFAULT '{}',    -- generations that have already been auto-retried

  -- operator disposition
  operator_disposition INTEGER NOT NULL DEFAULT 0,           -- DeadLetterDisposition enum
  operator_notes       TEXT,
  operator_actor       TEXT,

  -- error fingerprint (Slice 2 of release/v0.645.0-alpha.1)
  -- Populated inline by move_to_dead_letters via compute_dead_letter_fingerprint
  -- (defined in migration 053). VARCHAR(16) holds the first 16 hex chars of
  -- SHA256(type:frame1:frame2:frame3). Lets operators triage 38k+ rows via
  -- GROUP BY error_fingerprint without waiting for Slice 6's aggregation job.
  -- NULL when error_text is NULL — no spurious 'all-NULL' cluster.
  error_fingerprint         VARCHAR(16),
  error_fingerprint_version SMALLINT
);

CREATE INDEX IF NOT EXISTS wh_dead_letters_next_recovery_idx
  ON wh_dead_letters (next_recovery_at)
  WHERE recovered_at IS NULL AND recovery_status != 2;  -- 2 = HoldForReview

CREATE INDEX IF NOT EXISTS wh_dead_letters_stream_idx
  ON wh_dead_letters (stream_id, dead_lettered_at);

CREATE INDEX IF NOT EXISTS wh_dead_letters_generation_idx
  ON wh_dead_letters (generation);

CREATE INDEX IF NOT EXISTS wh_dead_letters_reason_status_idx
  ON wh_dead_letters (failure_reason, recovery_status)
  WHERE recovered_at IS NULL;

-- Slice 2 of release/v0.645.0-alpha.1 — supports the canonical operator/AI triage
-- query `GROUP BY error_fingerprint`. Partial (WHERE NOT NULL) keeps the index
-- skinny on NULL-fingerprint rows (those with NULL error_text).
CREATE INDEX IF NOT EXISTS wh_dead_letters_fingerprint_idx
  ON wh_dead_letters (error_fingerprint)
  WHERE error_fingerprint IS NOT NULL;

COMMENT ON TABLE wh_dead_letters IS
'Forensic record + recovery state for permanently-failed work items (Whizbang internal DLQ). Rows enter via move_to_dead_letters() when attempts exceed the worker''s configured Max*Attempts. The DeadLetterRecoveryWorker scans this table on an idle-cadence trigger and re-emits to the source table when policy allows; generation-tagged auto-replay catches "we shipped a fix" cases on new deploys. See plans/dlq-recovery.md for the full design.';

-- ===========================================================================
-- move_to_dead_letters — atomic insert-into-DLQ + delete-from-source
-- ===========================================================================
-- Snapshots the row's forensic state into wh_dead_letters and DELETEs from the source
-- table in a single transaction. The atomic move prevents partial state on crash + makes
-- the dead-letter operation safely retriable from the worker side.
--
-- Generic over the three source tables (wh_outbox / wh_inbox / wh_perspective_events).
-- Caller passes the source table name + source id; function fetches the row, snapshots
-- the canonical columns, and DELETEs.

SELECT __SCHEMA__.drop_all_overloads('move_to_dead_letters');

CREATE OR REPLACE FUNCTION __SCHEMA__.move_to_dead_letters(
  p_dead_letter_id UUID,                                       -- caller generates (TrackedGuid.NewMedo on C# side)
  p_source_table  TEXT,
  p_source_id     UUID,
  p_failure_reason INTEGER,
  p_error_text    TEXT,
  p_instance_id   UUID,
  p_generation    TEXT
) RETURNS UUID AS $$
DECLARE
  v_stream_id      UUID;
  v_message_type   TEXT;
  v_destination    TEXT;
  v_perspective    TEXT;
  v_envelope       JSONB;
  v_metadata       JSONB;
  v_attempts       INTEGER;
BEGIN

  -- Snapshot per source table. Each branch reads the canonical columns and DELETEs in a
  -- single CTE so the row movement is atomic. The wh_outbox / wh_inbox tables carry the
  -- envelope + metadata directly; wh_perspective_events carries an event_id pointer that
  -- the recovery worker will rejoin against wh_event_store at recovery time.
  IF p_source_table = 'wh_outbox' THEN
    WITH moved AS (
      DELETE FROM wh_outbox
      WHERE message_id = p_source_id
      RETURNING
        stream_id,
        message_type,
        destination,
        event_data,
        metadata,
        attempts
    )
    SELECT m.stream_id, m.message_type, m.destination,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, m.attempts
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts
    FROM moved m;
  ELSIF p_source_table = 'wh_inbox' THEN
    WITH moved AS (
      DELETE FROM wh_inbox
      WHERE message_id = p_source_id
      RETURNING
        stream_id,
        message_type,
        event_data,
        metadata,
        attempts
    )
    SELECT m.stream_id, m.message_type, NULL::TEXT,
           jsonb_build_object('event_data', m.event_data, 'metadata', m.metadata),
           m.metadata, m.attempts
      INTO v_stream_id, v_message_type, v_destination, v_envelope, v_metadata, v_attempts
    FROM moved m;
  ELSIF p_source_table = 'wh_perspective_events' THEN
    WITH moved AS (
      DELETE FROM wh_perspective_events
      WHERE event_work_id = p_source_id
      RETURNING
        stream_id,
        perspective_name,
        event_id,
        attempts
    )
    SELECT m.stream_id, m.perspective_name,
           jsonb_build_object('event_id', m.event_id, 'perspective_name', m.perspective_name),
           '{}'::JSONB, m.attempts
      INTO v_stream_id, v_perspective, v_envelope, v_metadata, v_attempts
    FROM moved m;
    v_message_type := 'perspective_event';
  ELSE
    RAISE EXCEPTION 'move_to_dead_letters: unsupported source table %', p_source_table;
  END IF;

  -- If no row was found (already DLQ'd, already DELETEd by another path), no-op.
  IF v_attempts IS NULL THEN
    RETURN NULL;
  END IF;

  -- Slice 3a of release/v0.645.0-alpha.1 — auto-fingerprint every row at INSERT
  -- time via Slice 2's compute_dead_letter_fingerprint. One source of truth
  -- (the SQL function), three call sites (this INSERT, plus Slice 6's
  -- aggregate_dead_letters version-aware backfill). NULL p_error_text →
  -- NULL fingerprint + NULL version so the column NULLability flows through.
  INSERT INTO wh_dead_letters (
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
    generation
  ) VALUES (
    p_dead_letter_id,
    p_source_table,
    p_source_id,
    v_stream_id,
    v_message_type,
    v_destination,
    v_perspective,
    v_envelope,
    v_metadata,
    p_failure_reason,
    p_error_text,
    __SCHEMA__.compute_dead_letter_fingerprint(p_error_text),
    CASE WHEN p_error_text IS NOT NULL
         THEN __SCHEMA__.current_dead_letter_fingerprint_version()
         ELSE NULL
    END,
    v_attempts,
    p_instance_id,
    p_generation
  );

  RETURN p_dead_letter_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.move_to_dead_letters IS
'Atomically moves a wh_outbox/wh_inbox/wh_perspective_events row into wh_dead_letters with a forensic snapshot. Single tx so partial-failure crashes leave consistent state. Returns the new dead_letter_id, or NULL if the source row was already gone (idempotent under retry). Called by FailureFlushWorker when attempts exceeds the configured limit (slice C.4).';
