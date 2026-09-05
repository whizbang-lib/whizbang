-- ============================================================================
-- 134_PassedCampaignFingerprints.sql
-- ============================================================================
-- get_passed_campaign_fingerprints(p_generation): fingerprints whose canary campaign
-- reached a terminal Pass verdict on the given build generation.
--
-- A Pass is standing evidence about the BUILD, not a one-shot release trigger. The
-- recovery worker consults this set before quarantining an exhausted row: a row whose
-- fingerprint passed on the current generation is re-driven instead of held, which stops
-- a proven-safe cohort from re-accumulating in HoldForReview after the campaign retired
-- (issue #681 — the one-shot canary-pass release freed only the rows held at verdict
-- time, then the scan re-held the rest of the cohort a batch per cycle, forever).
-- ============================================================================

SELECT __SCHEMA__.drop_all_overloads('get_passed_campaign_fingerprints');

CREATE OR REPLACE FUNCTION __SCHEMA__.get_passed_campaign_fingerprints(
  p_generation TEXT
) RETURNS TABLE(fingerprint VARCHAR(16)) AS $$
BEGIN
  RETURN QUERY
  SELECT c.fingerprint
  FROM __SCHEMA__.wh_dlq_probe_campaigns c
  WHERE c.generation = p_generation
    AND c.verdict = 1;  -- CanaryVerdictKind.Pass
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.get_passed_campaign_fingerprints IS
'Fingerprints with a terminal Pass campaign on the given generation (134). The recovery worker treats these as standing evidence: exhausted rows of a passed cohort are re-driven on the paced scan cadence rather than moved to HoldForReview (#681).';

-- ============================================================================
-- mark_dead_letter_discarded — settle a disabled-subsystem row (#684)
-- ============================================================================
-- Recovered(3) + recovered_at + an explanatory note, WITHOUT re-driving anything. Used by
-- the recovery worker when a row's message type belongs to a disabled subsystem: the
-- message has no meaning while its feature is off, and quarantine policies that hold
-- before dispatch (PoisonRedeliveryLoop, MaxAttempts=0) make the inbox-gate discard
-- unreachable for these rows. Settled rows age out through the normal retention purge.

SELECT __SCHEMA__.drop_all_overloads('mark_dead_letter_discarded');

CREATE OR REPLACE FUNCTION __SCHEMA__.mark_dead_letter_discarded(
  p_id   UUID,
  p_note TEXT
) RETURNS BOOLEAN AS $$
DECLARE
  v_count INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_dead_letters
  SET recovery_status = 3,  -- Recovered: settled, eligible for the retention purge
      recovered_at    = NOW(),
      operator_notes  = COALESCE(operator_notes || E'\n', '') || COALESCE(p_note, 'discarded: subsystem disabled')
  WHERE dead_letter_id = p_id
    AND recovered_at IS NULL
    AND recovery_status NOT IN (3, 4);
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count > 0;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.mark_dead_letter_discarded IS
'Settles a dead letter whose message belongs to a disabled subsystem (134, #684): Recovered + note, no re-drive. Idempotent — an already-settled row returns false.';
