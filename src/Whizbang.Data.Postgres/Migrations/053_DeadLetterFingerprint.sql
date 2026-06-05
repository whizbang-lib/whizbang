-- Migration: 053_DeadLetterFingerprint.sql
-- Date: 2026-06-05 (release/v0.645.0-alpha.1 — Slice 2 of outbox-DLQ + dual-hash analysis)
-- Description: Adds the SQL fingerprint utility used by Slice 3's live capture
--              (move_to_dead_letters INSERT extension) and Slice 6's version-aware
--              aggregation. Single source of truth — no parallel C# implementation —
--              so the algorithm cannot drift between live capture and analysis.
--
--              Algorithm v1 (locked):
--                1. Read exception type token from first dotted-PascalCase identifier
--                   on line 1 of error_text (e.g. "System.InvalidOperationException").
--                2. Extract frame tokens from subsequent lines matching
--                   '^\s+at\s+([^\s(]+)' — strips trailing "(args)" and any whitespace
--                   suffix so the token is a clean dotted identifier.
--                3. Exclude frames whose first dotted segment is Microsoft / System /
--                   Npgsql, AND frames starting with Whizbang.Core.Workers. /
--                   Whizbang.Core.Messaging.Internal. (the catch-and-forward sites,
--                   never the actual fault origin).
--                4. Take the first 3 surviving frames; fewer is fine.
--                5. Concatenate "type:frame1:frame2:frame3" with literal colons.
--                6. SHA256 the UTF-8 bytes, return first 16 hex characters lowercase.
--
--              NULL input → NULL output so wh_dead_letters.error_fingerprint stays
--              NULL when error_text is NULL (no spurious "all-nulls" cluster).
--
--              Versioning: current_dead_letter_fingerprint_version() returns 1.
--              Bumping the algorithm = updating the function body + bumping this
--              version. Slice 6's aggregate_dead_letters() WHERE clause keys off
--              this version so the version-aware backfill re-hashes only stale rows.
--
-- Dependencies: 001-052 (wh_dead_letters table from 050; pgcrypto NOT required —
--               uses Postgres core sha256() introduced in PG 11)

-- ============================================================================
-- 1. wh_dead_letters fingerprint columns + partial index (DDL on existing tables)
-- ============================================================================
-- Migration 050 was edited in place per project_pre_v1_migrations to add
-- error_fingerprint and error_fingerprint_version to wh_dead_letters' CREATE
-- TABLE — fine for FRESH databases. But existing databases (e.g. a consumer BFF
-- production) already ran the old 050 and have a wh_dead_letters table WITHOUT
-- those columns; CREATE TABLE IF NOT EXISTS is a no-op on re-run.
--
-- The columns MUST be added via ALTER TABLE here so the DDL flows in the
-- correct order: 050 re-runs (no-op on table, can't create the partial index
-- because the column doesn't exist yet), 053 runs (ALTER TABLE adds the
-- columns, then the partial index can be created safely).
--
-- IF NOT EXISTS makes both statements idempotent — no-op on fresh DBs where
-- 050's CREATE TABLE already provided the columns and 053 hasn't run before.
-- production root cause: production CrashLoopBackOff on Jun-2026 when 050's hash
-- changed and the migration runner re-applied it, hitting a CREATE INDEX on
-- a column 053 hadn't added yet. Lesson: when editing existing migrations in
-- place, dependent DDL belongs in the new migration, not the edited one.
ALTER TABLE wh_dead_letters
  ADD COLUMN IF NOT EXISTS error_fingerprint VARCHAR(16) NULL,
  ADD COLUMN IF NOT EXISTS error_fingerprint_version SMALLINT NULL;

-- Partial index supports the canonical operator/AI triage query
-- `SELECT error_fingerprint, COUNT(*) FROM wh_dead_letters
--  WHERE error_fingerprint IS NOT NULL GROUP BY 1`.
-- WHERE NOT NULL keeps the index skinny on NULL-fingerprint rows.
CREATE INDEX IF NOT EXISTS wh_dead_letters_fingerprint_idx
  ON wh_dead_letters (error_fingerprint)
  WHERE error_fingerprint IS NOT NULL;

-- ============================================================================
-- 2. current_dead_letter_fingerprint_version()
-- ============================================================================
-- Returns the current fingerprint algorithm version. Slice 6's aggregator uses
-- this inside its WHERE clause to identify rows that need re-hashing after a
-- version bump. Keeping this as a function (not a hardcoded literal) means
-- "bump the algorithm version" is a one-line edit here + algorithm-body edit
-- in compute_dead_letter_fingerprint — no consumer-side changes.

SELECT __SCHEMA__.drop_all_overloads('current_dead_letter_fingerprint_version');

CREATE OR REPLACE FUNCTION __SCHEMA__.current_dead_letter_fingerprint_version()
RETURNS SMALLINT
LANGUAGE SQL
IMMUTABLE
AS $$ SELECT 1::SMALLINT $$;

COMMENT ON FUNCTION __SCHEMA__.current_dead_letter_fingerprint_version IS
'Returns the current dead-letter fingerprint algorithm version (Slice 2 of release/v0.645.0-alpha.1). Bumping = one-line edit here + algorithm body edit in compute_dead_letter_fingerprint + the version-aware backfill in aggregate_dead_letters re-hashes every stale row on the next maintenance tick.';

-- ============================================================================
-- 3. compute_dead_letter_fingerprint(p_error_text TEXT) RETURNS TEXT
-- ============================================================================
-- Pure function. Same input → same output. IMMUTABLE so the optimizer can fold
-- it and Slice 8's round-trip lock can use it inside a WHERE predicate without
-- per-row cost concerns.

SELECT __SCHEMA__.drop_all_overloads('compute_dead_letter_fingerprint');

CREATE OR REPLACE FUNCTION __SCHEMA__.compute_dead_letter_fingerprint(p_error_text TEXT)
RETURNS TEXT
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
  v_lines           TEXT[];
  v_first_line      TEXT;
  v_line            TEXT;
  v_type            TEXT;
  v_frame           TEXT;
  v_in_app_frames   TEXT[] := ARRAY[]::TEXT[];
  v_combined        TEXT;
  v_line_idx        INTEGER := 0;
BEGIN
  -- Step 0: NULL passthrough. Keeps wh_dead_letters.error_fingerprint NULL
  -- when the caller has no error text to fingerprint.
  IF p_error_text IS NULL THEN
    RETURN NULL;
  END IF;

  v_lines := string_to_array(p_error_text, E'\n');

  -- Step 1: Exception type from first line. First dotted PascalCase identifier
  -- — matches "System.InvalidOperationException", "InvalidOperationException",
  -- "Whizbang.Core.Messaging.LifecycleException", etc. Falls back to empty
  -- string when the first line has no recognizable type token (e.g. pure
  -- prose error from a non-CLR producer).
  v_first_line := COALESCE(v_lines[1], '');
  v_type := COALESCE(
    (regexp_match(v_first_line, '([A-Z][A-Za-z0-9_]+(\.[A-Z][A-Za-z0-9_]+)*)'))[1],
    ''
  );

  -- Step 2-4: Walk remaining lines, capture frame tokens with
  -- '^\s+at\s+([^\s(]+)' (stops at first whitespace OR open-paren so
  -- "OutboxClaim.LeaseAsync(Guid instanceId)" yields "OutboxClaim.LeaseAsync"
  -- cleanly). Exclude framework + Whizbang catchers. Stop at 3 survivors.
  FOREACH v_line IN ARRAY v_lines LOOP
    v_line_idx := v_line_idx + 1;
    IF v_line_idx = 1 THEN
      CONTINUE;  -- skip the type/message line
    END IF;

    v_frame := (regexp_match(v_line, '^\s+at\s+([^\s(]+)'))[1];
    IF v_frame IS NULL THEN
      CONTINUE;
    END IF;

    -- Exclude framework frames: .NET patch versions, Npgsql client patches.
    IF v_frame ~ '^(Microsoft|System|Npgsql)\.' THEN
      CONTINUE;
    END IF;

    -- Exclude Whizbang catch-and-forward sites: these never throw the original
    -- fault, only re-raise from one stack frame down. Treating them as in-app
    -- would mask the actual thrower.
    IF v_frame LIKE 'Whizbang.Core.Workers.%'
       OR v_frame LIKE 'Whizbang.Core.Messaging.Internal.%' THEN
      CONTINUE;
    END IF;

    v_in_app_frames := array_append(v_in_app_frames, v_frame);
    EXIT WHEN array_length(v_in_app_frames, 1) >= 3;
  END LOOP;

  -- Step 5: Concatenate. Omits trailing separators when fewer than 3 frames
  -- survive (so a type-only stack hashes the type alone, not "type:::").
  v_combined := v_type;
  FOREACH v_frame IN ARRAY v_in_app_frames LOOP
    v_combined := v_combined || ':' || v_frame;
  END LOOP;

  -- Step 6: SHA256 → first 16 hex chars. Postgres core sha256() (PG 11+);
  -- no pgcrypto required. 2^64 fingerprint space is ample for realistic DLQ
  -- volumes (birthday-bound ~4B rows before first expected collision).
  RETURN substring(encode(sha256(v_combined::bytea), 'hex') FROM 1 FOR 16);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_dead_letter_fingerprint IS
'Algorithm v1 (Slice 2 of release/v0.645.0-alpha.1). Hashes "type:frame1:frame2:frame3" (excluding framework + Whizbang catch-site frames) and returns the first 16 hex chars of SHA256. Called by Slice 3''s move_to_dead_letters extension (live capture) and Slice 6''s aggregate_dead_letters (version-aware backfill). NULL input → NULL output. See operations/dead-letter-queue/error-fingerprinting docs page for the algorithm rationale, exclusions, and version bump procedure.';

-- ============================================================================
-- 4. wh_dead_letter_summary table (Slice 6)
-- ============================================================================
-- Operator-facing rollup of raw wh_dead_letters by (fingerprint, source, message_type).
-- Collapses the 38k+ row a consumer BFF DLQ into ~dozens of distinct clusters with counts,
-- first/last seen timestamps, and a representative sample error_text per cluster.
-- Refreshed by aggregate_dead_letters() called from migration 032's
-- perform_maintenance() (every 10 min default).

CREATE TABLE IF NOT EXISTS wh_dead_letter_summary (
  error_fingerprint  VARCHAR(16) NOT NULL,
  source_table       TEXT NOT NULL,
  message_type       TEXT NOT NULL,
  occurrence_count   BIGINT NOT NULL,
  first_seen_at      TIMESTAMPTZ NOT NULL,
  last_seen_at       TIMESTAMPTZ NOT NULL,
  sample_error_text  TEXT NOT NULL,
  PRIMARY KEY (error_fingerprint, source_table, message_type)
);

COMMENT ON TABLE wh_dead_letter_summary IS
'Slice 6 of release/v0.645.0-alpha.1 — operator/AI-friendly rollup of wh_dead_letters by (error_fingerprint, source_table, message_type). Refreshed by aggregate_dead_letters() inside perform_maintenance. sample_error_text is the most-recent row''s text for each cluster so the dashboard view tracks current behavior.';

-- ============================================================================
-- 5. aggregate_dead_letters() (Slice 6)
-- ============================================================================
-- Two-step pipeline:
--   (a) Version-aware backfill of error_fingerprint on raw wh_dead_letters rows
--       whose error_fingerprint_version is stale (NULL or below the current
--       algorithm version). Only stale rows are touched — current-version rows
--       are skipped so each maintenance tick is O(new+stale), not O(all).
--   (b) GROUP BY upsert into wh_dead_letter_summary. occurrence_count is the
--       current row count for that cluster; sample_error_text takes the most
--       recent row's error_text per cluster.
--
-- Called from perform_maintenance (migration 032). Idempotent.

SELECT __SCHEMA__.drop_all_overloads('aggregate_dead_letters');

CREATE OR REPLACE FUNCTION __SCHEMA__.aggregate_dead_letters()
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  -- Step (a): version-aware backfill. The WHERE clause selects rows that need
  -- (re-)hashing: NULL version (never tagged) OR a version below the current
  -- algorithm. Rows at the current version are deliberately left alone — they're
  -- already fingerprinted under the current algorithm. Without this skip, every
  -- maintenance tick burns IO re-hashing every row.
  UPDATE wh_dead_letters
  SET error_fingerprint = __SCHEMA__.compute_dead_letter_fingerprint(error_text),
      error_fingerprint_version = __SCHEMA__.current_dead_letter_fingerprint_version()
  WHERE error_text IS NOT NULL
    AND (error_fingerprint_version IS NULL
         OR error_fingerprint_version < __SCHEMA__.current_dead_letter_fingerprint_version());

  -- Step (b): aggregate. INSERT ... ON CONFLICT DO UPDATE so a fresh maintenance
  -- tick refreshes occurrence_count and last_seen_at without orphaning the
  -- first_seen_at value from the very first cluster appearance.
  INSERT INTO wh_dead_letter_summary (
    error_fingerprint, source_table, message_type,
    occurrence_count, first_seen_at, last_seen_at, sample_error_text
  )
  SELECT
    error_fingerprint,
    source_table,
    message_type,
    COUNT(*),
    MIN(dead_lettered_at),
    MAX(dead_lettered_at),
    (array_agg(error_text ORDER BY dead_lettered_at DESC))[1]
  FROM wh_dead_letters
  WHERE error_fingerprint IS NOT NULL
    AND error_text IS NOT NULL
  GROUP BY error_fingerprint, source_table, message_type
  ON CONFLICT (error_fingerprint, source_table, message_type) DO UPDATE
  SET occurrence_count = EXCLUDED.occurrence_count,
      first_seen_at = LEAST(wh_dead_letter_summary.first_seen_at, EXCLUDED.first_seen_at),
      last_seen_at = GREATEST(wh_dead_letter_summary.last_seen_at, EXCLUDED.last_seen_at),
      sample_error_text = EXCLUDED.sample_error_text;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.aggregate_dead_letters IS
'Slice 6 of release/v0.645.0-alpha.1 — refreshes wh_dead_letter_summary. (1) Version-aware backfill: re-hashes raw rows with stale fingerprint_version, leaves current-version rows alone (O(new+stale), not O(all)). (2) GROUP BY upsert into the summary table. Called from perform_maintenance every 10 min default.';
