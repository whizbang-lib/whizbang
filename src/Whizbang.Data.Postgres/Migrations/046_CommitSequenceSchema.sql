-- Migration: 046_CommitSequenceSchema.sql
-- Date: 2026-05-23
-- Description: Slice 26 step 1 — foundational schema for commit-sequence stamping.
--              Adds wh_service_config bootstrap table (stable per-service identity),
--              new columns on wh_event_store (commit_sequence + origin_*) and wh_inbox
--              (source_service_id + source_commit_sequence), the wh_commit_seq sequence
--              that the stamper allocates from, and indexes for the stamper's hot-path
--              query (find unstamped rows) and for downstream cursor-by-source joins.
--
--              The stamping worker (slice 26 step 2+) populates commit_sequence post-commit
--              using `WHERE commit_sequence IS NULL AND xmin < pg_snapshot_xmin(...)`. The
--              outbox publish path then includes (source_service_id, source_commit_sequence)
--              in the envelope; downstream services persist those into wh_inbox.
-- Dependencies: 007 (wh_active_streams), tables wh_event_store + wh_inbox created via EF.

-- ============================================================================
-- wh_service_config — singleton bootstrap row for the local service identity
-- ============================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_service_config (
  single_row BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (single_row),
  service_id UUID NOT NULL DEFAULT gen_random_uuid(),
  service_name TEXT NOT NULL DEFAULT 'unknown',
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Seed the singleton row. ON CONFLICT DO NOTHING keeps the original service_id stable
-- across migrations / restarts — never overwrite once assigned.
INSERT INTO __SCHEMA__.wh_service_config (single_row)
VALUES (TRUE)
ON CONFLICT (single_row) DO NOTHING;

COMMENT ON TABLE __SCHEMA__.wh_service_config IS
'Slice 26 — singleton bootstrap row. service_id is the stable identity that rides on outgoing message envelopes as SourceServiceId and lets downstream consumers maintain per-source cursors. CHECK (single_row) constraint enforces exactly one row.';

-- ============================================================================
-- wh_commit_seq — monotonic source for commit_sequence values
-- ============================================================================

CREATE SEQUENCE IF NOT EXISTS __SCHEMA__.wh_commit_seq
  AS BIGINT
  MINVALUE 1
  START 1
  CACHE 100;

COMMENT ON SEQUENCE __SCHEMA__.wh_commit_seq IS
'Slice 26 — supplies BIGINT values to the stamper worker. Singleton stamper (advisory-lock leader) calls nextval() per row stamped, ordered by xmin. Cache 100 amortizes the nextval call across batches.';

-- ============================================================================
-- wh_event_store — new columns for commit-sequence stamping
-- ============================================================================

-- commit_sequence: populated post-commit by the stamper. NULL means "not yet stable".
ALTER TABLE __SCHEMA__.wh_event_store
  ADD COLUMN IF NOT EXISTS commit_sequence BIGINT;

-- origin_service_id / origin_commit_sequence: populated only when this row is the
-- destination of a 1:1 forwarded event (rare path). For locally-originated events,
-- both stay NULL and the outbox publish path COALESCEs to the local service's identity.
ALTER TABLE __SCHEMA__.wh_event_store
  ADD COLUMN IF NOT EXISTS origin_service_id UUID;

ALTER TABLE __SCHEMA__.wh_event_store
  ADD COLUMN IF NOT EXISTS origin_commit_sequence BIGINT;

COMMENT ON COLUMN __SCHEMA__.wh_event_store.commit_sequence IS
'Slice 26 — monotonic per-DB sequence value stamped POST-COMMIT by the stamper worker. NULL until stamped (means "not yet stable for downstream publication"). Outbox publish path filters WHERE commit_sequence IS NOT NULL.';

COMMENT ON COLUMN __SCHEMA__.wh_event_store.origin_service_id IS
'Slice 26 — populated only when this event was forwarded 1:1 from another service. NULL for locally-originated events. The outbox envelope picks this up via COALESCE(origin_service_id, local_service_id) so downstream consumers see a stable origin identity.';

COMMENT ON COLUMN __SCHEMA__.wh_event_store.origin_commit_sequence IS
'Slice 26 — companion to origin_service_id. Carries the original commit_sequence from the source service for 1:1 forwarded events.';

-- Stamper hot-path: find unstamped rows. PG's `xid` system column has no btree operator
-- class so we can't index by it directly; instead we use a partial index on event_id
-- restricted to unstamped rows. The set is small in steady state (stamper drains to zero
-- per tick), so the per-row in-memory sort by xmin after a small partial-index scan is
-- cheap. The partial WHERE is what gives the stamper its O(unstamped) discovery cost
-- even as wh_event_store grows.
CREATE INDEX IF NOT EXISTS idx_event_store_unstamped
  ON __SCHEMA__.wh_event_store (event_id)
  WHERE commit_sequence IS NULL;

-- Downstream consumer: ORDER BY commit_sequence to honor commit-order on drain.
-- Skip NULL entries (covered by the partial index above on the stamper side).
CREATE INDEX IF NOT EXISTS idx_event_store_commit_sequence
  ON __SCHEMA__.wh_event_store (commit_sequence)
  WHERE commit_sequence IS NOT NULL;

-- ============================================================================
-- wh_inbox — new columns for cross-service source identity
-- ============================================================================

-- NOT NULL: every message that lands in wh_inbox must carry these. The receive-boundary
-- loose-deserialization gate (sibling resilient-transport plan) drops messages that
-- don't have the required envelope fields, so the table never sees NULLs in practice.
-- The migration adds them as nullable first (for any pre-existing rows during dev), then
-- backfills, then enforces NOT NULL. In a fresh test DB the table is empty, so NOT NULL
-- is safe to enforce immediately via a fill-and-flip pattern.
-- Add columns nullable first, backfill, then enforce NOT NULL via a BEFORE INSERT trigger.
-- PG does not allow subqueries in DEFAULT expressions, so we use a trigger to fill the
-- local service identity when the caller omits the columns (legacy test inserts, internal
-- emit paths, local handler chains). Real cross-service envelopes carry explicit values
-- via store_inbox_messages; the trigger is purely a safety net so NOT NULL stays enforceable
-- without rewriting every call site that pre-dated slice 26.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = current_schema()
      AND table_name = 'wh_inbox'
      AND column_name = 'source_service_id'
  ) THEN
    ALTER TABLE __SCHEMA__.wh_inbox ADD COLUMN source_service_id UUID;
    UPDATE __SCHEMA__.wh_inbox
       SET source_service_id = (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1)
     WHERE source_service_id IS NULL;
    ALTER TABLE __SCHEMA__.wh_inbox ALTER COLUMN source_service_id SET NOT NULL;
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = current_schema()
      AND table_name = 'wh_inbox'
      AND column_name = 'source_commit_sequence'
  ) THEN
    ALTER TABLE __SCHEMA__.wh_inbox ADD COLUMN source_commit_sequence BIGINT DEFAULT 0;
    UPDATE __SCHEMA__.wh_inbox
       SET source_commit_sequence = 0
     WHERE source_commit_sequence IS NULL;
    ALTER TABLE __SCHEMA__.wh_inbox ALTER COLUMN source_commit_sequence SET NOT NULL;
  END IF;
END $$;

-- Trigger: fill source_service_id with local wh_service_config.service_id when caller
-- doesn't supply it. Doesn't touch source_commit_sequence (column default 0 handles that).
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_inbox_fill_source_defaults() RETURNS TRIGGER AS $$
BEGIN
  IF NEW.source_service_id IS NULL THEN
    NEW.source_service_id := (SELECT service_id FROM __SCHEMA__.wh_service_config LIMIT 1);
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS wh_inbox_fill_source_defaults_trg ON __SCHEMA__.wh_inbox;
CREATE TRIGGER wh_inbox_fill_source_defaults_trg
  BEFORE INSERT ON __SCHEMA__.wh_inbox
  FOR EACH ROW
  EXECUTE FUNCTION __SCHEMA__._wh_inbox_fill_source_defaults();

COMMENT ON COLUMN __SCHEMA__.wh_inbox.source_service_id IS
'Slice 26 — service_id of the originating service for this message. Carried from the outbox envelope. NOT NULL: receive-boundary gate drops messages without it.';

COMMENT ON COLUMN __SCHEMA__.wh_inbox.source_commit_sequence IS
'Slice 26 — commit_sequence value the SOURCE service stamped for this event. Together with source_service_id, the basis for the downstream consumer''s per-source cursor.';

CREATE INDEX IF NOT EXISTS idx_inbox_source_cursor
  ON __SCHEMA__.wh_inbox (source_service_id, source_commit_sequence);
