-- Migration: 057_EmitChainInboxIndex.sql
-- Date: 2026-06-11
-- Description: Partial index backing _emit_event_store_chain_for_inbox's outer scan.
--              The function filters wh_inbox for THIS instance's unprocessed event rows
--              with a stream_id, then per-row NOT EXISTS against wh_event_store. A
--              production measurement found the unindexed scan at 137 ms mean (~11 % of
--              total DB time) once wh_event_store grew past several hundred thousand rows
--              and the inbox handler-delay backlog exceeded several thousand rows. The partial index narrows the candidate
--              set to exactly the predicate emit_chain cares about and puts message_id in
--              the key so PG can plan a merge / hash anti-join against wh_event_store.event_id.
-- Dependencies: 001-056 (wh_inbox table, wh_event_store table)

-- 162 moves instance_id, processed_at to wh_inbox_state and drops them here, so a replayed
-- ledger reaches this statement against the post-split shape. It must no-op rather than
-- fail with 42703 and wedge the init behind the schema-ready gate. Same guard as 072's
-- already-dropped inline body columns; to_regclass takes __SCHEMA__ verbatim.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_attribute
             WHERE attrelid = to_regclass('__SCHEMA__.wh_inbox')
               AND attname = 'processed_at' AND NOT attisdropped) THEN
    CREATE INDEX IF NOT EXISTS idx_inbox_emit_chain
    ON __SCHEMA__.wh_inbox (instance_id, message_id)
    WHERE processed_at IS NULL
    AND is_event = true
    AND stream_id IS NOT NULL;
  END IF;
END $$;

-- Guarded for the same reason as the CREATE above: after 162 drops the columns this index
-- keys on, a replay never creates it, and COMMENT ON a missing index is 42P01.
DO $$
BEGIN
  IF to_regclass('__SCHEMA__.idx_inbox_emit_chain') IS NOT NULL THEN
    COMMENT ON INDEX __SCHEMA__.idx_inbox_emit_chain IS
    'v0.685 — backs _emit_event_store_chain_for_inbox''s outer scan. Partial WHERE matches the function''s candidate-row predicate exactly (instance''s unprocessed event rows with stream_id); message_id in the key lets PG plan an anti-join against wh_event_store.event_id (PK) instead of per-row PK lookup. Production baseline before this index: 137 ms / call ~11% of total DB time under heavy inbox load.';
  END IF;
END $$;
