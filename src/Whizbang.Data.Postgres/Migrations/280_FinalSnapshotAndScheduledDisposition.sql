-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 280_FinalSnapshotAndScheduledDisposition.sql
-- Date: 2026-11-01
-- Description: The scheduled disposition of sealed streams and the NewGeneration append policy (#1734, WS-3 S5; owner
--              decisions of 2026-11-01).
--
--              Scheduled disposition. A sealed stream with no disposition scheduled is scheduled by the maintenance step
--              (wh_schedule_stream_dispositions): due its grace after the later of its seal and the step's clock, so a
--              stream sealed before this migration, or a sealed stream someone restored, gets a full grace rather than an
--              immediate disposition. A partial index holds only those streams (idx_stream_lifecycle_unscheduled), and its
--              predicate's columns are never written by an append, so an append's ledger update stays heap-only. The due
--              streams come back oldest first from idx_stream_lifecycle_due (wh_due_stream_dispositions), each with
--              whether it can go: a perspective that owes an event blocks it, an active destruction hold holds it, and a
--              stream no longer Sealed (archived already) is done. The step moves or clears the schedule
--              (wh_set_stream_disposition_due).
--
--              NewGeneration. A stream type may declare AppendToArchived NewGeneration. An append to a stream of that type
--              archived whole (and not sealed: a sealed stream's refusal is the seal's) starts a new generation when every
--              cursor the archive parked has a snapshot at the event it names: the stream becomes Active again with its
--              archive as a prefix (the locator stays), and the append is numbered from the ledger row's last version, so
--              no archived version is reused. The parked cursors stay where they are; a perspective with no live cursor
--              resumes after its parked one. When a parked cursor has no snapshot at its event, the append is treated as
--              AutoRestore. The policy is decided once (_wh_archived_stream_refusals, which leaves the streams that start a
--              new generation out of its refusals) for every write path; _wh_start_new_generations does the start. The
--              emit chains and the direct append read the ledger's last version only for a stream with nothing hot in a
--              write that held an archived stream, so every other write costs what it did.
--
--              The write paths that call _wh_start_new_generations (the outbox and inbox emit chains and wh_append_event) are
--              redefined in 296, after 285 and 295 redefine them.
--
--              Number 280: reserved for this change (281 to 284, also reserved, are unused).
--
-- Dependencies: 260 (wh_stream_lifecycle, idx_stream_lifecycle_due), 262 (wh_event_archive_cursors), 271 (last word of
--               _wh_archived_stream_refusals; wh_stream_type_policies), 266 (wh_finalize_stream: sealed_at)
-- Objects: ck_stream_type_policies_append_to_archived, idx_stream_lifecycle_unscheduled, _wh_new_generation_streams,
--          _wh_archived_stream_refusals, _wh_start_new_generations, wh_perspective_resume_cursors,
--          wh_schedule_stream_dispositions, wh_due_stream_dispositions, wh_set_stream_disposition_due
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- ===========================================================================================
-- The NewGeneration policy
-- ===========================================================================================

-- The stored policies admit the third value. Dropped and added, so a re-run ends on the same constraint.
ALTER TABLE __SCHEMA__.wh_stream_type_policies DROP CONSTRAINT IF EXISTS ck_stream_type_policies_append_to_archived;
ALTER TABLE __SCHEMA__.wh_stream_type_policies ADD CONSTRAINT ck_stream_type_policies_append_to_archived
  CHECK (append_to_archived IN ('Refuse', 'AutoRestore', 'NewGeneration'));

SELECT __SCHEMA__.drop_all_overloads('_wh_new_generation_streams');

-- The streams among p_stream_ids that an append starts a new generation of: archived whole, not sealed, of a type whose
-- policy is NewGeneration, and with a snapshot at the event every parked cursor names (the final snapshot a perspective
-- resumes from). Reads only; the caller holds the stream locks.
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_EveryParkedCursorHasItsSnapshot_StoresAtTheNextVersion_AndTheArchiveBecomesAPrefixAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_AParkedCursorWithoutASnapshotAtIt_FallsBackToAutoRestoreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_NoPerspectiveEverConsumedTheStream_StartsANewGenerationAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_new_generation_streams(p_stream_ids UUID[])
RETURNS TABLE(new_generation_stream_id UUID) AS $$
BEGIN
  RETURN QUERY
  SELECT l.stream_id
  FROM __SCHEMA__.wh_stream_lifecycle l
  JOIN __SCHEMA__.wh_stream_type_policies p ON p.stream_type = l.stream_type
  WHERE l.stream_id = ANY(p_stream_ids)
    AND l.state = 'Archived'
    AND l.sealed_at IS NULL
    AND p.append_to_archived = 'NewGeneration'
    AND NOT EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_event_archive_cursors pc
      WHERE pc.stream_id = l.stream_id
        AND NOT EXISTS (
          SELECT 1
          FROM __SCHEMA__.wh_perspective_snapshots sn
          WHERE sn.stream_id = pc.stream_id
            AND sn.perspective_name = pc.perspective_name
            AND sn.snapshot_event_id = (pc.cursor_row->>'last_event_id')::uuid));
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_new_generation_streams(UUID[]) IS
  'The streams an append starts a new generation of (#1734): archived whole, not sealed, of a NewGeneration type, and '
  'with a snapshot at the event every parked cursor names. Reads only.';

-- _wh_archived_stream_refusals: last word 271, leaving the streams that start a new generation out, and reading
-- NewGeneration without a snapshot at every parked cursor as AutoRestore.
SELECT __SCHEMA__.drop_all_overloads('_wh_archived_stream_refusals');

-- The events of a write (parallel arrays) that an archived stream refuses, with the policy its type declares. A sealed
-- stream is the seal's to refuse (266 runs first), so it is left out here, and so is a stream the write starts a new
-- generation of (280). Reads only; the caller holds the stream locks.
-- <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_Refuse_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveAppendPolicySqlTests.cs:OutboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesOneRequestPerStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_AParkedCursorWithoutASnapshotAtIt_IsDeadLetteredAwaitingRestoreAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_archived_stream_refusals(p_event_ids UUID[], p_stream_ids UUID[])
RETURNS TABLE(refused_event_id UUID, refused_stream_id UUID, append_policy TEXT, refusal_text TEXT) AS $$
BEGIN
  RETURN QUERY
  WITH policy AS (
    SELECT c.event_id, c.stream_id, l.last_version,
           CASE WHEN p.append_to_archived = 'NewGeneration' THEN 'AutoRestore'
                ELSE COALESCE(p.append_to_archived, 'Refuse') END AS append_policy
    FROM unnest(p_event_ids, p_stream_ids) AS c(event_id, stream_id)
    JOIN __SCHEMA__.wh_stream_lifecycle l ON l.stream_id = c.stream_id
    LEFT JOIN __SCHEMA__.wh_stream_type_policies p ON p.stream_type = l.stream_type
    WHERE l.state = 'Archived'
      AND l.sealed_at IS NULL
      AND c.stream_id NOT IN (SELECT g.new_generation_stream_id FROM __SCHEMA__._wh_new_generation_streams(p_stream_ids) g)
  )
  SELECT pl.event_id,
         pl.stream_id,
         pl.append_policy,
         __SCHEMA__._wh_stream_archived_refusal_text(pl.stream_id, pl.last_version, pl.append_policy)
  FROM policy pl
  ORDER BY pl.stream_id, pl.event_id;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_archived_stream_refusals(UUID[], UUID[]) IS
  'Finds the events of a write that an archived (and unsealed) stream refuses, with the AppendToArchived policy of the '
  'stream''s type (wh_stream_type_policies; Refuse when the type has no row) and the refusal''s wording (#1738). Reads only. '
  '280: a stream the write starts a new generation of is not refused, and NewGeneration without a snapshot at every parked '
  'cursor reads AutoRestore (#1734).';

SELECT __SCHEMA__.drop_all_overloads('_wh_start_new_generations');

-- Starts the new generation of each stream among p_stream_ids that _wh_new_generation_streams names: the stream becomes
-- Active again and keeps its locator, so its archive reads as a prefix. Nothing else changes: the archived events, the
-- parked cursors and the snapshots (with their archive marks) stay as they are. The caller holds the stream locks and
-- numbers the append from the row's last version.
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:DirectAppend_EveryParkedCursorHasItsSnapshot_StoresAtTheNextVersion_AndTheArchiveBecomesAPrefixAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:OutboxAppend_StoresTheEventAtTheNextVersion_AndDeadLettersNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:InboxAppend_StoresTheReceivedEventAtTheNextVersionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_start_new_generations(p_stream_ids UUID[])
RETURNS INTEGER AS $$
DECLARE
  v_count INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET state = 'Active'
  WHERE l.stream_id IN (SELECT g.new_generation_stream_id FROM __SCHEMA__._wh_new_generation_streams(p_stream_ids) g);
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_start_new_generations(UUID[]) IS
  'Starts the new generation of each NewGeneration stream an append reaches (#1734): Active again, its archive kept as a '
  'prefix. Returns how many streams started one.';

SELECT __SCHEMA__.drop_all_overloads('wh_perspective_resume_cursors');

-- Where each perspective resumes on the given streams (p_perspective_name NULL for every perspective): its live cursor,
-- else the cursor a whole-stream archive parked. A stream that started a new generation keeps its cursors parked, so a
-- perspective resumes after the event its parked cursor names until it moves on and has a live cursor of its own. Both
-- coordinators read cursors through it.
-- <docs>fundamentals/events/archive-store#new-generation</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveNewGenerationSqlTests.cs:GetPerspectiveCursor_WithNoLiveCursor_ReadsTheParkedOne_AndALiveOneWinsAsync</tests>
-- <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamDispositionTests.cs:AppendAsync_NewGeneration_ContinuesAtTheNextVersion_AndTheCoordinatorResumesFromTheParkedCursorAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_perspective_resume_cursors(p_stream_ids UUID[], p_perspective_name TEXT)
RETURNS TABLE(stream_id UUID, perspective_name TEXT, last_event_id UUID, status SMALLINT, rewind_trigger_event_id UUID) AS $$
  SELECT c.stream_id, c.perspective_name::TEXT, c.last_event_id, c.status::SMALLINT, c.rewind_trigger_event_id
  FROM __SCHEMA__.wh_perspective_cursors c
  WHERE c.stream_id = ANY(p_stream_ids)
    AND (p_perspective_name IS NULL OR c.perspective_name = p_perspective_name)
  UNION ALL
  SELECT pc.stream_id, pc.perspective_name, (pc.cursor_row->>'last_event_id')::UUID, (pc.cursor_row->>'status')::SMALLINT,
         (pc.cursor_row->>'rewind_trigger_event_id')::UUID
  FROM __SCHEMA__.wh_event_archive_cursors pc
  WHERE pc.stream_id = ANY(p_stream_ids)
    AND (p_perspective_name IS NULL OR pc.perspective_name = p_perspective_name)
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_perspective_cursors x
      WHERE x.stream_id = pc.stream_id AND x.perspective_name = pc.perspective_name);
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_perspective_resume_cursors(UUID[], TEXT) IS
  'Where each perspective resumes on the given streams: its live cursor, else the one a whole-stream archive parked '
  '(#1734, NewGeneration).';

-- ===========================================================================================
-- The scheduled disposition of sealed streams
-- ===========================================================================================

-- The sealed streams no disposition is scheduled for: what the scheduler reads, so it never visits a stream it already
-- scheduled or one that is open. Its predicate's columns (state, disposition_due_at) are never written by an append.
CREATE INDEX IF NOT EXISTS idx_stream_lifecycle_unscheduled
  ON __SCHEMA__.wh_stream_lifecycle (stream_id)
  WHERE state = 'Sealed' AND disposition_due_at IS NULL;

SELECT __SCHEMA__.drop_all_overloads('wh_schedule_stream_dispositions');

-- Schedules up to p_limit sealed streams with no disposition scheduled: due p_grace after the later of the seal and
-- p_now. Counting from the later of the two gives a stream sealed before scheduling existed, and a sealed stream someone
-- restored, a full grace rather than an immediate disposition. Returns how many were scheduled.
-- <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs:Schedule_ASealedStreamWithNoSchedule_IsDueAGraceAfterTheLaterOfItsSealAndNow_AndNothingElseIsTouchedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs:Schedule_TakesAtMostItsLimit_AndTheNextCallTheRestAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_schedule_stream_dispositions(p_grace INTERVAL, p_limit INTEGER, p_now TIMESTAMPTZ)
RETURNS INTEGER AS $$
DECLARE
  v_count INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET disposition_due_at = GREATEST(l.sealed_at, p_now) + p_grace
  WHERE l.stream_id IN (
    SELECT u.stream_id
    FROM __SCHEMA__.wh_stream_lifecycle u
    WHERE u.state = 'Sealed' AND u.disposition_due_at IS NULL
    ORDER BY u.stream_id
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED);
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_schedule_stream_dispositions(INTERVAL, INTEGER, TIMESTAMPTZ) IS
  'Schedules up to p_limit sealed streams with no disposition: due p_grace after the later of the seal and p_now (#1734). '
  'Returns how many were scheduled.';

SELECT __SCHEMA__.drop_all_overloads('wh_due_stream_dispositions');

-- The streams whose disposition is due at p_now, oldest first, from idx_stream_lifecycle_due, each with whether it can go,
-- as StreamDispositionReadiness numbers it: 3 (Done) when it is no longer Sealed (archived already), 1 (Blocked) while a
-- perspective owes one of its events, 2 (Held) while an active destruction hold covers one, else 0 (Ready). The same gates a whole-stream archive applies, read first so
-- the final snapshot is taken only once every perspective has consumed the stream.
-- <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs:GetDue_ReturnsTheDueStreamsOldestFirst_WithWhetherEachCanGo_AndOnlyUpToItsLimitAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs:Step_ArchivesASealedStreamWhole_OnceItsGraceHasPassed_AndNotBeforeAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_due_stream_dispositions(p_now TIMESTAMPTZ, p_limit INTEGER)
RETURNS TABLE(due_stream_id UUID, readiness INTEGER) AS $$
BEGIN
  RETURN QUERY
  SELECT d.stream_id,
         CASE
           WHEN d.state <> 'Sealed' THEN 3
           WHEN EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_perspective_events pe
             WHERE pe.stream_id = d.stream_id AND pe.processed_at IS NULL) THEN 1
           WHEN EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_event_destruction_hold h
             JOIN __SCHEMA__.wh_event_store es ON es.event_id = h.event_id
             WHERE es.stream_id = d.stream_id AND h.hold_until > NOW()) THEN 2
           ELSE 0
         END
  FROM (
    SELECT l.stream_id, l.state, l.disposition_due_at
    FROM __SCHEMA__.wh_stream_lifecycle l
    WHERE l.disposition_due_at <= p_now
    ORDER BY l.disposition_due_at, l.stream_id
    LIMIT p_limit
  ) d
  ORDER BY d.disposition_due_at, d.stream_id;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_due_stream_dispositions(TIMESTAMPTZ, INTEGER) IS
  'The streams whose disposition is due, oldest first, each 0 Ready, 1 Blocked (a perspective owes an event), 2 Held (an '
  'active destruction hold) or 3 Done (no longer Sealed) (#1734). Reads only.';

SELECT __SCHEMA__.drop_all_overloads('wh_set_stream_disposition_due');

-- Moves a stream's disposition to p_due, or clears it when p_due is NULL (the disposition ran).
-- <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs:SetDue_MovesAndClearsTheSchedule_AndGetCursorsListsEachPerspectivesPositionAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_set_stream_disposition_due(p_stream_id UUID, p_due TIMESTAMPTZ)
RETURNS VOID AS $$
BEGIN
  UPDATE __SCHEMA__.wh_stream_lifecycle l
  SET disposition_due_at = p_due
  WHERE l.stream_id = p_stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_set_stream_disposition_due(UUID, TIMESTAMPTZ) IS
  'Moves a stream''s disposition to p_due, or clears it when p_due is NULL (#1734).';
