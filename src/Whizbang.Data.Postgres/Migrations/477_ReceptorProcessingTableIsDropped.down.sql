-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 477_ReceptorProcessingTableIsDropped.sql: wh_receptor_processing re-created empty, with the indexes an
-- earlier release's bootstrap and migration 031 gave it. Nothing wrote it, so the reverse restores no rows.

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_receptor_processing (
  id UUID NOT NULL PRIMARY KEY,
  event_id UUID NOT NULL,
  receptor_name TEXT NOT NULL,
  stream_id UUID NOT NULL,
  partition_number INTEGER,
  status SMALLINT NOT NULL DEFAULT 0,
  attempts INTEGER NOT NULL DEFAULT 0,
  error TEXT,
  instance_id UUID,
  lease_expiry TIMESTAMPTZ,
  started_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
  claimed_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ,
  processed_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_receptor_processing_event_id ON __SCHEMA__.wh_receptor_processing (event_id);
CREATE INDEX IF NOT EXISTS idx_receptor_processing_receptor_name ON __SCHEMA__.wh_receptor_processing (receptor_name);
CREATE INDEX IF NOT EXISTS idx_receptor_processing_status ON __SCHEMA__.wh_receptor_processing (status);
CREATE INDEX IF NOT EXISTS idx_receptor_processing_claim
  ON __SCHEMA__.wh_receptor_processing (instance_id, lease_expiry)
  WHERE completed_at IS NULL;
