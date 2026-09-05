-- Migration: 127_DlqCanaryCampaigns
-- Date: 2026-09-03
-- Description: Held-cohort canary campaign persistence (P1 of plans/dlq-stack-intelligence.md).
--   A campaign probes a few rows of a held fingerprint cohort; the cohort releases only when
--   the probes recover. wh_dlq_probe_campaigns is the durable record (one row per
--   fingerprint x generation) a restarted pod resumes from. Probing and releasing only ever
--   return rows to Pending — actual re-drives stay with the paced recovery scans.
-- Dependencies: 050_WhDeadLetters (table), 053_DeadLetterFingerprint (cohort key)
-- Objects: wh_dlq_probe_campaigns, purge_undeliverable_held_dead_letters, list_held_dead_letter_cohorts, begin_canary_probes, evaluate_canary_campaign, release_held_dead_letter_cohort, begin_trickle_wave, count_wave_requarantines

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_dlq_probe_campaigns (
  fingerprint        VARCHAR(16) NOT NULL,
  generation         TEXT        NOT NULL,
  started_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  probe_ids          UUID[]      NOT NULL,
  verdict            INTEGER     NOT NULL DEFAULT 0,   -- CanaryVerdictKind: 0 Pending, 1 Pass, 2 Fail, 3 Mixed
  probes_succeeded   INTEGER     NOT NULL DEFAULT 0,
  probes_failed      INTEGER     NOT NULL DEFAULT 0,
  verdict_at         TIMESTAMPTZ,
  wave               INTEGER     NOT NULL DEFAULT 0,
  wave_started_at    TIMESTAMPTZ,
  PRIMARY KEY (fingerprint, generation)
);

-- Idempotent for databases that ran an earlier revision of this file.
ALTER TABLE __SCHEMA__.wh_dlq_probe_campaigns ADD COLUMN IF NOT EXISTS wave INTEGER NOT NULL DEFAULT 0;
ALTER TABLE __SCHEMA__.wh_dlq_probe_campaigns ADD COLUMN IF NOT EXISTS wave_started_at TIMESTAMPTZ;

COMMENT ON TABLE __SCHEMA__.wh_dlq_probe_campaigns IS
'One canary campaign per (error_fingerprint, build generation): which rows probed, and how the verdict resolved. Durable so a pod restart mid-campaign resumes evaluation instead of minting a second probe set.';

-- ============================================================================
-- purge_undeliverable_held_dead_letters — the grandfather gate
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.purge_undeliverable_held_dead_letters()
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_count INTEGER;
BEGIN
  -- jsonb_typeof, never IS NULL: the column is NOT NULL, and the undeliverable shape is
  -- the JSON null literal (or a scalar) — an envelope that is not an object cannot be
  -- re-driven by any campaign, so campaigns must never count it as material.
  UPDATE __SCHEMA__.wh_dead_letters
  SET recovery_status = 4,  -- PermanentlyFailed: visible in the operator ledger, not deleted
      operator_notes = COALESCE(operator_notes || E'\n', '')
        || 'auto-purged by campaign grandfather gate: envelope is not a re-drivable object'
  WHERE recovery_status = 2
    AND recovered_at IS NULL
    AND jsonb_typeof(envelope) IS DISTINCT FROM 'object';
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.purge_undeliverable_held_dead_letters IS
'Campaign grandfather gate (127): held rows whose envelope is not a JSON object are marked PermanentlyFailed — no recovery can ever re-drive them, and leaving them held would let campaigns probe rows that cannot succeed.';

-- ============================================================================
-- list_held_dead_letter_cohorts — campaign units
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.list_held_dead_letter_cohorts()
RETURNS TABLE(fingerprint VARCHAR(16), row_count BIGINT, message_type_count INTEGER)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  SELECT dl.error_fingerprint, count(*), count(DISTINCT dl.message_type)::INTEGER
  FROM __SCHEMA__.wh_dead_letters dl
  WHERE dl.recovery_status = 2
    AND dl.recovered_at IS NULL
    AND dl.error_fingerprint IS NOT NULL
    AND dl.operator_disposition NOT IN (2, 3)  -- HoldIndefinitely, MarkPermanentlyFailed
  GROUP BY dl.error_fingerprint;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.list_held_dead_letter_cohorts IS
'Held rows grouped by error fingerprint (127) — the units a startup campaign probes or releases. Operator-held dispositions are never campaign material.';

-- ============================================================================
-- begin_canary_probes — idempotent per (fingerprint, generation)
-- ============================================================================
SELECT __SCHEMA__.drop_all_overloads('begin_canary_probes');

CREATE OR REPLACE FUNCTION __SCHEMA__.begin_canary_probes(
  p_fingerprint VARCHAR(16), p_generation TEXT, p_probe_size INTEGER,
  p_generation_budget INTEGER DEFAULT 3)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_probe_ids UUID[];
  v_inserted  INTEGER;
  v_failed_generations INTEGER;
BEGIN
  -- Generation budget: attempt counts are evidence about a BUILD. When campaigns have
  -- already FAILED on p_generation_budget distinct generations, this cohort is permanently
  -- pending an operator decision — return -1 and touch nothing.
  SELECT count(*) INTO v_failed_generations
  FROM __SCHEMA__.wh_dlq_probe_campaigns c
  WHERE c.fingerprint = p_fingerprint AND c.verdict = 2;
  IF v_failed_generations >= p_generation_budget THEN
    RETURN -1;
  END IF;

  -- Stratified pick: round-robin across message types (rn 1 of every type first), newest
  -- rows preferred — a probe set all of one type would hide a cohort that splits by type.
  SELECT array_agg(dead_letter_id) INTO v_probe_ids
  FROM (
    SELECT dl.dead_letter_id
    FROM (
      SELECT dead_letter_id, message_type,
             ROW_NUMBER() OVER (PARTITION BY message_type ORDER BY dead_lettered_at DESC) AS rn
      FROM __SCHEMA__.wh_dead_letters
      WHERE error_fingerprint = p_fingerprint
        AND recovery_status = 2
        AND recovered_at IS NULL
        AND operator_disposition NOT IN (2, 3)
        AND jsonb_typeof(envelope) = 'object'
    ) dl
    ORDER BY dl.rn, dl.message_type
    LIMIT p_probe_size
  ) picked;

  IF v_probe_ids IS NULL THEN
    RETURN 0;
  END IF;

  INSERT INTO __SCHEMA__.wh_dlq_probe_campaigns (fingerprint, generation, probe_ids)
  VALUES (p_fingerprint, p_generation, v_probe_ids)
  ON CONFLICT (fingerprint, generation) DO NOTHING;
  GET DIAGNOSTICS v_inserted = ROW_COUNT;
  IF v_inserted = 0 THEN
    -- Campaign already exists (restart mid-campaign). While any of its probe rows survive,
    -- resume it — minting a second probe set would double-probe. But when the retention
    -- purge has destroyed EVERY probe row (issue #682), "resume" means evaluating an empty
    -- evidence set forever: refresh the unresolved campaign's probe_ids from the surviving
    -- held rows instead, so the campaign regains countable evidence. started_at moves with
    -- the refresh — the re-dead-letter clock must measure the NEW probes, not the old ones.
    IF EXISTS (
      SELECT 1
      FROM __SCHEMA__.wh_dlq_probe_campaigns c
      JOIN __SCHEMA__.wh_dead_letters d ON d.dead_letter_id = ANY(c.probe_ids)
      WHERE c.fingerprint = p_fingerprint AND c.generation = p_generation
    ) THEN
      RETURN 0;
    END IF;
    UPDATE __SCHEMA__.wh_dlq_probe_campaigns c
    SET probe_ids = v_probe_ids, started_at = NOW()
    WHERE c.fingerprint = p_fingerprint AND c.generation = p_generation AND c.verdict = 0;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    IF v_inserted = 0 THEN
      -- Terminal campaign with no surviving probes: its verdict already stands.
      RETURN 0;
    END IF;
  END IF;

  UPDATE __SCHEMA__.wh_dead_letters
  -- Fresh attempt (evidence-scoped, like the observation-window reset below): attempt count
  -- is evidence about the OLD build. Without resetting it, a spent-budget probe is re-held by
  -- the recovery worker's exhaustion check before it ever runs, and the verdict sticks Pending.
  SET recovery_status = 0, next_recovery_at = NOW(), recovery_attempts = 0
  WHERE dead_letter_id = ANY(v_probe_ids);

  -- Observation windows scope to the generation, exactly like attempt budgets: a probed
  -- message sitting AT the redelivery observation bound would re-cross it on its very
  -- first probe redelivery and instantly requarantine — an auto-failed probe that says
  -- nothing about the new build. Fresh window, same bound.
  UPDATE __SCHEMA__.wh_message_deduplication d
  SET observation_count = 0
  WHERE d.message_id IN (
    SELECT dl.source_id FROM __SCHEMA__.wh_dead_letters dl
    WHERE dl.dead_letter_id = ANY(v_probe_ids));

  RETURN COALESCE(array_length(v_probe_ids, 1), 0);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.begin_canary_probes IS
'Starts a canary campaign (127): stratified probe rows return to Pending due-now (the paced scans re-drive them), probed messages'' observation windows reset (generation-scoped evidence), and the campaign row records them. Idempotent per (fingerprint, generation) — a conflict resumes while probe rows survive (returns 0) and refreshes the probe set when the purge destroyed all of them (#682); a cohort whose campaigns failed on p_generation_budget distinct generations returns -1 and touches nothing.';

-- ============================================================================
-- evaluate_canary_campaign — probe arithmetic and durable verdict
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.evaluate_canary_campaign(
  p_fingerprint VARCHAR(16), p_generation TEXT)
RETURNS TABLE(verdict INTEGER, probes_succeeded INTEGER, probes_failed INTEGER, probes_outstanding INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
  v_campaign  __SCHEMA__.wh_dlq_probe_campaigns%ROWTYPE;
  v_succeeded INTEGER := 0;
  v_failed    INTEGER := 0;
  v_outstanding INTEGER := 0;
  v_verdict   INTEGER;
BEGIN
  SELECT * INTO v_campaign
  FROM __SCHEMA__.wh_dlq_probe_campaigns c
  WHERE c.fingerprint = p_fingerprint AND c.generation = p_generation;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 0, 0, 0, 0;
    RETURN;
  END IF;

  -- A probe that re-dead-lettered (a NEWER unrecovered row for the same source id) FAILED,
  -- even if its original row shows recovered — the round trip is the evidence, and coming
  -- back is the failure.
  SELECT
    count(*) FILTER (WHERE NOT redead AND dl.recovered_at IS NOT NULL),
    count(*) FILTER (WHERE redead),
    count(*) FILTER (WHERE NOT redead AND dl.recovered_at IS NULL)
  INTO v_succeeded, v_failed, v_outstanding
  FROM (
    SELECT p.recovered_at, p.dead_letter_id,
           EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_dead_letters n
             WHERE n.source_id = p.source_id
               AND n.dead_letter_id <> p.dead_letter_id
               AND n.dead_lettered_at > v_campaign.started_at
               AND n.recovered_at IS NULL
           ) AS redead
    FROM __SCHEMA__.wh_dead_letters p
    WHERE p.dead_letter_id = ANY(v_campaign.probe_ids)
  ) dl;

  IF v_succeeded + v_failed + v_outstanding = 0 THEN
    -- Issue #682: every probe row is GONE — the retention purge can destroy evidence out
    -- from under a live campaign, and rows can be deleted by operators. Zero evidence must
    -- never resolve: the failed=0 branch below would return Pass vacuously (and the
    -- symmetric partial-purge skew condemns half-passed cohorts as Fail). Report Pending;
    -- the worker re-probes via begin_canary_probes, which refreshes a probe-less
    -- unresolved campaign from the surviving held rows.
    RETURN QUERY SELECT 0, 0, 0, 0;
    RETURN;
  END IF;

  IF v_outstanding > 0 THEN
    v_verdict := 0;  -- Pending
  ELSIF v_failed = 0 THEN
    v_verdict := 1;  -- Pass
  ELSIF v_succeeded = 0 THEN
    v_verdict := 2;  -- Fail
  ELSE
    v_verdict := 3;  -- Mixed
  END IF;

  IF v_verdict <> 0 THEN
    -- Terminal verdicts persist once; an already-resolved campaign is never overwritten.
    UPDATE __SCHEMA__.wh_dlq_probe_campaigns c
    SET verdict = v_verdict, probes_succeeded = v_succeeded, probes_failed = v_failed,
        verdict_at = NOW()
    WHERE c.fingerprint = p_fingerprint AND c.generation = p_generation AND c.verdict = 0;
  END IF;

  RETURN QUERY SELECT v_verdict, v_succeeded, v_failed, v_outstanding;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.evaluate_canary_campaign IS
'Campaign probe arithmetic (127): recovered-and-stayed-recovered probes succeed; a probe whose message dead-lettered again after the campaign started failed regardless of its own row; anything else is outstanding. An empty evidence set (all probe rows destroyed) resolves to Pending, never to a terminal verdict (#682). Terminal verdicts persist exactly once.';

-- ============================================================================
-- release_held_dead_letter_cohort — staggered eligibility, never a firehose
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.release_held_dead_letter_cohort(
  p_fingerprint VARCHAR(16), p_stagger_seconds INTEGER)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_count INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_dead_letters
  SET recovery_status = 0,
      recovery_attempts = 0,  -- fresh attempt, else the released cohort re-holds on exhaustion
      next_recovery_at = NOW() + (random() * GREATEST(p_stagger_seconds, 0)) * INTERVAL '1 second'
  WHERE error_fingerprint = p_fingerprint
    AND recovery_status = 2
    AND recovered_at IS NULL
    AND operator_disposition NOT IN (2, 3)
    AND jsonb_typeof(envelope) = 'object';
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.release_held_dead_letter_cohort IS
'Releases a held cohort (127) as STAGGERED eligibility: rows return to Pending with next_recovery_at spread across the window, and the paced recovery scans drain them under arbitration. Release is never a re-drive.';

-- ============================================================================
-- begin_trickle_wave — one bounded, staggered wave of a Mixed cohort
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.begin_trickle_wave(
  p_fingerprint VARCHAR(16), p_generation TEXT, p_wave_size INTEGER)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_count INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_dead_letters dl
  SET recovery_status = 0,
      recovery_attempts = 0,  -- fresh attempt, else the trickled rows re-hold on exhaustion
      next_recovery_at = NOW() + (random() * 300) * INTERVAL '1 second'
  WHERE dl.dead_letter_id IN (
    SELECT i.dead_letter_id FROM __SCHEMA__.wh_dead_letters i
    WHERE i.error_fingerprint = p_fingerprint
      AND i.recovery_status = 2
      AND i.recovered_at IS NULL
      AND i.operator_disposition NOT IN (2, 3)
      AND jsonb_typeof(i.envelope) = 'object'
    ORDER BY i.dead_lettered_at DESC
    LIMIT p_wave_size);
  GET DIAGNOSTICS v_count = ROW_COUNT;

  IF v_count > 0 THEN
    UPDATE __SCHEMA__.wh_dlq_probe_campaigns c
    SET wave = c.wave + 1, wave_started_at = NOW()
    WHERE c.fingerprint = p_fingerprint AND c.generation = p_generation;
  END IF;

  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.begin_trickle_wave IS
'One trickle wave for a Mixed cohort (127): releases up to p_wave_size HELD rows staggered across five minutes and stamps the campaign''s wave state. Zero released = the cohort is drained. Doubling happens between waves in the worker, never inside one.';

-- ============================================================================
-- count_wave_requarantines — has the wave washed back?
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.count_wave_requarantines(
  p_fingerprint VARCHAR(16), p_generation TEXT)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
  v_since TIMESTAMPTZ;
  v_count INTEGER;
BEGIN
  SELECT c.wave_started_at INTO v_since
  FROM __SCHEMA__.wh_dlq_probe_campaigns c
  WHERE c.fingerprint = p_fingerprint AND c.generation = p_generation;
  IF v_since IS NULL THEN
    RETURN 0;
  END IF;
  SELECT count(*) INTO v_count
  FROM __SCHEMA__.wh_dead_letters dl
  WHERE dl.error_fingerprint = p_fingerprint
    AND dl.recovered_at IS NULL
    AND dl.dead_lettered_at > v_since;
  RETURN v_count;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.count_wave_requarantines IS
'New unrecovered dead letters carrying the cohort''s fingerprint since the current wave started (127) — the wave washing back. Any washback halts the trickle in the worker.';
