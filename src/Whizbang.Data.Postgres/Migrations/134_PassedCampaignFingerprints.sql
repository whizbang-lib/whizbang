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
