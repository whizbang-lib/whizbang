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
--              participate in projection replay. A claim records when it expires
--              (default 30 minutes after it was taken), and the claimed-emission
--              prune maintenance step deletes it one day after that. The outbox and
--              inbox prune never touched this table. Claims under a prefix its owner
--              retains are left to that owner: Whizbang.Sagas prunes its spent claims
--              after its own retention and never prunes its abandonment claims
--              (saga-abandoned:{name}:{id}), which stop an abandoned saga being
--              re-armed.
--
--              The claim key is opaque to the framework: callers choose any string
--              unique within their domain. Whizbang.Sagas uses prefixed keys
--              (saga-completed:, saga-continuation:, saga-watchdog-sweep:,
--              saga-abandoned:).
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

  -- When the claim expires. The claimed-emission prune maintenance step
  -- deletes it one day later, unless its key prefix is retained by its owner
  -- (the saga framework's). Until it is deleted the claim holds its key.
  expires_at TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '30 minutes')
);

-- Supports the expiry prune (oldest expiry first). Lookups by claim_key use the PK.
CREATE INDEX IF NOT EXISTS idx_wh_unique_emission_claims_expires
ON __SCHEMA__.wh_unique_emission_claims (expires_at);

COMMENT ON TABLE __SCHEMA__.wh_unique_emission_claims IS
'Atomic claim primitive backing IDispatcher.PublishOnceAsync. First INSERT for a given claim_key wins via INSERT … ON CONFLICT DO NOTHING; subsequent emitters intentionally no-op. Messaging infrastructure — does NOT participate in projection replay. A claim expires 30 minutes after it is taken by default and is deleted by the claimed-emission prune maintenance step one day after its expiry, except under a key prefix its owner retains (the saga framework prunes its own claims and keeps its abandonment claims).';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.claim_key IS
'Caller-chosen idempotency key. Opaque to the framework. Saga library convention: saga-completed:, saga-continuation:, saga-watchdog-sweep: and saga-abandoned: prefixes.';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.claimed_at IS
'Timestamp the claim was taken.';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.claimed_by_event_id IS
'MessageId of the winning emission. Audit-only; framework does not read this column.';

COMMENT ON COLUMN __SCHEMA__.wh_unique_emission_claims.expires_at IS
'Timestamp the claim expires. Default 30 minutes after it is taken. The claim still holds its key until it is deleted, which the claimed-emission prune maintenance step does one day after this time, unless its key prefix is retained by its owner.';
