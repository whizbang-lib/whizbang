-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 302_IntegrityDeliveriesWaitForTheMaintainer.sql
-- Date: 2026-11-02
-- Description: Integrity deliveries wait in a queue that only the maintainer drains (#2001).
--
--              Stream-integrity work on the receive side (checking a received checkpoint, answering a manifest
--              request, comparing a manifest chunk, answering a redelivery request, reconciling a committed
--              redelivery bundle) ran on whichever instance happened to lease the message, in parallel with that
--              instance's ordinary traffic. With N instances, N instances put integrity work on the database at once.
--
--              Each such delivery is now recorded in wh_integrity_deliveries and returns; the integrity delivery
--              worker on the instance holding the maintainer duty drains the table in arrival order, under the
--              housekeeping idle gate, and invokes the real handler.
--
--              1. wh_integrity_deliveries: one row per delivery. delivery_id is derived from the delivery's type and
--                 payload bytes, so the inbox, outbox and local copies of one message, and a retried delivery, are
--                 recorded once. not_before is when the row is next due: now on enqueue, pushed out by the lease
--                 while a drain holds it, and by the backoff after a failure.
--              2. idx_integrity_deliveries_drain on (queued_at, delivery_id): the drain order.
--              3. wh_integrity_delivery_enqueue: records a delivery once (ON CONFLICT DO NOTHING); true when this call
--                 recorded it.
--              4. wh_integrity_delivery_lease: the next due deliveries in arrival order, FOR UPDATE SKIP LOCKED, with
--                 not_before pushed out by the lease so an overlapping drain during a duty failover cannot take them.
--              5. wh_integrity_delivery_complete: deletes a handled delivery.
--              6. wh_integrity_delivery_fail: counts an attempt, keeps the error, and holds the row for the backoff.
--
--              Retention: none needed in maintenance. The drain deletes every delivery it handles, every delivery
--              that has spent its attempts (dropped with a warning, as control-plane traffic is at the dead-letter
--              bound), and every delivery whose type no handler is registered for.
--
--              Number 302: 297 to 301 are taken.
--
-- Dependencies: none
-- Objects: wh_integrity_deliveries, idx_integrity_deliveries_drain, wh_integrity_delivery_enqueue,
--          wh_integrity_delivery_lease, wh_integrity_delivery_complete, wh_integrity_delivery_fail
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_integrity_deliveries (
  delivery_id   UUID        NOT NULL PRIMARY KEY,      -- derived from delivery_type and the payload bytes
  delivery_type TEXT        NOT NULL,                  -- which registered handler runs it
  payload       JSONB       NOT NULL,                  -- the delivery, as its handler reads it
  queued_at     TIMESTAMPTZ NOT NULL DEFAULT now(),    -- arrival, the drain order
  attempts      INTEGER     NOT NULL DEFAULT 0,        -- failed attempts so far
  not_before    TIMESTAMPTZ NOT NULL DEFAULT now(),    -- next due: lease and backoff push it out
  last_error    TEXT                                   -- the most recent failure, for an operator
);

COMMENT ON TABLE __SCHEMA__.wh_integrity_deliveries IS
  '302 (#2001): stream-integrity deliveries waiting for the maintainer''s drain. Recorded by every instance, drained '
  'in arrival order by the instance holding the maintainer duty, under the housekeeping idle gate. The drain deletes '
  'what it handles and what it drops.';

-- <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs:Lease_ReturnsDueDeliveriesInArrivalOrder_AndPushesThemOutByTheLeaseAsync</tests>
CREATE INDEX IF NOT EXISTS idx_integrity_deliveries_drain
  ON __SCHEMA__.wh_integrity_deliveries (queued_at, delivery_id);

COMMENT ON INDEX __SCHEMA__.idx_integrity_deliveries_drain IS
  '302 (#2001): the drain reads due deliveries in arrival order.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Records a delivery once. clock_timestamp, not now: several deliveries recorded in one transaction still take
-- their arrival order.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_delivery_enqueue');
-- <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs:Enqueue_TheSameDeliveryTwice_RecordsItOnceAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_delivery_enqueue(
  p_delivery_id UUID,
  p_delivery_type TEXT,
  p_payload JSONB
) RETURNS BOOLEAN AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_integrity_deliveries (delivery_id, delivery_type, payload, queued_at, not_before)
  VALUES (p_delivery_id, p_delivery_type, p_payload, clock_timestamp(), clock_timestamp())
  ON CONFLICT (delivery_id) DO NOTHING;
  RETURN FOUND;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_delivery_enqueue(UUID, TEXT, JSONB) IS
  '302 (#2001): records an integrity delivery once; true when this call recorded it, false when it was already there.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
-- Leases the next due deliveries in arrival order. SKIP LOCKED, so a second drain never waits on the first; the
-- lease pushes not_before out, so a drain that overlaps this one while the maintainer duty fails over does not take
-- the same rows once this statement has committed.
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_delivery_lease');
-- <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs:Lease_ReturnsDueDeliveriesInArrivalOrder_AndPushesThemOutByTheLeaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs:Lease_SkipsADeliveryAnotherDrainHoldsLockedAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_delivery_lease(
  p_max_deliveries INTEGER,
  p_lease_seconds INTEGER
) RETURNS TABLE(delivery_id UUID, delivery_type TEXT, payload JSONB, attempts INTEGER, queued_at TIMESTAMPTZ) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  WITH due AS (
    SELECT d.delivery_id
    FROM __SCHEMA__.wh_integrity_deliveries d
    WHERE d.not_before <= clock_timestamp()
    ORDER BY d.queued_at, d.delivery_id
    LIMIT p_max_deliveries
    FOR UPDATE SKIP LOCKED
  ), leased AS (
    UPDATE __SCHEMA__.wh_integrity_deliveries d
    SET not_before = clock_timestamp() + make_interval(secs => p_lease_seconds)
    FROM due
    WHERE d.delivery_id = due.delivery_id
    RETURNING d.delivery_id, d.delivery_type, d.payload, d.attempts, d.queued_at
  )
  SELECT l.delivery_id, l.delivery_type, l.payload, l.attempts, l.queued_at
  FROM leased l
  ORDER BY l.queued_at, l.delivery_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_delivery_lease(INTEGER, INTEGER) IS
  '302 (#2001): leases up to p_max_deliveries due integrity deliveries in arrival order (SKIP LOCKED), pushing each '
  'one''s not_before out by p_lease_seconds.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_delivery_complete');
-- <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs:Complete_DeletesTheDeliveryAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_delivery_complete(
  p_delivery_id UUID
) RETURNS VOID AS $$
BEGIN
  DELETE FROM __SCHEMA__.wh_integrity_deliveries WHERE delivery_id = p_delivery_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_delivery_complete(UUID) IS
  '302 (#2001): deletes an integrity delivery the drain handled or dropped.';

-- ────────────────────────────────────────────────────────────────────────────────────────────────
SELECT __SCHEMA__.drop_all_overloads('wh_integrity_delivery_fail');
-- <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityDeliverySqlTests.cs:Fail_CountsTheAttempt_KeepsTheError_AndHoldsItForTheBackoffAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_delivery_fail(
  p_delivery_id UUID,
  p_error TEXT,
  p_backoff_seconds INTEGER
) RETURNS VOID AS $$
BEGIN
  UPDATE __SCHEMA__.wh_integrity_deliveries
  SET attempts = attempts + 1,
      not_before = clock_timestamp() + make_interval(secs => p_backoff_seconds),
      last_error = left(p_error, 4000)
  WHERE delivery_id = p_delivery_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_delivery_fail(UUID, TEXT, INTEGER) IS
  '302 (#2001): counts a failed attempt of an integrity delivery, keeps the error, and holds it for p_backoff_seconds.';
