-- Migration: 128_DlqStackTables
-- Date: 2026-09-03
-- Description: Relational stack layer (P2 of plans/dlq-stack-intelligence.md) — the inverted
--   index over dead-letter stacks. Frames dedupe across ALL dead letters; links preserve
--   order; wh_dead_letters gains stack_id. Normalization runs in C# ONLY (StackNormalizer):
--   these objects store, they never compute — a C#/SQL dual implementation would drift and
--   split the cohort identity the canary campaigns and the new-stack alarm key on.
-- Dependencies: 050_WhDeadLetters
-- Objects: wh_stack_frames, wh_stacks, wh_stack_links, record_dead_letter_stack, fetch_unstacked_dead_letters

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_stack_frames (
  frame_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  frame      TEXT NOT NULL UNIQUE,
  first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_stacks (
  stack_id    VARCHAR(16) PRIMARY KEY,
  frame_count INTEGER NOT NULL,
  is_prose    BOOLEAN NOT NULL,
  first_seen  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_stack_links (
  stack_id VARCHAR(16) NOT NULL,
  position INTEGER NOT NULL,
  frame_id BIGINT NOT NULL,
  PRIMARY KEY (stack_id, position)
);

-- Blast-radius queries ("every stack passing through this frame") walk this index.
CREATE INDEX IF NOT EXISTS wh_stack_links_frame_idx ON __SCHEMA__.wh_stack_links (frame_id);

ALTER TABLE __SCHEMA__.wh_dead_letters ADD COLUMN IF NOT EXISTS stack_id VARCHAR(16);
CREATE INDEX IF NOT EXISTS wh_dead_letters_stack_idx
  ON __SCHEMA__.wh_dead_letters (stack_id) WHERE stack_id IS NOT NULL;
-- The backfill's work queue: unstamped rows with text to normalize.
CREATE INDEX IF NOT EXISTS wh_dead_letters_unstacked_idx
  ON __SCHEMA__.wh_dead_letters (dead_lettered_at DESC)
  WHERE stack_id IS NULL AND error_text IS NOT NULL;

COMMENT ON TABLE __SCHEMA__.wh_stack_frames IS
'Deduplicated normalized stack frames across every dead letter (128). One row per distinct frame; the links table gives order per stack. Normalization happens in C# (StackNormalizer) — this table stores, it never computes.';
COMMENT ON TABLE __SCHEMA__.wh_stacks IS
'One row per distinct normalized stack (128): stack_id is the 16-hex sequence hash the inline metric and the backfill both compute via the SAME C# implementation. is_prose marks template identities (errors with no frames).';
COMMENT ON TABLE __SCHEMA__.wh_stack_links IS
'Ordered many-to-many between stacks and frames (128): (stack_id, position) -> frame_id. Order is semantic — throw site versus caller — so this IS the stack, not a bag of frames.';

-- ============================================================================
-- record_dead_letter_stack — idempotent upsert + stamp
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.record_dead_letter_stack(
  p_dead_letter_id UUID, p_stack_id VARCHAR(16), p_is_prose BOOLEAN, p_frames TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_pos INTEGER;
BEGIN
  INSERT INTO __SCHEMA__.wh_stacks (stack_id, frame_count, is_prose)
  VALUES (p_stack_id, COALESCE(array_length(p_frames, 1), 0), p_is_prose)
  ON CONFLICT (stack_id) DO NOTHING;

  IF p_frames IS NOT NULL THEN
    FOR v_pos IN 1..COALESCE(array_length(p_frames, 1), 0) LOOP
      INSERT INTO __SCHEMA__.wh_stack_frames (frame)
      VALUES (p_frames[v_pos])
      ON CONFLICT (frame) DO NOTHING;

      INSERT INTO __SCHEMA__.wh_stack_links (stack_id, position, frame_id)
      SELECT p_stack_id, v_pos, f.frame_id
      FROM __SCHEMA__.wh_stack_frames f
      WHERE f.frame = p_frames[v_pos]
      ON CONFLICT (stack_id, position) DO NOTHING;
    END LOOP;
  END IF;

  UPDATE __SCHEMA__.wh_dead_letters
  SET stack_id = p_stack_id
  WHERE dead_letter_id = p_dead_letter_id;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.record_dead_letter_stack IS
'Persists a C#-normalized stack idempotently (128): stack row, deduped frames, ordered links, and the dead letter''s stamp. A storm records the same stack thousands of times — every conflict is a designed no-op.';

-- ============================================================================
-- fetch_unstacked_dead_letters — the backfill work queue
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.fetch_unstacked_dead_letters(p_max INTEGER)
RETURNS TABLE(dead_letter_id UUID, error_text TEXT)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  SELECT dl.dead_letter_id, dl.error_text
  FROM __SCHEMA__.wh_dead_letters dl
  WHERE dl.stack_id IS NULL AND dl.error_text IS NOT NULL
  ORDER BY dl.dead_lettered_at DESC
  LIMIT p_max;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.fetch_unstacked_dead_letters IS
'Unstamped dead letters, newest first (128) — the bounded per-scan work queue for the C# stack backfill.';
