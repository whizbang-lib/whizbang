-- Migration: 060_CreateUniqueEmissionClaims.sql
-- Date: 2026-06-22
-- Description: Creates wh_unique_emission_claims table — the atomic claim primitive
--              backing IDispatcher.PublishOnceAsync. Receptors and command handlers
--              that emit a logically once-per-key event (e.g. a saga completion event
--              under N concurrent terminal handlers) take a claim before emitting.
--              First INSERT wins by INSERT … ON CONFLICT DO NOTHING; subsequent
--              attempts return zero rows affected and intentionally no-op.
--
--              This is messaging infrastructure, not domain state. Rows do not
--              participate in projection replay. Claims expire (default 30 minutes —
--              wide safety margin over the millisecond-scale race window) and the
--              same prune cadence that clears wh_outbox / wh_inbox sweeps them.
--
--              The claim key is opaque to the framework: callers choose any string
--              unique within their domain. Whizbang.Sagas (forthcoming) will wrap
--              this with the convention key = sagaId.ToString() for the saga
--              completion case.
-- Dependencies: None (independent infrastructure table)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_unique_emission_claims (
  -- The caller-chosen idempotency key. Opaque to the framework.
  claim_key TEXT PRIMARY KEY,

  -- When the claim was taken.
  claimed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  -- The MessageId of the event whose emission this claim guards. Useful for
  -- audit / debugging — "which event won the race?" — but the framework does
  -- not read this column.
  claimed_by_event_id UUID NOT NULL,

  -- When the claim becomes eligible for pruning. The race window is
  -- milliseconds; 30 minutes is a wide safety margin so that the rare
  -- "claimed but emission crashed" case has time to manually recover or
  -- naturally re-attempt before the claim releases.
  expires_at TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '30 minutes')
);

-- Single index supports the prune sweep. Lookups by claim_key use the PK.
CREATE INDEX IF NOT EXISTS idx_wh_unique_emission_claims_expires
ON __SCHEMA__.wh_unique_emission_claims (expires_at);

COMMENT ON TABLE __SCHEMA__.wh_unique_emission_claims IS
'Atomic claim primitive backing IDispatcher.PublishOnceAsync. First INSERT for a given claim_key wins via INSERT … ON CONFLICT DO NOTHING; subsequent emitters intentionally no-op. Messaging infrastructure — does NOT participate in projection replay. Default 30-minute TTL; pruned by the same sweep that clears wh_outbox / wh_inbox.';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.claim_key IS
'Caller-chosen idempotency key. Opaque to the framework. Saga library convention: sagaId.ToString().';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.claimed_at IS
'Timestamp the claim was taken.';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.claimed_by_event_id IS
'MessageId of the winning emission. Audit-only; framework does not read this column.';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.expires_at IS
'Timestamp the claim becomes prune-eligible. Default 30 minutes — wide safety margin over the millisecond race window so a stranded claim (claim taken, emission crashed) has time to recover before release.';
