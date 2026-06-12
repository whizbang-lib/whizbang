-- Migration: 057_EmitChainInboxIndex.sql
-- Date: 2026-06-11
-- Description: Partial index backing _emit_event_store_chain_for_inbox's outer scan.
--              The function filters wh_inbox for THIS instance's unprocessed event rows
--              with a stream_id, then per-row NOT EXISTS against wh_event_store. Slot-3
--              2026-06-11 PM measured the unindexed scan at 137 ms mean (~11 % of slot-3
--              DB time) once wh_event_store grew past 600 k rows and the inbox handler-
--              delay backlog exceeded ~10 k rows. The partial index narrows the candidate
--              set to exactly the predicate emit_chain cares about and puts message_id in
--              the key so PG can plan a merge / hash anti-join against wh_event_store.event_id.
-- Dependencies: 001-056 (wh_inbox table, wh_event_store table)

CREATE INDEX IF NOT EXISTS idx_inbox_emit_chain
  ON __SCHEMA__.wh_inbox (instance_id, message_id)
  WHERE processed_at IS NULL
    AND is_event = true
    AND stream_id IS NOT NULL;

COMMENT ON INDEX __SCHEMA__.idx_inbox_emit_chain IS
  'v0.685 — backs _emit_event_store_chain_for_inbox''s outer scan. Partial WHERE matches the function''s candidate-row predicate exactly (instance''s unprocessed event rows with stream_id); message_id in the key lets PG plan an anti-join against wh_event_store.event_id (PK) instead of per-row PK lookup. Slot-3 2026-06-11 PM baseline before this index: 137 ms / call ~11% of slot-3 DB time under heavy inbox load.';
