-- Migration: 106_OffloadClaimLedger
-- Date:      2026-08-16
-- Purpose:   The database's record of every transport body-offload blob, so cleanup is a query
--            instead of a container listing.
--
-- Why:       The claim (storage key + provider) otherwise exists only inside the claim envelope in
--            wh_outbox/wh_inbox, and those rows are deleted on completion — after which the
--            database knows nothing about the blob. Passive cleanup (the default, and the only
--            fan-out-safe mode: an active per-consumer delete breaks sibling subscribers'
--            downloads) then depends entirely on a provider-side lifecycle rule that nothing
--            verifies exists. With the ledger, the passive sweep is:
--              SELECT expired → delete blobs (404 = success, DeleteAsync is idempotent on missing)
--              → DELETE rows for successes only.
--            A failed blob delete keeps its row and retries next sweep: the row outlives the blob,
--            never the reverse.
--
-- Clock:     uploaded_at defaults to the DB clock, and expiry is evaluated against it AT SWEEP
--            TIME — so changing the expiry window is retroactive over every existing blob by
--            construction. Nothing is stamped per blob (contrast ADLS per-blob ExpiresOn, which
--            stamps at creation and cannot be retimed without a rewalk).
--
-- Growth:    one row per offloaded body, bounded by expiry-window volume; rows die with their
--            blobs. Pre-ledger blobs are invisible here — the provider-side lifecycle rule remains
--            the documented backstop, set LONGER than the sweep window.
--
-- Dependencies: none (standalone table; written by the offload hook, read by the maintenance sweep)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_offload_claims (
  storage_key   TEXT NOT NULL PRIMARY KEY,
  provider_name TEXT NOT NULL,
  uploaded_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- The sweep's whole access path: uploaded_at < NOW() - window ORDER BY uploaded_at LIMIT n.
CREATE INDEX IF NOT EXISTS idx_wh_offload_claims_uploaded_at
  ON __SCHEMA__.wh_offload_claims (uploaded_at);

COMMENT ON TABLE __SCHEMA__.wh_offload_claims IS
  'Ledger of transport body-offload blobs: written by the offload hook at upload, drained by the '
  'passive expiry sweep (delete blob, then remove row on success only). The row outlives the blob, '
  'never the reverse.';
COMMENT ON COLUMN __SCHEMA__.wh_offload_claims.uploaded_at IS
  'DB clock at ledger insert. Expiry is evaluated against this at sweep time, so a changed window '
  'is retroactive over existing blobs; nothing is stamped per blob.';
