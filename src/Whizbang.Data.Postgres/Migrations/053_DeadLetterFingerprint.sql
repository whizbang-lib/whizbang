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
-- TABLE — fine for FRESH databases. But existing databases (e.g. a consumer's
-- production deployment) already ran the old 050 and have a wh_dead_letters
-- table WITHOUT those columns; CREATE TABLE IF NOT EXISTS is a no-op on re-run.
--
-- The columns MUST be added via ALTER TABLE here so the DDL flows in the
-- correct order: 050 re-runs (no-op on table, can't create the partial index
-- because the column doesn't exist yet), 053 runs (ALTER TABLE adds the
-- columns, then the partial index can be created safely).
--
-- IF NOT EXISTS makes both statements idempotent — no-op on fresh DBs where
-- 050's CREATE TABLE already provided the columns and 053 hasn't run before.
-- Production root cause: a CrashLoopBackOff incident when 050's hash
-- changed and the migration runner re-applied it, hitting a CREATE INDEX on
-- a column 053 hadn't added yet. Lesson: when editing existing migrations in
-- place, dependent DDL belongs in the new migration, not the edited one.
ALTER TABLE __SCHEMA__.wh_dead_letters
  ADD COLUMN IF NOT EXISTS error_fingerprint VARCHAR(16) NULL,
  ADD COLUMN IF NOT EXISTS error_fingerprint_version SMALLINT NULL;

-- Partial index supports the canonical operator/AI triage query
-- `SELECT error_fingerprint, COUNT(*) FROM wh_dead_letters
--  WHERE error_fingerprint IS NOT NULL GROUP BY 1`.
-- WHERE NOT NULL keeps the index skinny on NULL-fingerprint rows.
CREATE INDEX IF NOT EXISTS wh_dead_letters_fingerprint_idx
  ON __SCHEMA__.wh_dead_letters (error_fingerprint)
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
AS $$ SELECT 2::SMALLINT $$;

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
  v_fallback_frame  TEXT;
  v_combined        TEXT;
  v_template        TEXT;
  v_line_idx        INTEGER := 0;
  v_inner           TEXT[];
BEGIN
  -- v2 (P2 of plans/dlq-stack-intelligence.md). Three changes over v1, each a measured
  -- cohort-identity failure:
  --   1. Async state machines normalize: <Method>d__N carries a compiler-assigned N that
  --      changes on recompile - a cohort split by a rebuild breaks canary campaigns.
  --   2. The INNERMOST exception type wins: the wrapper is plumbing, the inner type is
  --      the failure.
  --   3. Prose errors (no frames - 100% of the 2026-09-03 corpus) hash a SCRUBBED
  --      first-line template: quoted strings, GUIDs, hex and digit runs become
  --      placeholders with exact character placement, so volatile values can never split
  --      a template cohort and different templates never collapse by first-word accident.
  IF p_error_text IS NULL THEN
    RETURN NULL;
  END IF;

  v_lines := string_to_array(p_error_text, E'\n');
  v_first_line := COALESCE(v_lines[1], '');

  -- Innermost exception type: walk every '---> Some.Type :' and keep the LAST (inner in
  -- .NET's outer-first rendering). Else a first-line token only when it actually looks
  -- like an exception type - a bare PascalCase prose word is NOT a type; that heuristic
  -- is what made v1 mushy on prose.
  FOR v_inner IN SELECT m FROM regexp_matches(p_error_text, '--->\s*([A-Za-z0-9_.]+)\s*:', 'g') AS m LOOP
    v_type := v_inner[1];
  END LOOP;
  IF v_type IS NULL THEN
    v_type := (regexp_match(v_first_line, '(^|[\s(])([A-Za-z0-9_.]*Exception)'))[2];
  END IF;

  -- Frames: normalize async state machinery, prefer consumer frames; the deepest
  -- non-BCL frame is the fallback when nothing outside the framework survives -
  -- discrimination lives in exclusions, not frame count.
  FOREACH v_line IN ARRAY v_lines LOOP
    v_line_idx := v_line_idx + 1;
    IF v_line_idx = 1 THEN
      CONTINUE;
    END IF;
    v_frame := (regexp_match(v_line, '^\s+at\s+([^\s(]+)'))[1];
    IF v_frame IS NULL THEN
      CONTINUE;
    END IF;
    v_frame := regexp_replace(v_frame, '<([A-Za-z0-9_]+)>d__[0-9]+', '\1', 'g');
    v_frame := regexp_replace(v_frame, '\.MoveNext$', '');
    IF v_frame ~ '^(Microsoft|System|Npgsql)\.' THEN
      CONTINUE;
    END IF;
    IF v_frame ~ '^Whizbang\.' THEN
      IF v_fallback_frame IS NULL THEN
        v_fallback_frame := v_frame;
      END IF;
      CONTINUE;
    END IF;
    v_in_app_frames := array_append(v_in_app_frames, v_frame);
    EXIT WHEN array_length(v_in_app_frames, 1) >= 3;
  END LOOP;
  IF array_length(v_in_app_frames, 1) IS NULL AND v_fallback_frame IS NOT NULL THEN
    v_in_app_frames := ARRAY[v_fallback_frame];
  END IF;

  IF array_length(v_in_app_frames, 1) IS NOT NULL OR v_type IS NOT NULL THEN
    v_combined := COALESCE(v_type, '');
    FOREACH v_frame IN ARRAY v_in_app_frames LOOP
      v_combined := v_combined || ':' || v_frame;
    END LOOP;
  ELSE
    -- Prose template: quoted strings first (they may contain digits), then GUIDs (before
    -- generic hex), then long hex runs, then digit runs; whitespace collapses; 160 chars
    -- bounds the key.
    v_template := v_first_line;
    v_template := regexp_replace(v_template, $q$'[^']*'$q$, '<q>', 'g');
    v_template := regexp_replace(v_template, '"[^"]*"', '<q>', 'g');
    v_template := regexp_replace(v_template,
      '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '<g>', 'g');
    v_template := regexp_replace(v_template, '[0-9a-fA-F]{8,}', '<h>', 'g');
    v_template := regexp_replace(v_template, '[0-9]+', '<n>', 'g');
    v_template := regexp_replace(v_template, '\s+', ' ', 'g');
    v_combined := 'prose:' || substring(v_template FROM 1 FOR 160);
  END IF;

  RETURN substring(encode(sha256(v_combined::bytea), 'hex') FROM 1 FOR 16);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.compute_dead_letter_fingerprint IS
'Algorithm v2 (P2 of plans/dlq-stack-intelligence.md). Typed errors hash "innermostType:frame1..frame3" with async state machines normalized (<M>d__N.MoveNext -> M) and consumer frames preferred (deepest Whizbang frame as fallback); prose errors hash a scrubbed first-line template so volatile values never split a cohort. First 16 hex chars of SHA256. Called by Slice 3''s move_to_dead_letters extension (live capture) and Slice 6''s aggregate_dead_letters (version-aware backfill). NULL input → NULL output. See operations/dead-letter-queue/error-fingerprinting docs page for the algorithm rationale, exclusions, and version bump procedure.';

-- ============================================================================
-- 4. wh_dead_letter_summary table (Slice 6)
-- ============================================================================
-- Operator-facing rollup of raw wh_dead_letters by (fingerprint, source, message_type).
-- Collapses a tens-of-thousands-row DLQ from a consumer's service into ~dozens of distinct clusters with counts,
-- first/last seen timestamps, and a representative sample error_text per cluster.
-- Refreshed by aggregate_dead_letters() called from migration 032's
-- perform_maintenance() (every 10 min default).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_dead_letter_summary (
  error_fingerprint  VARCHAR(16) NOT NULL,
  source_table       TEXT NOT NULL,
  message_type       TEXT NOT NULL,
  occurrence_count   BIGINT NOT NULL,
  first_seen_at      TIMESTAMPTZ NOT NULL,
  last_seen_at       TIMESTAMPTZ NOT NULL,
  sample_error_text  TEXT NOT NULL,
  PRIMARY KEY (error_fingerprint, source_table, message_type)
);

COMMENT ON TABLE __SCHEMA__.wh_dead_letter_summary IS
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
  UPDATE __SCHEMA__.wh_dead_letters
  SET error_fingerprint = __SCHEMA__.compute_dead_letter_fingerprint(error_text),
      error_fingerprint_version = __SCHEMA__.current_dead_letter_fingerprint_version()
  WHERE error_text IS NOT NULL
    AND (error_fingerprint_version IS NULL
         OR error_fingerprint_version < __SCHEMA__.current_dead_letter_fingerprint_version());

  -- Step (b): aggregate. INSERT ... ON CONFLICT DO UPDATE so a fresh maintenance
  -- tick refreshes occurrence_count and last_seen_at without orphaning the
  -- first_seen_at value from the very first cluster appearance.
  INSERT INTO __SCHEMA__.wh_dead_letter_summary (
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
  FROM __SCHEMA__.wh_dead_letters
  WHERE error_fingerprint IS NOT NULL
    AND error_text IS NOT NULL
  GROUP BY error_fingerprint, source_table, message_type
  ON CONFLICT (error_fingerprint, source_table, message_type) DO UPDATE
  SET occurrence_count = EXCLUDED.occurrence_count,
      first_seen_at = LEAST(__SCHEMA__.wh_dead_letter_summary.first_seen_at, EXCLUDED.first_seen_at),
      last_seen_at = GREATEST(__SCHEMA__.wh_dead_letter_summary.last_seen_at, EXCLUDED.last_seen_at),
      sample_error_text = EXCLUDED.sample_error_text;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.aggregate_dead_letters IS
'Slice 6 of release/v0.645.0-alpha.1 — refreshes wh_dead_letter_summary. (1) Version-aware backfill: re-hashes raw rows with stale fingerprint_version, leaves current-version rows alone (O(new+stale), not O(all)). (2) GROUP BY upsert into the summary table. Called from perform_maintenance every 10 min default.';
