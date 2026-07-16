-- Migration: 072_EphemeralBodyOffload.sql
-- Date: 2026-07-15 (E1 ephemeral events — #13b1: optional body-offload table)
-- Description: Additive, strangler-first step toward the pointer+body event-store split. Introduces an
--              OPTIONAL body table that, for now, holds ONLY the bodies of EPHEMERAL events, leaving the
--              durable/Sourced path (inline event_data/metadata in wh_event_store) completely untouched.
--                wh_event_body                — event_id -> (event_data, metadata), the uniform envelope
--                                               body. Empty until an ephemeral event exists. Reapable by
--                                               the consumption-gated reaper (#13b2) without ever touching
--                                               wh_event_store, so the durable path never bloats.
--                wh_event_store.event_data/metadata -> NULLABLE — backward-compatible (existing rows and
--                                               all Sourced writes still carry inline values); ephemeral
--                                               events store NULL inline and put the body in wh_event_body.
--              The emit chain (_emit_event_store_chain[_for_inbox]) branches on the persisted
--              EventFlags.Ephemeral bit ((flags & 8) = 8) to route the body — added in the function-rewrite
--              section below.
-- Dependencies: 061 (the _emit_event_store_chain[_for_inbox] functions + flags column), 062 (persists flags).

-- ── The optional body table ────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_event_body (
  event_id   UUID  NOT NULL PRIMARY KEY,
  event_data JSONB NOT NULL,
  metadata   JSONB NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_event_body IS
  'E1 #13b1: optional envelope-body table. Currently holds ONLY ephemeral event bodies (the emit chain '
  'offloads them here when (wh_event_store.flags & 8) = 8), leaving the durable inline body on '
  'wh_event_store for Sourced events. The consumption-gated reaper deletes from here once every '
  'perspective has consumed the event; the wh_event_store pointer row stays as the ordering anchor '
  '(reaped body reads back pointer-present/body-NULL). Future (#13b4): migrate Sourced bodies here too '
  'and drop the inline columns, making wh_event_store the pure pointer table.';

-- ── Relax the durable inline body so ephemeral events can null it out ───────────────────────────────
-- Idempotent (DROP NOT NULL on an already-nullable column is a no-op). Backward-compatible: existing
-- rows keep their values, and Sourced writes continue to provide event_data/metadata inline.
ALTER TABLE __SCHEMA__.wh_event_store ALTER COLUMN event_data DROP NOT NULL;
ALTER TABLE __SCHEMA__.wh_event_store ALTER COLUMN metadata   DROP NOT NULL;
