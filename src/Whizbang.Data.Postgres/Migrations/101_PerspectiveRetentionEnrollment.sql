-- Migration: 101_PerspectiveRetentionEnrollment
-- Purpose:  Carry a perspective's row-retention DECLARATION into SQL, so the reaper can sweep only
--           enrolled perspectives instead of enumerating every table that happens to have an
--           expires_at column.
--
-- Why here: wh_perspective_registry already exists, already carries the schema hash, and is already
--           reconciled at startup — so enrolment syncs for free rather than needing its own table
--           and its own lifecycle.
--
-- Shape:    Enrolment and duration are SEPARATE. row_retention_enrolled says whether the reaper
--           looks at this perspective at all; row_ttl_seconds (sliding, from updated_at) and
--           row_max_age_seconds (absolute, from created_at) are optional windows. Enrolled with
--           both NULL means "swept, no default rule" — rows expire only by an explicitly assigned
--           expires_at. NULL is deliberately distinct from zero, which would mean expire-immediately.
--
-- Dependencies: 030 (wh_perspective_registry + reconcile)

ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS row_retention_enrolled BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS row_ttl_seconds INTEGER;

ALTER TABLE __SCHEMA__.wh_perspective_registry
  ADD COLUMN IF NOT EXISTS row_max_age_seconds INTEGER;

-- Partial index: the reaper's driving question is "which perspectives am I sweeping?", and enrolled
-- perspectives are the small minority. A partial index keeps that lookup proportional to the
-- enrolled set rather than the whole registry.
CREATE INDEX IF NOT EXISTS idx_perspective_registry_retention_enrolled
  ON __SCHEMA__.wh_perspective_registry (clr_type_name)
  WHERE row_retention_enrolled;

-- Syncs one perspective's retention declaration. Called at startup from the C# TTL registry, which
-- is where the attribute values live.
--
-- Deliberately does NOT touch schema_hash: retention is metadata about a perspective's LIFECYCLE,
-- not about its table SHAPE. Folding it into the hash would make every retention change look like
-- schema drift to the 030 reconcile and produce a spurious fleet-wide drift report.
--
-- Un-enrolling clears both windows, so a later re-enrolment cannot silently inherit a stale one.
CREATE OR REPLACE FUNCTION __SCHEMA__.sync_perspective_retention(
  p_clr_type_name TEXT,
  p_enrolled BOOLEAN,
  p_ttl_seconds INTEGER,
  p_max_age_seconds INTEGER
) RETURNS INTEGER AS $$
DECLARE
  v_updated INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_perspective_registry
     SET row_retention_enrolled = p_enrolled,
         row_ttl_seconds        = CASE WHEN p_enrolled THEN p_ttl_seconds ELSE NULL END,
         row_max_age_seconds    = CASE WHEN p_enrolled THEN p_max_age_seconds ELSE NULL END
   WHERE clr_type_name = p_clr_type_name;

  GET DIAGNOSTICS v_updated = ROW_COUNT;
  RETURN v_updated;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.row_retention_enrolled IS
  'Whether the row reaper sweeps this perspective at all. Enrolment is separate from duration.';
COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.row_ttl_seconds IS
  'Sliding window in seconds, measured from updated_at (business time). NULL = enrolled with no default rule.';
COMMENT ON COLUMN __SCHEMA__.wh_perspective_registry.row_max_age_seconds IS
  'Absolute cap in seconds, measured from created_at (business time). Binds regardless of any per-row override.';
