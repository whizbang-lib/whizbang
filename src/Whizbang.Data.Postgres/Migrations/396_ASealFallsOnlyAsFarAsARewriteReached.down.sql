-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 396_ASealFallsOnlyAsFarAsARewriteReached.sql (#2262): the generation guard, the bump and the refold
-- helper as 093 defined them, the epoch fold as 092 defined it, and the rewrite log, its settings, the note and the
-- answer dropped. The log is lost: an older release never reads it, and resets a consumer's seal on every generation
-- change, as it did before 396. Seals and the generation itself are kept.

-- ---------------------------------------------------------------------------------------------
-- integrity_seal_generation_guard: 093's definition, whole.
-- ---------------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION __SCHEMA__.integrity_seal_generation_guard(
  p_origin     UUID,
  p_generation BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_gen BIGINT;
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_seals (origin_service_id, sealed_through, origin_generation, updated_at)
  VALUES (p_origin, 0, p_generation, NOW())
  ON CONFLICT (origin_service_id) DO NOTHING;

  SELECT s.origin_generation INTO v_gen
  FROM __SCHEMA__.wh_integrity_seals s
  WHERE s.origin_service_id = p_origin;

  IF v_gen = p_generation THEN
    RETURN TRUE;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_seals
  SET sealed_through = 0, origin_generation = p_generation, updated_at = NOW()
  WHERE origin_service_id = p_origin;
  RETURN FALSE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.integrity_seal_generation_guard IS
'Consumer guard: unchanged generation proceeds; a changed one resets the seal once and skips the round. Deliberate history mutation is re-verified, never alarmed on.';

-- ---------------------------------------------------------------------------------------------
-- _wh_bump_origin_generation: 093's definition, whole.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_bump_origin_generation() RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  UPDATE __SCHEMA__.wh_settings
  SET setting_value = ((setting_value)::bigint + 1)::text
  WHERE setting_key = 'integrity_origin_generation';
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_bump_origin_generation IS
'Internal: advances the origin generation after a legitimate mutation of folded history.';

-- ---------------------------------------------------------------------------------------------
-- _wh_refold_epochs_covering: 093's definition, whole.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_refold_epochs_covering(
  p_lane    UUID,
  p_min_seq BIGINT,
  p_max_seq BIGINT
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_width BIGINT;
BEGIN
  SELECT f.epoch_width INTO v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = p_lane;
  IF v_width IS NULL THEN
    RETURN;
  END IF;
  PERFORM __SCHEMA__.refold_digest_epochs(p_lane, p_min_seq / v_width, p_max_seq / v_width);
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_refold_epochs_covering IS
'Internal: refolds the sealed epochs whose sequence ranges a legitimate mutation touched.';

-- ---------------------------------------------------------------------------------------------
-- _wh_fold_digest_epoch: 092's definition, whole.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_fold_digest_epoch(
  p_lane      UUID,
  p_epoch     BIGINT,
  p_width     BIGINT,
  p_closed_at TIMESTAMPTZ
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_digest_epochs
  WHERE origin_service_id = p_lane AND epoch_id = p_epoch;

  INSERT INTO __SCHEMA__.wh_digest_epochs
    (origin_service_id, scope_tenant, event_type, epoch_id, digest_lo, digest_hi, event_count, closed_at)
  SELECT p_lane, b.b_tenant, b.b_type, p_epoch, b.b_lo, b.b_hi, b.b_cnt, p_closed_at
  FROM __SCHEMA__._wh_epoch_buckets(p_lane, p_epoch, p_width) b;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__._wh_fold_digest_epoch IS
'Internal: (re)writes one epoch''s bucket rows from the canonical fold (_wh_epoch_buckets).';

-- ---------------------------------------------------------------------------------------------
-- What 396 added.
-- ---------------------------------------------------------------------------------------------
DROP FUNCTION IF EXISTS __SCHEMA__.wh_integrity_seal_guard(UUID, BIGINT, BIGINT, BIGINT);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_integrity_history_rewrite(BIGINT);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_note_origin_rewrite(BIGINT);
DROP TABLE IF EXISTS __SCHEMA__.wh_integrity_origin_rewrites;
DELETE FROM __SCHEMA__.wh_settings
WHERE setting_key IN ('integrity_origin_rewrite_log_from', 'integrity_origin_rewrite_log_rows');
