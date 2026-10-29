-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 260_StreamLifecycleLedger.sql
-- Date: 2026-10-29
-- Description: The stream lifecycle ledger, wh_stream_lifecycle (#1732, WS-3 slice S3): one row per stream, so the
--              end-of-life work that follows (sealing, scheduled disposition, archiving, idle and size policies) finds
--              its candidate streams without reading the event store.
--
--              Every path that stores an event keeps its stream's row: the outbox emit chain and the inbox emit chain
--              (one set-based upsert per batch, in the statement that copies the events) and the direct append
--              (IEventStore.AppendAsync on both drivers, through wh_append_event). Each runs under the per-stream
--              advisory lock the copy already holds, so the upsert waits only on a batch that holds the same stream.
--              An ephemeral event or a compaction origin (flags 8 and 16) does not count, and neither does an event
--              without a stream (the empty stream id): an ephemeral stream is reaped by its time to live and has no
--              end of life to manage. A purge removes the stream's row with its events.
--
--              The emit chain is the hottest write path in the framework, so the table is built for heap-only
--              updates. Only state and disposition_due_at are indexed besides the key (one partial index over the rows
--              with a disposition scheduled), and an append writes neither: it moves last_version, adds to
--              approx_bytes, and moves last_appended_bucket only when the UTC hour changes. Fillfactor 70 leaves room
--              on each page for the new row version a heap-only update needs, and autovacuum runs on a fixed count of
--              dead rows, as for wh_stream_digests (222), because every append batch leaves one per stream.
--
--              The streams the store held before this migration are seeded by a one-time backfill that runs outside
--              the migration, in bounded batches driven by a maintenance step (StreamLifecycleBackfill), each batch
--              its own transaction. It is gated on the wh_settings row stream_lifecycle_backfill_version (README rule
--              6: 0 = to do, 1 = done) and resumes from stream_lifecycle_backfill_cursor. A store with no events at
--              this point has nothing to backfill and is recorded as done here.
--
--              Number 260: reserved for this change.
--
-- Dependencies: 216 (last word of _emit_event_store_chain), 230 (last word of _emit_event_store_chain_for_inbox),
--               228 (last word of wh_append_event), 214 (last word of wh_purge_streams), 222 (the autovacuum settings
--               copied here)
-- Objects: wh_stream_lifecycle, idx_stream_lifecycle_due, ck_stream_lifecycle_state, wh_backfill_stream_lifecycle_batch,
--          _emit_event_store_chain, _emit_event_store_chain_for_inbox, wh_append_event, wh_purge_streams; the
--          wh_settings rows stream_lifecycle_backfill_version and stream_lifecycle_backfill_cursor
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ===========================================================================================
-- The table
-- ===========================================================================================

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_stream_lifecycle (
  stream_id UUID NOT NULL PRIMARY KEY,
  stream_type TEXT NOT NULL,
  tenant TEXT NULL,
  first_appended_at TIMESTAMPTZ NOT NULL,
  last_appended_bucket TIMESTAMPTZ NOT NULL,
  last_version BIGINT NOT NULL,
  approx_bytes BIGINT NOT NULL DEFAULT 0,
  state TEXT NOT NULL DEFAULT 'Active',
  sealed_at TIMESTAMPTZ NULL,
  disposition_due_at TIMESTAMPTZ NULL,
  archive_locator TEXT NULL,
  origin_generation BIGINT NULL,
  CONSTRAINT ck_stream_lifecycle_state CHECK (state IN ('Active', 'Sealed', 'Archived'))
) WITH (
  fillfactor = 70,
  autovacuum_vacuum_scale_factor = 0,
  autovacuum_vacuum_threshold = 10000
);

-- The due streams, in the order a disposition sweep takes them. Partial: a stream with no disposition scheduled (every
-- Active stream) has no entry, so an append never touches this index. The key carries stream_id and the entry carries
-- state, so the sweep's candidate query is answered from the index alone.
CREATE INDEX IF NOT EXISTS idx_stream_lifecycle_due
  ON __SCHEMA__.wh_stream_lifecycle (disposition_due_at, stream_id) INCLUDE (state)
  WHERE disposition_due_at IS NOT NULL;

COMMENT ON TABLE __SCHEMA__.wh_stream_lifecycle IS
  'One row per stream that holds a Sourced event: what the stream end-of-life work needs to find its candidates without reading the event store (#1732). Kept by the emit chains and wh_append_event; seeded for older streams by wh_backfill_stream_lifecycle_batch.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.stream_type IS
  'The aggregate_type of the stream''s first event (the stream type, decision 1 of the stream end-of-life plan). Written once.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.tenant IS
  'The tenant (scope t) of the stream''s first event; NULL when it had none. Written once.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.first_appended_at IS
  'When the stream''s first event was stored. Written once.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.last_appended_bucket IS
  'The UTC hour of the stream''s newest append. Moves only forward and only when the hour changes.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.last_version IS
  'The stream''s newest version.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.approx_bytes IS
  'Approximate size of the stream''s stored bodies (payload plus metadata). An append counts each body as its row holds it before TOAST compression; the backfill counts it as stored, which is smaller for a compressed body.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.state IS
  'Active, Sealed or Archived. Stable string values that later slices match on exactly; every stream starts Active.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.sealed_at IS
  'When the stream was sealed. Written by finalization, never by an append.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.disposition_due_at IS
  'When the stream''s disposition is due. NULL while none is scheduled, which keeps the row out of idx_stream_lifecycle_due.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.archive_locator IS
  'Where the archive store put the stream''s archived events. Written by the archive store.';
COMMENT ON COLUMN __SCHEMA__.wh_stream_lifecycle.origin_generation IS
  'The origin generation the stream''s history was last mutated at (an archive bumps it). NULL until one happens; appends never write it.';

-- ===========================================================================================
-- The backfill's version marker and cursor (README rule 6)
-- ===========================================================================================

-- A store with no events has nothing to seed, so a new database never runs the backfill. ON CONFLICT DO NOTHING: a re-run
-- of this file (the redefinition closure re-runs it whenever an earlier file defining one of its objects re-runs) never
-- restarts a finished backfill and never finishes an unfinished one.
INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
SELECT 'stream_lifecycle_backfill_version',
       CASE WHEN EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_store) THEN '0' ELSE '1' END,
       'integer',
       'Gates the one-time seeding of wh_stream_lifecycle from the event store (#1732, migration 260). 0 = to do, 1 = done.',
       NOW(),
       'migration:260_StreamLifecycleLedger'
ON CONFLICT (setting_key) DO NOTHING;

INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
VALUES ('stream_lifecycle_backfill_cursor',
        __EMPTY_UUID__,
        'string',
        'The last stream id the wh_stream_lifecycle backfill committed; the next batch starts after it (#1732, migration 260).',
        NOW(),
        'migration:260_StreamLifecycleLedger')
ON CONFLICT (setting_key) DO NOTHING;

-- ===========================================================================================
-- The one-time backfill
-- ===========================================================================================
-- Seeds wh_stream_lifecycle for the streams the store held before this migration. Not run here: one statement over the
-- whole store inside the migration's transaction would hold every stream it touched until the migration committed and
-- would be bounded by nothing but the store's size. A maintenance step (StreamLifecycleBackfill, both drivers) calls this
-- repeatedly instead, each call its own transaction over at most p_limit streams.
--
-- Returns the number of streams the call visited; 0 when there is nothing left (it then records the backfill as done,
-- and every later call returns 0 after one keyed read); -1 when another caller holds the cursor, so concurrent instances
-- step aside instead of queueing behind each other.
--
--   Resumable   the cursor (the last stream id committed) moves in the batch's own transaction, so a batch that fails
--               leaves it where it was and the next call repeats that batch.
--   Idempotent  each row is computed whole from the stream's events and written over whatever is there, so computing it
--               again writes the same row. A row a live append created before the backfill reached its stream (it knew
--               only that append) is corrected the same way.
--   Exact       the batch takes each of its streams' append locks (the advisory lock every write path takes) before it
--               reads them, in stream id order as the emit chain does, so an append in flight commits first and is read.
--
-- Streams are walked in stream id order through idx_event_store_stream, one index probe per stream (a recursive walk
-- that stops at p_limit), so a call costs its batch, not the store. The empty stream id sorts first and is never
-- visited, and a stream holding only ephemeral events or compaction origins gets no row, as on the live paths.
-- <docs>fundamentals/events/stream-end-of-life#the-one-time-backfill</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_SeedsEveryStreamFromItsEvents_AndRecordsThatItIsDoneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_StoppedAfterOneBatch_ResumesAfterItsCursor_AndFinishesTheRestAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_RunAgainFromTheStart_WritesTheSameRowsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_OnceDone_ReadsNothingAndChangesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_CorrectsARowALiveAppendCreatedBeforeItArrivedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_WhileAnotherCallerHoldsTheCursor_ReportsBusyAndWritesNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_TakesEachStreamsAppendLock_SoAnAppendInFlightIsNeverMissedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleBackfillSqlTests.cs:Backfill_WithItsCursorRowMissing_StartsFromTheBeginningAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_backfill_stream_lifecycle_batch(p_limit INTEGER)
RETURNS INTEGER AS $$
DECLARE
  v_cursor_text TEXT;
  v_streams UUID[];
  c_version_key CONSTANT TEXT := 'stream_lifecycle_backfill_version';
  c_cursor_key CONSTANT TEXT := 'stream_lifecycle_backfill_cursor';
  -- EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
BEGIN
  IF COALESCE((SELECT s.setting_value::INTEGER FROM __SCHEMA__.wh_settings s WHERE s.setting_key = c_version_key), 0) >= 1 THEN
    RETURN 0;
  END IF;

  -- One caller walks the cursor at a time. SKIP LOCKED: a caller that finds it held returns at once.
  SELECT s.setting_value INTO v_cursor_text
  FROM __SCHEMA__.wh_settings s
  WHERE s.setting_key = c_cursor_key
  FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN
    IF EXISTS (SELECT 1 FROM __SCHEMA__.wh_settings s WHERE s.setting_key = c_cursor_key) THEN
      RETURN -1;
    END IF;
    -- The cursor row is gone (an operator removed it): start again from the beginning, which is safe because a row is
    -- recomputed whole. A concurrent caller that recreated it first wins and this one steps aside.
    INSERT INTO __SCHEMA__.wh_settings (setting_key, setting_value, value_type, description, updated_at, updated_by)
    VALUES (c_cursor_key, __EMPTY_UUID__, 'string',
            'The last stream id the wh_stream_lifecycle backfill committed; the next batch starts after it (#1732, migration 260).',
            NOW(), 'wh_backfill_stream_lifecycle_batch')
    ON CONFLICT (setting_key) DO NOTHING;
    SELECT s.setting_value INTO v_cursor_text
    FROM __SCHEMA__.wh_settings s
    WHERE s.setting_key = c_cursor_key
    FOR UPDATE SKIP LOCKED;
    IF NOT FOUND THEN
      RETURN -1;
    END IF;
  END IF;

  WITH RECURSIVE walk AS (
    (SELECT es.stream_id
     FROM __SCHEMA__.wh_event_store es
     WHERE es.stream_id > v_cursor_text::UUID
     ORDER BY es.stream_id
     LIMIT 1)
    UNION ALL
    SELECT (SELECT es.stream_id
            FROM __SCHEMA__.wh_event_store es
            WHERE es.stream_id > w.stream_id
            ORDER BY es.stream_id
            LIMIT 1)
    FROM walk w
    WHERE w.stream_id IS NOT NULL
  )
  SELECT array_agg(b.stream_id ORDER BY b.stream_id) INTO v_streams
  FROM (SELECT w.stream_id FROM walk w WHERE w.stream_id IS NOT NULL LIMIT p_limit) b;

  IF v_streams IS NULL THEN
    UPDATE __SCHEMA__.wh_settings
    SET setting_value = '1', updated_at = NOW(), updated_by = 'wh_backfill_stream_lifecycle_batch'
    WHERE setting_key = c_version_key;
    RETURN 0;
  END IF;

  -- Each stream's append lock, in stream id order (the emit chain's order). Held to commit.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || l.sid::text))
  FROM (SELECT u.sid FROM unnest(v_streams) AS u(sid) ORDER BY u.sid) AS l;

  -- A separate statement, so its snapshot is taken after the locks were granted.
  INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
    (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes)
  SELECT u.sid, f.aggregate_type, f.scope ->> 't', f.created_at, date_trunc('hour', a.newest_at, 'UTC'), a.last_version, a.bytes
  FROM unnest(v_streams) AS u(sid)
  CROSS JOIN LATERAL (
    SELECT es.aggregate_type, es.scope, es.created_at
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = u.sid
      AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
    ORDER BY es.version
    LIMIT 1
  ) f
  CROSS JOIN LATERAL (
    SELECT MAX(es.version) AS last_version,
           MAX(es.created_at) AS newest_at,
           COALESCE(SUM(COALESCE(pg_column_size(b.event_data), 0) + COALESCE(pg_column_size(b.metadata), 0)), 0) AS bytes
    FROM __SCHEMA__.wh_event_store es
    LEFT JOIN __SCHEMA__.wh_event_body b ON b.event_id = es.event_id
    WHERE es.stream_id = u.sid
      AND COALESCE(es.flags, 0) & c_flags_off_the_ledger = 0
  ) a
  ORDER BY u.sid
  ON CONFLICT (stream_id) DO UPDATE SET
    stream_type = EXCLUDED.stream_type,
    tenant = EXCLUDED.tenant,
    first_appended_at = EXCLUDED.first_appended_at,
    last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket),
    last_version = EXCLUDED.last_version,
    approx_bytes = EXCLUDED.approx_bytes;

  UPDATE __SCHEMA__.wh_settings
  SET setting_value = v_streams[cardinality(v_streams)]::text, updated_at = NOW(), updated_by = 'wh_backfill_stream_lifecycle_batch'
  WHERE setting_key = c_cursor_key;

  RETURN cardinality(v_streams);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_backfill_stream_lifecycle_batch(INTEGER) IS
  'One batch of the one-time wh_stream_lifecycle backfill (#1732): seeds or corrects the rows of the next p_limit streams after the cursor, under their append locks, and moves the cursor. Returns the streams visited, 0 when done (recorded in wh_settings stream_lifecycle_backfill_version), -1 when another caller holds the cursor. Idempotent and resumable; StreamLifecycleBackfill drives it.';

-- _emit_event_store_chain: last word 216, with the stream lifecycle ledger upsert. Nothing else changes.

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:OutboxAppend_CreatesTheRow_FromTheStreamsFirstEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Append_WritesOneLedgerRowPerStreamPerBatch_NotOnePerEventAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:AppendWithinTheHour_IsAHeapOnlyUpdate_WhileAnIndexedColumnChangeIsNotAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:Bucket_AdvancesOnlyWhenTheHourChanges_AndAnEarlierClockNeverMovesItBackAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:ConcurrentBatchesOnDifferentStreams_NeitherWaitsForTheOtherAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:Reconciliation_AuditTraffic_OriginAndSubscriberAnswerTheSameAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain(
  p_outbox_message_ids UUID[],
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;  -- NOSONAR S1192: the field name recurs per function; PL/pgSQL has no file-level constants
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
BEGIN
  IF p_outbox_message_ids IS NULL OR cardinality(p_outbox_message_ids) = 0 THEN
    RETURN 0;
  END IF;

  -- Per-stream advisory locks. Without these, two concurrent transactions can both
  -- read MAX(version)=N from wh_event_store and both attempt INSERT at version=N+1,
  -- violating idx_event_store_stream UNIQUE(stream_id, version) (PG error 23505). The
  -- legacy process_work_batch ran serially through one ProcessWorkBatchAsync call so
  -- the race didn't exist; the new path lets parallel handlers / strategy flushes
  -- target the same stream concurrently.
  --
  -- Lock order is hashtext(stream_id::text), sorted ascending, ensures deadlock-free
  -- nesting between any pair of transactions touching overlapping stream sets.
  -- pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    SELECT DISTINCT o.stream_id AS sid
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
    ORDER BY o.stream_id
  ) AS streams_to_lock;

  -- Phase 4.5A-equivalent: store outbox events into wh_event_store with sequential versioning.
  -- Phase H step 10 slice 1: ORDER BY o.message_id (UUIDv7 = chronological at the source)
  -- so version assignment matches canonical event_id ordering. Without this, two events stored
  -- "out of wall-clock order" (e.g., one took longer to land) would receive versions that
  -- disagree with their UUIDv7 ordering, perspective cursors advance by version, then later
  -- see an "earlier" event_id and trip the cursor-inversion detector + full replay.
  -- Phase H step 10 slice 3: version is computed via a correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, if a future refactor weakens or bypasses the lock, the
  -- per-row MAX read still picks up any concurrent commits.
  -- Migration 061: o.flags carried into wh_event_store.flags (was dropped here previously).
  WITH outbox_events AS (
    SELECT
      o.message_id,
      o.stream_id,
      o.message_type,
      o.event_data,
      o.metadata,
      o.scope,
      o.flags,
      o.created_at,
      ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.message_id) AS row_num
    FROM __SCHEMA__.wh_outbox o
    WHERE o.message_id = ANY(p_outbox_message_ids)
      AND o.is_event = true
      AND o.stream_id IS NOT NULL
  ),
  -- Migration 072: materialize the extracted payload + built metadata ONCE, so the pointer INSERT
  -- and the ephemeral body offload read identical values without recomputing the JSON extraction.
  computed AS (
    SELECT
      oe.message_id,
      oe.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(oe.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(oe.message_type) AS event_type,
      COALESCE(oe.event_data::jsonb -> 'p', oe.event_data::jsonb -> 'Payload', oe.event_data::jsonb -> 'payload') AS body_data,
      jsonb_build_object(
        c_field_message_id, COALESCE(oe.event_data::jsonb -> 'id', oe.event_data::jsonb -> c_field_message_id, oe.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(oe.event_data::jsonb -> 'h', oe.event_data::jsonb -> c_field_hops, oe.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN oe.metadata IS NOT NULL
             AND jsonb_typeof(oe.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((oe.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS body_meta,
      oe.scope,
      oe.row_num,
      oe.flags
    FROM outbox_events oe
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id), 0) + c.row_num,
      p_now,
      c.flags
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version
  ),
  -- Migration 077 (full split): offload EVERY body. Joined to stored_events so only events actually
  -- stored this call get a body row; ON CONFLICT keeps re-store idempotent.
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT
      __EMPTY_UUID__::uuid,  -- NOSONAR S1192: the no-source-service sentinel recurs per function; PL/pgSQL has no file-level constants
      COALESCE(c.scope::jsonb ->> 't', ''),
      c.event_type,
      c.stream_id,
      bit_xor(hashtextextended(c.message_id::text, 0)),
      bit_xor(hashtextextended(c.message_id::text, 1)),
      COUNT(*)::int,
      p_now
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
      -- 216 (#1492): audit records are never digested.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0))
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket)
  )
  SELECT array_agg(event_id) INTO v_stored_event_ids FROM stored_events;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Phase 4.6-equivalent: auto-create perspective events for matching event types.
  -- Phase H step 6 slice 2: populate partition_number via compute_partition(stream_id, p_partition_count)
  -- so claim_orphaned_perspective_events can apply partition-modulo load balancing symmetric
  -- with the outbox / inbox claim paths.
  -- Slice 26.14: route the lease through wh_active_streams. When a live owner is pinned for
  -- the stream, lease to that owner regardless of which instance ran the commit, eliminates
  -- the cross-instance saga race (different instances commit to the same stream, each
  -- selfishly leasing to themselves, drainer races) that produced the residual ~1000 cursor
  -- inversions in a production run. When no live owner is pinned yet (new stream OR stale owner),
  -- fall back to the commit instance so sync paths (UI waiting on a perspective checkpoint)
  -- don't pay claim_orphaned-polling-interval latency on first-event-per-stream.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Migration 061: collective-event routing. A collective event carries no perspective
  -- association, route it to the single fixed __collective__ sink so the perspective worker
  -- dispatches it via ICollectiveDispatcher exactly once (the dispatcher fans out to every
  -- matching model handler internally). One sink row per collective event, driven by the
  -- flag bit, independent of associations. Same partition / owner-lease / dedupe semantics as
  -- the association branch above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    c_collective_sink,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_outbox src ON src.message_id = es.event_id
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND (es.flags & c_flag_collective) = c_flag_collective
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. PG buffers NOTIFY until COMMIT and
  -- dedups (channel, payload) within the transaction, so a tx storing 10k events
  -- delivers exactly one wh_committed notification to each LISTEN-er. The stamper
  -- is the only listener; on wake it runs stamp_pending_commit_sequences within ~1ms
  -- instead of waiting for its polling tick.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain(UUID[], UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Copies committed outbox events into the event store and creates their perspective work (146). 149: the perspective rows inherit the outbox row''s priority. 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732).';

-- _emit_event_store_chain_for_inbox: last word 230, with the stream lifecycle ledger upsert. Nothing else changes.

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:InboxAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamOrderSqlTests.cs:TwoClaimsOfOneStream_TheHeadsClaimCommitsLast_VersionsFollowTheProducerOrderAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditDigestLaneSqlTests.cs:InboxStoredAuditRecord_LeavesTheOriginsDigestLaneUntouched_WhileADomainEventFoldsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(
  p_instance_id UUID,
  p_lease_expiry TIMESTAMPTZ,
  p_now TIMESTAMPTZ,
  p_partition_count INTEGER DEFAULT 10000
) RETURNS INTEGER AS $$
DECLARE
  v_stored_event_ids UUID[];
  v_count INTEGER;
  v_local_service_id UUID;
  c_field_message_id CONSTANT TEXT := __ENVELOPE_FIELD_MESSAGE_ID__;
  c_field_hops CONSTANT TEXT := 'Hops';
  c_source_perspective CONSTANT TEXT := __CATEGORY_PERSPECTIVE__;
  -- Migration 061: collective routing sink + flag bit (EventFlags.Collective = 1 << 0).
  c_collective_sink CONSTANT TEXT := '__collective__';
  c_flag_collective CONSTANT INTEGER := 1;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
  -- 228 (#1546): the rows this pass would copy, for the refusal before the copy.
  v_candidate_ids UUID[];
  v_candidate_streams UUID[];
  v_candidate_flags INTEGER[];
  v_candidate_types TEXT[];
BEGIN
  -- Migration 087: resolve the LOCAL service id once. store_inbox_messages (062) COALESCEs a
  -- missing envelope SourceServiceId to the local id, so a wh_inbox row attributed to SELF (or
  -- zero) is a locally-originated event (loopback), its origin_service_id must stay NULL,
  -- matching the 046 contract ("NULL for locally-originated events").
  SELECT service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config LIMIT 1;

  -- Phase H step 10 slice 2: per-stream advisory locks. Mirrors _emit_event_store_chain (lines
  -- 311-329). Without these, two concurrent claim_work calls (e.g., NOTIFY-driven wake racing
  -- a heartbeat-driven poll) can both read MAX(version)=N from wh_event_store for the same
  -- stream and both attempt INSERT at version=N+1, violating idx_event_store_stream
  -- UNIQUE(stream_id, version) (PG error 23505). Reproduced in production on a consumer's
  -- service during job creation. Lock order is hashtext(stream_id::text), sorted ASC,
  -- ensures deadlock-free nesting between any pair of transactions touching overlapping stream
  -- sets. pg_advisory_xact_lock auto-releases at commit/rollback.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || sid::text))
  FROM (
    -- 162: every column this pass reads is on wh_inbox_state, so the lock pass never touches the
    -- wide message row at all. chain_emitted_at moved here precisely so this predicate stays on one
    -- table and one partial index can cover it; see idx_inbox_state_chain_pending.
    SELECT DISTINCT ist.stream_id AS sid
    FROM __SCHEMA__.wh_inbox_state ist
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      -- 158: only rows this chain has never read (idx_inbox_state_chain_pending); every held row was
      -- read against the event store on every poll before, twice.
      AND ist.chain_emitted_at IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
    ORDER BY ist.stream_id
  ) AS streams_to_lock;

  -- 228 (#1546): a received event that would make its stream hold both ephemeral and Sourced events is refused
  -- before the copy. Nobody awaits a claim, so every refusal here is dead-lettered: recorded in wh_dead_letters and
  -- its lease row completed in place, so the copy below no longer sees it and the claim never offers it again. The
  -- message row is not touched here: this runs inside claim_work after the stream ledger, and wh_inbox comes first
  -- in the canonical lock order. The rows are the ones the copy reads, by the same predicate, under the locks taken
  -- above.
  SELECT array_agg(ist.message_id), array_agg(ist.stream_id), array_agg(i.flags), array_agg(i.message_type)
    INTO v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types
  FROM __SCHEMA__.wh_inbox_state ist
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );
  IF v_candidate_ids IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refuse_mixed_stream_inbox_events(
      v_candidate_ids, v_candidate_streams, v_candidate_flags, v_candidate_types, p_instance_id, p_now);
  END IF;

  -- Auto-create event_store rows for inbox rows owned by this instance that:
  --   - are events (is_event = true)
  --   - have a stream_id
  --   - aren't yet in wh_event_store (idempotent, ON CONFLICT swallows duplicates)
  -- Bounded by lease ownership so we don't scan the whole inbox every tick.
  -- Phase H step 10 slice 1: ORDER BY i.message_id (UUIDv7 = chronological at the source) so
  -- version assignment matches canonical event_id order. See _emit_event_store_chain above
  -- for the rationale, same fix applies to the inbox backfill path.
  -- Migration 061: i.flags carried into wh_event_store.flags (was dropped here previously).
  WITH inbox_events AS (
    SELECT
      i.message_id,
      ist.stream_id,
      i.message_type,
      i.event_data,
      i.metadata,
      i.scope,
      i.flags,
      i.source_service_id,
      i.source_commit_sequence,
      ist.received_at
    -- 162: this pass needs the message body, so both tables. The LEASE table drives the join, so
    -- selection happens on the narrow table and the wide row is fetched only for the rows that
    -- survive it, which is the opposite of scanning the wide row to apply the same predicate.
    FROM __SCHEMA__.wh_inbox_state ist
    JOIN __SCHEMA__.wh_inbox i ON i.message_id = ist.message_id
    WHERE ist.instance_id = p_instance_id
      AND ist.lease_expiry > p_now
      AND ist.processed_at IS NULL
      AND ist.is_event = true
      AND ist.stream_id IS NOT NULL
      AND ist.chain_emitted_at IS NULL  -- 158: see the lock pass above
      AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
      )
  ),
  -- 230 (#1607): a received stream is stored in the producer's order, however many claims carry it. A claim
  -- numbers the rows it holds after the stream's tail, so a claim that holds a stream's later events and reaches
  -- this pass first would number them ahead of the head another claim leased earlier and has not stored yet.
  -- The head is visible here as a pending event of the stream that this pass does not hold: not yet in the event
  -- store, and not leased to this instance under a live lease (a peer's lease, an expired one, none, or a lease a
  -- concurrent claim has not committed). Every row of this batch that sorts after the stream's first such event
  -- waits: it is not stored and not stamped, so the next claim re-reads it, and fetch_inbox_batch does not hand
  -- it to the dispatcher before it is stored. Rows before it are stored now, so the head of a stream never waits.
  -- The order is the message id, the order this pass has always numbered by (slice 1): a fan-out child's id is
  -- derived from its composite and ordinal, so it sorts in producer order whether or not it carries an origin
  -- commit sequence.
  --
  -- One probe per stream, over idx_inbox_state_stream_order (stream_id, received_at, message_id), bounded by the
  -- batch: only rows received no later than the batch's newest row and sorting before its last one can hold it
  -- back. In a stream's FIFO backlog that range is the batch and the rows before it, never the rows queued behind
  -- it, and the lease test is answered from the index, so only a row this pass does not hold reads the heap. No
  -- index is added: wh_inbox_state's index count is a gated design property (InboxStateWriteCostScenarioTests).
  -- An event received after the rows it precedes is not waited for; that is the transport reordering two
  -- deliveries, which the receiver cannot see coming.
  batch_streams AS (
    SELECT ie.stream_id,
           MAX(ie.received_at) AS newest_received_at,
           (array_agg(ie.message_id ORDER BY ie.message_id DESC))[1] AS last_message_id
    FROM inbox_events ie
    GROUP BY ie.stream_id
  ),
  held_back_from AS (
    SELECT s.stream_id,
           (SELECT b.message_id
            FROM __SCHEMA__.wh_inbox_state b
            WHERE b.stream_id = s.stream_id
              AND b.processed_at IS NULL
              AND b.received_at <= s.newest_received_at
              AND b.message_id < s.last_message_id
              AND NOT COALESCE(b.instance_id = p_instance_id AND b.lease_expiry > p_now, false)
              AND b.is_event = true
              AND b.chain_emitted_at IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = b.message_id
              )
            ORDER BY b.message_id
            LIMIT 1) AS first_unheld
    FROM batch_streams s
  ),
  storable_events AS (
    SELECT ie.*,
           ROW_NUMBER() OVER (PARTITION BY ie.stream_id ORDER BY ie.message_id) AS row_num
    FROM inbox_events ie
    JOIN held_back_from h ON h.stream_id = ie.stream_id
    WHERE h.first_unheld IS NULL OR ie.message_id < h.first_unheld
  ),
  -- Phase H step 10 slice 3: version computed via correlated subquery rather than a
  -- pre-materialized CTE. Inside the per-stream advisory lock the values are equivalent, but
  -- the per-row form is defensive, see _emit_event_store_chain above for the rationale.
  -- Migration 072: materialize the extracted payload + built metadata ONCE (see outbox fn above).
  computed AS (
    SELECT
      ie.message_id,
      ie.stream_id,
      SPLIT_PART(__SCHEMA__.normalize_event_type(ie.message_type), ',', 1) AS aggregate_type,
      __SCHEMA__.normalize_event_type(ie.message_type) AS event_type,
      COALESCE(ie.event_data::jsonb -> 'p', ie.event_data::jsonb -> 'Payload', ie.event_data::jsonb -> 'payload') AS body_data,
      jsonb_build_object(
        c_field_message_id, COALESCE(ie.event_data::jsonb -> 'id', ie.event_data::jsonb -> c_field_message_id, ie.event_data::jsonb -> 'messageId'),
        c_field_hops, COALESCE(ie.event_data::jsonb -> 'h', ie.event_data::jsonb -> c_field_hops, ie.event_data::jsonb -> 'hops', '[]'::jsonb)
      ) || CASE
        WHEN ie.metadata IS NOT NULL
             AND jsonb_typeof(ie.metadata::jsonb -> 'ett') = 'number'
        THEN jsonb_build_object('ephemeral_expires_at',
               p_now + ((ie.metadata::jsonb ->> 'ett')::int * INTERVAL '1 second'))
        ELSE '{}'::jsonb
      END AS body_meta,
      ie.scope,
      ie.row_num,
      ie.flags,
      -- Migration 087: normalize the received origin, self/zero means locally-originated (NULL).
      CASE
        WHEN ie.source_service_id IS NULL
             OR ie.source_service_id = __EMPTY_UUID__::uuid
             OR ie.source_service_id = v_local_service_id
        THEN NULL
        ELSE ie.source_service_id
      END AS origin_service_id,
      NULLIF(ie.source_commit_sequence, 0) AS origin_commit_sequence
    FROM storable_events ie
  ),
  stored_events AS (
    INSERT INTO __SCHEMA__.wh_event_store (
      event_id, stream_id, aggregate_id, aggregate_type, event_type,
      scope, version, created_at, flags, origin_service_id, origin_commit_sequence
    )
    SELECT
      c.message_id,
      c.stream_id,
      c.stream_id,
      c.aggregate_type,
      c.event_type,
      c.scope,
      COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = c.stream_id), 0) + c.row_num,
      p_now,
      c.flags,
      -- Migration 087: stamp the origin identity the transport delivered (046 columns were never
      -- populated by the emit chain before this, consumer-side origin-keyed verification needs them).
      c.origin_service_id,
      CASE WHEN c.origin_service_id IS NULL THEN NULL ELSE c.origin_commit_sequence END
    FROM computed c
    -- Phase H step 10 slice 4: DO NOTHING with NO constraint specifier so PG handles BOTH the
    -- event_id PK conflict (idempotent re-store) AND the idx_event_store_stream (stream_id, version)
    -- UNIQUE conflict gracefully. Conflicting rows are silently skipped; the next claim_work cycle
    -- re-attempts them with a fresh MAX(version) snapshot.
    ON CONFLICT DO NOTHING
    RETURNING event_id, stream_id, version
  ),
  -- Migration 077 (full split): offload EVERY body (see outbox fn above).
  stored_bodies AS (
    INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
    SELECT c.message_id, c.body_data, c.body_meta
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    -- Constraint-LESS form (event_id PK is wh_event_body's only constraint, so semantics are
    -- identical), keeps the emit-chain source free of constraint-specific ON CONFLICT forms,
    -- which the version-ordering regression lock forbids (a specific-constraint form on
    -- wh_event_store once let idx_event_store_stream conflicts bubble up as PG 23505).
    ON CONFLICT DO NOTHING
    RETURNING event_id
  ),
  -- Migration 087 (A1c): incrementally fold the just-stored events into wh_stream_digests.
  -- Bucket + predicates mirror ComputeStreamDigestsAsync (the full-sweep recompute) exactly:
  -- ephemeral (flags & 8) and at-most-once occurrences are excluded; XOR is self-inverse, so
  -- ON CONFLICT folds new hashes in by XOR. Joined to stored_events so an idempotent re-store
  -- (ON CONFLICT DO NOTHING above) never double-folds. Bucket conflicts across concurrent
  -- transactions are impossible here: the bucket key contains stream_id and the per-stream
  -- advisory locks serialize same-stream emits; ORDER BY is belt-and-suspenders lock ordering.
  -- The zero-uuid origin bucket = locally-originated events; a non-zero origin = events
  -- received FROM that origin (inbox flavor only).
  digest_folds AS (
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    SELECT
      COALESCE(c.origin_service_id, __EMPTY_UUID__::uuid),
      COALESCE(c.scope::jsonb ->> 't', ''),
      c.event_type,
      c.stream_id,
      bit_xor(hashtextextended(c.message_id::text, 0)),
      bit_xor(hashtextextended(c.message_id::text, 1)),
      COUNT(*)::int,
      p_now
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & 8 = 0
      -- 214 (#1484): the guarantee is a flag bit on the row. The stored metadata never carried it, so the
      -- body_meta test this replaces never matched and every at-most-once occurrence was folded.
      AND COALESCE(c.flags, 0) & c_flag_at_most_once = 0
      -- 216 (#1492): audit records are never digested; a received one no longer lands in its origin's lane.
      AND split_part(c.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
    GROUP BY 1, 2, 3, 4
    ORDER BY 1, 2, 3, 4
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at
  ),
  -- 260 (#1732): the stream lifecycle ledger. One row per stream this call stored a Sourced event on, in the same
  -- statement as the copy: one set-based upsert per batch, grouped by stream (never a write per event), in stream id
  -- order under the per-stream advisory locks taken above, so it can wait only on a batch that holds the same stream.
  -- An event that is ephemeral or a compaction origin (flags & 24) does not count, and neither does a stream-less event:
  -- an ephemeral stream is reaped by its time to live and never reaches end of life. The update touches no indexed
  -- column (last_version, approx_bytes, and a bucket that changes only when the UTC hour does), so it stays heap-only.
  lifecycle_upsert AS (
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes)
    SELECT
      se.stream_id,
      (array_agg(c.aggregate_type ORDER BY se.version))[1],
      (array_agg(c.scope::jsonb ->> 't' ORDER BY se.version))[1],
      p_now,
      date_trunc('hour', p_now, 'UTC'),
      MAX(se.version),
      SUM(COALESCE(pg_column_size(c.body_data), 0) + COALESCE(pg_column_size(c.body_meta), 0))
    FROM computed c
    JOIN stored_events se ON se.event_id = c.message_id
    WHERE COALESCE(c.flags, 0) & c_flags_off_the_ledger = 0
      AND se.stream_id <> __EMPTY_UUID__::uuid
    GROUP BY se.stream_id
    ORDER BY se.stream_id
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket)
  )
  SELECT array_agg(event_id) INTO v_stored_event_ids FROM stored_events;
  v_stored_event_ids := COALESCE(v_stored_event_ids, '{}');
  v_count := cardinality(v_stored_event_ids);

  -- 158: stamp the held rows the chain is finished with, so the next poll reads none of them.
  -- "Finished" is the row's event being in the event store: either this pass inserted it, or it was
  -- already there and the pass above excluded the row for exactly that reason. A row whose insert hit
  -- ON CONFLICT DO NOTHING has no event-store row, so it stays unstamped and the next poll re-attempts
  -- it with a fresh version snapshot, which is the contract that conflict clause states. The stamp runs
  -- before the nothing-was-stored return below, because that return is the steady state of a busy
  -- instance (every held event already chained) and the case the stamp exists for. The stamp and the
  -- inserts share this transaction, so a failure leaves both undone.
  -- 162: the stamp is the write this migration helps most. It used to rewrite a row carrying the
  -- whole message body across twenty-four indexes to set one timestamp, measured at about 41 blocks
  -- per row stamped with only 30 of those spent finding the rows. It now writes the narrow table,
  -- and the predicate that finds them is local to it.
  UPDATE __SCHEMA__.wh_inbox_state ist
  SET chain_emitted_at = p_now
  WHERE ist.instance_id = p_instance_id
    AND ist.lease_expiry > p_now
    AND ist.processed_at IS NULL
    AND ist.is_event = true
    AND ist.stream_id IS NOT NULL
    AND ist.chain_emitted_at IS NULL
    AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = ist.message_id
    );

  IF v_count = 0 THEN
    RETURN 0;
  END IF;

  -- Auto-create perspective_events for the newly-stored events.
  -- Phase H step 6 slice 2: populate partition_number for symmetric load balancing.
  -- Slice 26.14: route the lease through wh_active_streams (live owner wins; fall back to
  -- commit instance when no live owner). Mirror of the outbox-side change in
  -- _emit_event_store_chain.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    ma.target_name,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    -- Slice 26.14: when caller is actively leasing (p_lease_expiry IS NOT NULL),
    -- route through wh_active_streams to the stream's pinned owner. When caller passed
    -- NULL p_lease_expiry / NULL p_instance_id (strategy-flush path, "leave unleased so
    -- claim_orphaned picks it up"), preserve that contract; otherwise we'd land in
    -- instance_id-set-but-lease-NULL purgatory that claim_orphaned's filter excludes.
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
  INNER JOIN __SCHEMA__.wh_message_associations ma
    ON es.event_type = ma.normalized_message_type
    AND ma.association_type = c_source_perspective
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = ma.target_name
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- 100: reconcile self-declares the rewind. Any of THIS invocation's fresh work items whose
  -- event id slots BELOW its cursor's last-applied event is a straggler by construction, a
  -- backfilled event keeps its ORIGINAL id, while its fresh local commit_sequence sits above
  -- the cursor and hides it from the runtime inversion detector forever. Flag the cursor
  -- RewindRequired with the earliest straggler as trigger (min-merge with any existing
  -- trigger, mirroring complete_perspective_checkpoint's straggler path); the worker's
  -- existing rewind routing replays the stream through the corrected order.
  UPDATE __SCHEMA__.wh_perspective_cursors pc
  SET status = pc.status | 32,  -- RewindRequired flag (1 << 5)
      rewind_trigger_event_id = CASE
        WHEN pc.rewind_trigger_event_id IS NULL THEN s.straggler_event_id
        WHEN s.straggler_event_id < pc.rewind_trigger_event_id THEN s.straggler_event_id
        ELSE pc.rewind_trigger_event_id
      END,
      rewind_flagged_at = p_now,
      rewind_first_flagged_at = COALESCE(pc.rewind_first_flagged_at, p_now)
  FROM (
    SELECT pe.stream_id, pe.perspective_name,
           (array_agg(pe.event_id ORDER BY pe.event_id))[1] AS straggler_event_id
    FROM __SCHEMA__.wh_perspective_events pe
    JOIN __SCHEMA__.wh_perspective_cursors c
      ON c.stream_id = pe.stream_id AND c.perspective_name = pe.perspective_name
    WHERE pe.event_id = ANY(v_stored_event_ids)
      AND pe.processed_at IS NULL
      AND c.last_event_id IS NOT NULL
      AND pe.event_id < c.last_event_id
    GROUP BY pe.stream_id, pe.perspective_name
  ) s
  WHERE pc.stream_id = s.stream_id AND pc.perspective_name = s.perspective_name;

  -- Migration 061: collective-event routing (inbox path). See _emit_event_store_chain above.
  INSERT INTO __SCHEMA__.wh_perspective_events (
    event_work_id, stream_id, perspective_name, event_id,
    partition_number, status, attempts, created_at, instance_id, lease_expiry, priority
  )
  SELECT DISTINCT
    gen_random_uuid(),
    es.stream_id,
    c_collective_sink,
    es.event_id,
    __SCHEMA__.compute_partition(es.stream_id, p_partition_count),
    1,                  -- Stored flag
    0,
    p_now,
    CASE WHEN p_lease_expiry IS NOT NULL THEN COALESCE(owner.assigned_instance_id, p_instance_id) ELSE NULL END,
    p_lease_expiry,
    COALESCE(src.priority, 150)   -- 149: the projection is scheduled with the source row's number
  FROM __SCHEMA__.wh_event_store es
  LEFT JOIN __SCHEMA__.wh_inbox src ON src.message_id = es.event_id
  LEFT JOIN LATERAL (
    SELECT ast.assigned_instance_id
    FROM __SCHEMA__.wh_active_streams ast
    WHERE ast.stream_id = es.stream_id
      AND ast.assigned_instance_id IS NOT NULL
      AND EXISTS (
        SELECT 1 FROM __SCHEMA__.wh_service_instances si
        WHERE si.instance_id = ast.assigned_instance_id
      )
  ) owner ON TRUE
  WHERE es.event_id = ANY(v_stored_event_ids)
    AND (es.flags & c_flag_collective) = c_flag_collective
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_events pe_check
      WHERE pe_check.stream_id = es.stream_id
        AND pe_check.perspective_name = c_collective_sink
        AND pe_check.event_id = es.event_id
    )
  ON CONFLICT ON CONSTRAINT uq_perspective_event DO NOTHING;

  -- Slice 26.4: wake the commit-order stamper. See _emit_event_store_chain above for
  -- the rationale and dedup semantics. Inbox backfill is generally a smaller fan-in
  -- than outbox-emit, but the NOTIFY is just as cheap and keeps the stamper hot path
  -- responsive whether events arrive locally or via transport.
  PERFORM __SCHEMA__._queue_doorbell('wh_committed', '');

  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._emit_event_store_chain_for_inbox(UUID, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) IS
  'Copies leased inbox events into the event store, stamps their origin and creates their perspective work (162). 214: the digest fold leaves out at-most-once events (flags & 32). 216: and audit records (#1492). 228: an event that would make its stream hold both ephemeral and Sourced events is dead-lettered instead (WHIZ133, #1546). 230: a row waits behind an earlier pending row of its stream that this pass does not hold, so a received stream is stored in the producer''s order (#1607). 260: keeps each stored stream''s wh_stream_lifecycle row, one upsert per batch (#1732).';

-- wh_append_event: last word 228, with the stream lifecycle ledger upsert. Nothing else changes; the signature is the
-- same, and the overloads are cleared first because this name has had more than one arity (README rule 5).
SELECT __SCHEMA__.drop_all_overloads('wh_append_event');

-- <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_CreatesAndAdvancesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:DirectAppend_ThroughTheEventStore_CreatesTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EphemeralAndCompactionOriginEvents_CreateNoRow_WhileASourcedEventDoesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamLifecycleLedgerSqlTests.cs:EventsWithoutAStream_CreateNoRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EventStoreConcurrentAppendTests.cs:ParallelAppendsToOneStream_AllSucceed_WithVersionsZeroToNMinusOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendStreamDigestTests.cs:MixedPathStream_TableDigestEqualsARecomputationOverAllItsEventsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DirectAppendMixedStreamGuardTests.cs:DirectAppend_SourcedEventOnAnEphemeralStream_ThrowsAndStoresNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_append_event(
  p_event_id UUID,
  p_stream_id UUID,
  p_aggregate_id UUID,
  p_aggregate_type TEXT,
  p_event_type TEXT,
  p_event_data JSONB,
  p_metadata JSONB,
  p_scope JSONB,
  p_created_at TIMESTAMPTZ,
  -- 228 (#1546): the event's kind bits (EventFlags.Ephemeral, EventFlags.Compacted), stored on the row as the outbox
  -- and inbox copies store them. Defaulted so a caller from before this migration still resolves.
  p_flags INTEGER DEFAULT 0
) RETURNS INTEGER AS $$
DECLARE
  v_version INTEGER;
  v_flags INTEGER;
  v_refusal RECORD;
  v_body_bytes BIGINT;  -- 260: the size of the body row as written, for the lifecycle ledger
  c_flag_ephemeral CONSTANT INTEGER := 8;     -- EventFlags.Ephemeral (1 << 3), as the emit chain tests it
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  -- 260: EventFlags.Ephemeral (1 << 3) | EventFlags.Compacted (1 << 4), the events the lifecycle ledger does not count.
  c_flags_off_the_ledger CONSTANT INTEGER := 24;
BEGIN
  -- The emit chains' key for this stream (214), so a direct append and a chain emit serialize too.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || p_stream_id::text));

  -- 228 (#1546): an event that would make its stream hold both ephemeral and Sourced events is refused, under the
  -- lock above, before anything is stored. The caller awaits the append, so the refusal is raised (SQLSTATE WH133,
  -- which both drivers surface as MixedStreamEventException). The check only reads.
  SELECT r.refusal_text, r.refusal_detail INTO v_refusal
  FROM __SCHEMA__._wh_mixed_stream_refusals(
    ARRAY[p_event_id], ARRAY[p_stream_id], ARRAY[COALESCE(p_flags, 0)], ARRAY[p_event_type]) r
  LIMIT 1;
  IF FOUND THEN
    RAISE EXCEPTION USING
      ERRCODE = 'WH133',
      MESSAGE = v_refusal.refusal_text,
      DETAIL = v_refusal.refusal_detail::TEXT,
      HINT = 'Publish the ephemeral events on their own stream.';
  END IF;

  -- A separate statement: its snapshot is taken after the lock was granted.
  INSERT INTO __SCHEMA__.wh_event_store (
    event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at, flags
  )
  SELECT
    p_event_id,
    p_stream_id,
    p_aggregate_id,
    p_aggregate_type,
    p_event_type,
    p_scope,
    COALESCE((SELECT MAX(es.version) FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream_id), -1) + 1,
    p_created_at,
    COALESCE(p_flags, 0)
  RETURNING version, flags INTO v_version, v_flags;

  -- The body half of the pointer/body split (077). Constraint-less ON CONFLICT, as the emit chain writes it:
  -- event_id is the table's only key.
  INSERT INTO __SCHEMA__.wh_event_body (event_id, event_data, metadata)
  VALUES (p_event_id, p_event_data, p_metadata)
  ON CONFLICT DO NOTHING
  -- 260: measured on the row the insert formed, as the emit chains measure theirs (the values as the row holds them,
  -- before any TOAST compression), so the two paths count a body the same way.
  RETURNING pg_column_size(event_data) + pg_column_size(metadata) INTO v_body_bytes;

  -- 225 (#1600): fold the event into its stream digest bucket, as the emit chain's digest_folds does, still under
  -- the stream lock taken above. One event, so one row: the chain's bit_xor and COUNT(*) over a single event are
  -- its two hashes and 1.
  IF COALESCE(v_flags, 0) & c_flag_ephemeral = 0
     AND COALESCE(v_flags, 0) & c_flag_at_most_once = 0
     AND split_part(p_event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__) THEN
    INSERT INTO __SCHEMA__.wh_stream_digests AS d
      (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
    VALUES (
      __EMPTY_UUID__::uuid,  -- NOSONAR S1192: the local-origin sentinel recurs per function; PL/pgSQL has no file-level constants
      COALESCE(p_scope ->> 't', ''),
      p_event_type,
      p_stream_id,
      hashtextextended(p_event_id::text, 0),
      hashtextextended(p_event_id::text, 1),
      1,
      p_created_at
    )
    ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
      digest_lo = d.digest_lo # EXCLUDED.digest_lo,
      digest_hi = d.digest_hi # EXCLUDED.digest_hi,
      event_count = d.event_count + EXCLUDED.event_count,
      updated_at = EXCLUDED.updated_at;
  END IF;

  -- 260 (#1732): the stream lifecycle ledger, as the emit chains keep it, still under the stream lock taken above. The
  -- same events count (not ephemeral, not a compaction origin, on a stream), and the same columns move: an append never
  -- writes an indexed column, so the update is heap-only.
  IF COALESCE(v_flags, 0) & c_flags_off_the_ledger = 0 AND p_stream_id <> __EMPTY_UUID__::uuid THEN
    INSERT INTO __SCHEMA__.wh_stream_lifecycle AS l
      (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, approx_bytes)
    VALUES (
      p_stream_id,
      p_aggregate_type,
      p_scope ->> 't',
      p_created_at,
      date_trunc('hour', p_created_at, 'UTC'),
      v_version,
      COALESCE(v_body_bytes, 0)
    )
    ON CONFLICT (stream_id) DO UPDATE SET
      last_version = GREATEST(l.last_version, EXCLUDED.last_version),
      approx_bytes = l.approx_bytes + EXCLUDED.approx_bytes,
      last_appended_bucket = GREATEST(l.last_appended_bucket, EXCLUDED.last_appended_bucket);
  END IF;

  RETURN v_version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_append_event(UUID, UUID, UUID, TEXT, TEXT, JSONB, JSONB, JSONB, TIMESTAMPTZ, INTEGER) IS
'Appends one event to a stream for IEventStore.AppendAsync (both drivers) and returns the version it assigned. Takes the per-stream advisory lock the emit chains take (hashtext(''wh_event_store:'' || stream_id)), then inserts at COALESCE(MAX(version), -1) + 1 in a separate statement so the read sees every append committed during the wait (224, #1555). 225: folds the stored event into wh_stream_digests as the emit chain does, unless it is ephemeral, at-most-once or of a digest-exempt type (#1600). 228: stores the event''s kind bits (p_flags) and refuses, raising SQLSTATE WH133, an event that would make its stream hold both ephemeral and Sourced events (WHIZ133, #1546). 260: keeps the stream''s wh_stream_lifecycle row (#1732).';

-- wh_purge_streams: last word 214, with the streams' lifecycle rows removed. Nothing else changes.

-- <docs>operations/infrastructure/purging-streams</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:Purge_RemovesEveryRowOfTheStreams_AndLeavesOthersAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs:DryRun_CountsWhatWouldGo_AndChangesNothingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_purge_streams(
  p_stream_ids UUID[],
  p_dry_run BOOLEAN,
  p_purge_id UUID,
  p_batch_index INTEGER,
  p_requested_by TEXT,
  p_reason TEXT
) RETURNS TABLE(purged_table TEXT, purged_rows BIGINT) AS $$
DECLARE
  v_ids UUID[];
  v_event_ids UUID[];
  v_message_ids UUID[];
  v_schema TEXT;
  v_table TEXT;
  v_n BIGINT;
  v_counts JSONB := '{}'::jsonb;
  v_lanes UUID[];
  v_mins BIGINT[];
  v_maxs BIGINT[];
  v_i INTEGER;
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  -- The empty stream id is "no stream": purging it would reach every streamless row.
  IF __EMPTY_UUID__::uuid = ANY(p_stream_ids) THEN
    RAISE EXCEPTION 'wh_purge_streams: the empty stream id cannot be purged';
  END IF;

  SELECT COALESCE(array_agg(DISTINCT s), ARRAY[]::uuid[]) INTO v_ids FROM unnest(p_stream_ids) s;

  -- Ids the indirect tables are keyed by, read before anything is deleted.
  SELECT COALESCE(array_agg(es.event_id), ARRAY[]::uuid[]) INTO v_event_ids
  FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  SELECT v_event_ids || COALESCE(array_agg(i.message_id), ARRAY[]::uuid[]) INTO v_message_ids
  FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);

  -- Work tables, in the canonical lock order.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_outbox o WHERE o.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_outbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Counted before the inbox delete that removes them by cascade.
  SELECT count(*) INTO v_n
  FROM __SCHEMA__.wh_inbox_state st
  JOIN __SCHEMA__.wh_inbox i ON i.message_id = st.message_id
  WHERE i.stream_id = ANY(v_ids);
  purged_table := 'wh_inbox_state'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_inbox i WHERE i.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_inbox'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_events pe WHERE pe.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_events'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_active_streams a WHERE a.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_active_streams'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Deduplication entries of the stream's messages: its events and its inbox messages.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_message_deduplication d WHERE d.message_id = ANY(v_message_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_message_deduplication'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_receptor_processing rp WHERE rp.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_receptor_processing'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Before the events: fk_perspective_cursors_event is ON DELETE RESTRICT.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_cursors c WHERE c.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_cursors'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_snapshots sn WHERE sn.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_snapshots'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_applied pa WHERE pa.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_applied'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Every perspective's row for the streams. The registry names the tables; one it names that does not
  -- exist yet is skipped. Going through regclass resolves this function's own schema whichever way the
  -- runner substituted it (see 105).
  SELECT n.nspname INTO v_schema
  FROM pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
  WHERE c.oid = '__SCHEMA__.wh_event_store'::regclass;

  FOR v_table IN
    SELECT DISTINCT r.table_name FROM __SCHEMA__.wh_perspective_registry r ORDER BY r.table_name
  LOOP
    CONTINUE WHEN to_regclass(format('%I.%I', v_schema, v_table)) IS NULL;
    IF p_dry_run THEN
      EXECUTE format('SELECT count(*) FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) INTO v_n USING v_ids;
    ELSE
      EXECUTE format('DELETE FROM %I.%I WHERE id = ANY($1)', v_schema, v_table) USING v_ids;
      GET DIAGNOSTICS v_n = ROW_COUNT;
    END IF;
    purged_table := v_table; purged_rows := v_n; RETURN NEXT;
    v_counts := v_counts || jsonb_build_object(purged_table, v_n);
  END LOOP;

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_perspective_row_hold h WHERE h.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_perspective_row_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_row_eviction_journal j WHERE j.row_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_row_eviction_journal'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- The sealed epochs covering the fold-relevant events refold AFTER the delete, and their ranges are
  -- unknowable once the rows are gone (the same capture close_stream makes).
  IF NOT p_dry_run THEN
    SELECT array_agg(t.lane), array_agg(t.mn), array_agg(t.mx)
    INTO v_lanes, v_mins, v_maxs
    FROM (
      SELECT COALESCE(es.origin_service_id, __EMPTY_UUID__::uuid) AS lane,
             MIN(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mn,
             MAX(CASE WHEN es.origin_service_id IS NULL THEN es.commit_sequence ELSE es.origin_commit_sequence END) AS mx
      FROM __SCHEMA__.wh_event_store es
      WHERE es.stream_id = ANY(v_ids)
        AND COALESCE(es.flags, 0) & 8 = 0
        AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0  -- 214 (#1484)
      GROUP BY 1
    ) t
    WHERE t.mn IS NOT NULL;
  END IF;

  -- Every event of the streams goes, so every digest bucket and ledger row of the streams goes with them.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_digests sd WHERE sd.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_digests'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_integrity_ledger il WHERE il.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_integrity_ledger'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_body'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_destruction_hold dh WHERE dh.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_destruction_hold'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_lifecycle_completions lc WHERE lc.event_id = ANY(v_event_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_lifecycle_completions'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_archive ea WHERE ea.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_archive'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_apply_fold_watermarks fw WHERE fw.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_apply_fold_watermarks'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_event_store'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- 260 (#1732): the stream's lifecycle row goes with its events, after them, in the order the emit chain writes the
  -- two tables. A purged stream is gone, not archived: no row is left for an end-of-life sweep to act on.
  IF p_dry_run THEN
    SELECT count(*) INTO v_n FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
  ELSE
    DELETE FROM __SCHEMA__.wh_stream_lifecycle sl WHERE sl.stream_id = ANY(v_ids);
    GET DIAGNOSTICS v_n = ROW_COUNT;
  END IF;
  purged_table := 'wh_stream_lifecycle'; purged_rows := v_n; RETURN NEXT;
  v_counts := v_counts || jsonb_build_object(purged_table, v_n);

  -- Refold what the delete touched, then bump the generation ONCE so consumers reset their seals and
  -- re-verify instead of alarming on a deliberate change.
  IF v_lanes IS NOT NULL THEN
    FOR v_i IN 1 .. array_length(v_lanes, 1) LOOP
      PERFORM __SCHEMA__._wh_refold_epochs_covering(v_lanes[v_i], v_mins[v_i], v_maxs[v_i]);
    END LOOP;
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  -- A purged stream stays purged: mark it for every perspective, so a later event is skipped, not applied.
  v_n := COALESCE(array_length(v_ids, 1), 0);
  IF NOT p_dry_run THEN
    INSERT INTO __SCHEMA__.wh_stream_purge_markers (stream_id, perspective_name, purged_at, purge_event_id, purge_id)
    SELECT s, '*', NOW(), NULL, p_purge_id FROM unnest(v_ids) s
    ON CONFLICT (stream_id, perspective_name) DO UPDATE
      SET purged_at = EXCLUDED.purged_at, purge_event_id = NULL, purge_id = EXCLUDED.purge_id;

    INSERT INTO __SCHEMA__.wh_stream_purge_audit (purge_id, batch_index, requested_by, reason, stream_ids, row_counts)
    VALUES (p_purge_id, p_batch_index, p_requested_by, p_reason, v_ids, v_counts || jsonb_build_object('wh_stream_purge_markers', v_n))
    ON CONFLICT (purge_id, batch_index) DO NOTHING;
  END IF;
  purged_table := 'wh_stream_purge_markers'; purged_rows := v_n; RETURN NEXT;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_purge_streams IS
'Operator stream purge (#1030): removes every row of the listed streams from this service''s store in the caller''s transaction, marks them purged for every perspective, and audits the batch. p_dry_run counts the same rows and changes nothing. Returns one row per table. 214: the refolded epoch range leaves out at-most-once events (flags & 32), which no epoch holds. 260: and the streams'' wh_stream_lifecycle rows (#1732).';
