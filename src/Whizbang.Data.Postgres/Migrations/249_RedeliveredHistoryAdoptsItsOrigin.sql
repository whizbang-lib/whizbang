-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 249_RedeliveredHistoryAdoptsItsOrigin.sql
-- Date: 2026-10-27
-- Description: Stream integrity converges on history received before the store recorded origins, and stops asking
--              for a bucket repair cannot change (#1790).
--
--              087 started recording a received event's origin (origin_service_id, origin_commit_sequence) and nothing
--              backfills the events received before it: they keep both columns empty, and their digests sit in the
--              local lane. Origin-keyed verification (the checkpoint count, the origin lane's digests) therefore counts
--              every one of them as missing. Repair asks the origin to redeliver them, the receiver skips each one as
--              a duplicate (the inbox deduplication, then the emit chain's "already stored" test), and the next check
--              asks again. Only the origin knows the sequences, so the receiver cannot backfill them from its own
--              store; the redelivery repair asks for is the one source that carries them.
--
--              wh_adopt_redelivered_origin reconciles a redelivery bundle's events with the store. For each event the
--              store already holds:
--                * an empty origin takes the bundle's origin, and an empty origin commit sequence takes the event's
--                  sequence from the bundle. A recorded origin or sequence is never overwritten, and an event recorded
--                  under another origin is never re-attributed. The local service, and the empty id, adopt nothing:
--                  a locally-originated event keeps no origin stamp (the 046 contract);
--                * an event that leaves the local lane takes its digest with it: its hash leaves the local bucket and
--                  joins the origin's, under the predicates every digest fold uses (not ephemeral, not at-most-once,
--                  not exempt), and a local bucket left empty is removed. The local lane is this service's own
--                  manifest, so a received event folded there was also advertised as this service's;
--                * the sealed epochs either lane holds over the moved events are refolded, and when a sealed epoch of
--                  the local lane changed the origin generation advances, so this service's consumers re-verify that
--                  history instead of alarming on it (093).
--              It answers per (stream, tenant, type) bucket how many of the bundle's events were present and how many
--              of those adopted something. Events the store does not hold are not reported; the bundle stores them.
--
--              The ledger learns a fact it could not express: a bucket a redelivery answered with events that were all
--              already present, of which none adopted anything. Asking again ships the same events again, so
--              wh_integrity_mark_unhealable_batch records it (wh_integrity_ledger.unhealable_at) and returns the rows
--              it marked, which the caller reports once. A marked bucket is granted no repair by either grant path
--              (wh_integrity_try_begin_repair, wh_integrity_claim_repair_drain) and is not re-reported on the cooldown
--              (wh_integrity_try_begin_report). A changed signature is a fresh incident and lifts the mark; a heal
--              deletes the row. A bucket whose origin never answered keeps 099's terminal cadence: no answer is no
--              evidence that repair cannot change it.
--
--              Cost: the adoption runs once per redelivery bundle a receiver commits, over the bundle's events by
--              primary key (a bundle is bounded by the origin's composite limits), under the emit chain's per-stream
--              locks. Nothing on the live receive path changes, and no backfill scans the store: the history that
--              needs an origin is exactly the history verification asks the origin for.
--
-- Dependencies: 087 (wh_stream_digests, origin columns), 090 and 099 (the ledger and its grant functions, copied with
--               the deltas above), 092 (refold_digest_epochs, wh_digest_epoch_frontiers), 093 (_wh_refold_epochs_covering,
--               _wh_bump_origin_generation), 214 (the at-most-once flag), 216 (__DIGEST_EXEMPT_TYPES__),
--               230 (the emit chain's per-stream lock key)
-- Objects: wh_integrity_ledger, wh_adopt_redelivered_origin, wh_integrity_mark_unhealable_batch,
--          wh_integrity_try_begin_report, wh_integrity_try_begin_repair, wh_integrity_claim_repair_drain
-- Constants: the double-underscore tokens in this file (for example __EMPTY_UUID__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_integrity_ledger
  ADD COLUMN IF NOT EXISTS unhealable_at TIMESTAMPTZ;

COMMENT ON COLUMN __SCHEMA__.wh_integrity_ledger.unhealable_at IS
'249 (#1790): when a redelivery answered this bucket with events that were all already stored under their origin, so repair cannot change it. Repair is not granted and the cooldown does not re-report while set; a changed signature clears it.';

-- <docs>resilience/stream-integrity#pre-stamp-history</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_EventsStoredWithoutAnOrigin_TakeTheBundlesOriginAndSequencesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_TheNextCheckpointCountsTheAdoptedEvents_InsteadOfAskingForThemAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_MovesTheDigestFromTheLocalLaneToTheOriginsLane_LeavingLocalEventsWhereTheyAreAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_AnEventReceivedFromAnotherOrigin_IsNeverReattributedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveredOriginAdoptionSqlTests.cs:Adopt_IntoSealedEpochs_RefoldsBothLanes_AndAdvancesTheOriginGenerationAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_adopt_redelivered_origin(
  p_origin_service_id UUID,
  p_event_ids UUID[],
  p_commit_sequences BIGINT[]
) RETURNS TABLE(stream_id UUID, tenant_scope TEXT, event_type TEXT, present_count INTEGER, adopted_count INTEGER) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
  v_local_service_id UUID;
  v_adoptable BOOLEAN;
  v_min BIGINT;
  v_max BIGINT;
  v_width BIGINT;
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_commit_sequences) THEN
    RAISE EXCEPTION 'wh_adopt_redelivered_origin: % event ids but % commit sequences',
      cardinality(p_event_ids), cardinality(p_commit_sequences);
  END IF;

  -- The emit chain's normalization (087): the local service and the empty id are "locally originated".
  SELECT sc.service_id INTO v_local_service_id FROM __SCHEMA__.wh_service_config sc LIMIT 1;
  v_adoptable := p_origin_service_id IS NOT NULL
    AND p_origin_service_id <> __EMPTY_UUID__::uuid
    AND p_origin_service_id IS DISTINCT FROM v_local_service_id;

  -- The streams' append locks, in the emit chain's order, so a concurrent fold of the same bucket waits for this one.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT es.stream_id AS sid
    FROM unnest(p_event_ids) AS u(eid)
    JOIN __SCHEMA__.wh_event_store es ON es.event_id = u.eid
    ORDER BY 1
  ) s;

  CREATE TEMP TABLE IF NOT EXISTS _wh_adopt_rows (
    event_id UUID PRIMARY KEY,
    stream_id UUID NOT NULL,
    tenant_scope TEXT NOT NULL,
    event_type TEXT NOT NULL,
    left_local_lane BOOLEAN NOT NULL,
    local_sequence BIGINT,
    origin_sequence BIGINT,
    adopted BOOLEAN NOT NULL,
    digested BOOLEAN NOT NULL
  ) ON COMMIT DROP;
  TRUNCATE _wh_adopt_rows;

  INSERT INTO _wh_adopt_rows
  SELECT
    es.event_id,
    es.stream_id,
    COALESCE(es.scope ->> 't', ''),
    es.event_type,
    v_adoptable AND es.origin_service_id IS NULL,
    es.commit_sequence,
    COALESCE(es.origin_commit_sequence, NULLIF(m.seq, 0)),
    v_adoptable AND (
      es.origin_service_id IS NULL
      OR (es.origin_service_id = p_origin_service_id AND es.origin_commit_sequence IS NULL AND COALESCE(m.seq, 0) > 0)),
    COALESCE(es.flags, 0) & c_flag_ephemeral = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
  FROM (SELECT DISTINCT ON (u.eid) u.eid, u.seq FROM unnest(p_event_ids, p_commit_sequences) AS u(eid, seq) ORDER BY u.eid) m
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid;

  UPDATE __SCHEMA__.wh_event_store es
  SET origin_service_id = p_origin_service_id, origin_commit_sequence = r.origin_sequence
  FROM _wh_adopt_rows r
  WHERE es.event_id = r.event_id AND r.adopted;

  -- Digests: XOR is self-inverse, so an adopted event's hash leaves the local bucket and joins the origin's.
  WITH leaving AS (
    SELECT r.tenant_scope, r.event_type, r.stream_id,
           bit_xor(hashtextextended(r.event_id::text, 0)) AS lo, bit_xor(hashtextextended(r.event_id::text, 1)) AS hi,
           COUNT(*)::int AS n
    FROM _wh_adopt_rows r
    WHERE r.adopted AND r.digested AND r.left_local_lane
    GROUP BY 1, 2, 3
  )
  UPDATE __SCHEMA__.wh_stream_digests d
  SET digest_lo = d.digest_lo # l.lo, digest_hi = d.digest_hi # l.hi, event_count = d.event_count - l.n, updated_at = NOW()
  FROM leaving l
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid AND d.scope_tenant = l.tenant_scope
    AND d.event_type = l.event_type AND d.stream_id = l.stream_id;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.origin_service_id = __EMPTY_UUID__::uuid
    AND d.event_count <= 0
    AND d.stream_id IN (SELECT r.stream_id FROM _wh_adopt_rows r WHERE r.adopted AND r.digested AND r.left_local_lane);

  INSERT INTO __SCHEMA__.wh_stream_digests AS d
    (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
  SELECT p_origin_service_id, r.tenant_scope, r.event_type, r.stream_id,
         bit_xor(hashtextextended(r.event_id::text, 0)), bit_xor(hashtextextended(r.event_id::text, 1)),
         COUNT(*)::int, NOW()
  FROM _wh_adopt_rows r
  WHERE r.adopted AND r.digested AND r.left_local_lane
  GROUP BY 1, 2, 3, 4
  ORDER BY 1, 2, 3, 4
  ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
    digest_lo = d.digest_lo # EXCLUDED.digest_lo,
    digest_hi = d.digest_hi # EXCLUDED.digest_hi,
    event_count = d.event_count + EXCLUDED.event_count,
    updated_at = EXCLUDED.updated_at;

  -- Sealed epochs over the moved events: the origin's lane by origin sequence, the local lane by local sequence.
  SELECT MIN(r.origin_sequence), MAX(r.origin_sequence) INTO v_min, v_max
  FROM _wh_adopt_rows r
  WHERE r.adopted AND r.digested AND r.origin_sequence IS NOT NULL;
  IF v_min IS NOT NULL THEN
    PERFORM __SCHEMA__._wh_refold_epochs_covering(p_origin_service_id, v_min, v_max);
  END IF;

  SELECT MIN(r.local_sequence), MAX(r.local_sequence) INTO v_min, v_max
  FROM _wh_adopt_rows r
  WHERE r.adopted AND r.digested AND r.left_local_lane AND r.local_sequence IS NOT NULL;
  SELECT f.epoch_width INTO v_width
  FROM __SCHEMA__.wh_digest_epoch_frontiers f
  WHERE f.origin_service_id = __EMPTY_UUID__::uuid;
  IF v_min IS NOT NULL AND v_width IS NOT NULL
     AND __SCHEMA__.refold_digest_epochs(__EMPTY_UUID__::uuid, v_min / v_width, v_max / v_width) > 0 THEN
    PERFORM __SCHEMA__._wh_bump_origin_generation();
  END IF;

  RETURN QUERY
  SELECT r.stream_id, r.tenant_scope, r.event_type, COUNT(*)::int, (COUNT(*) FILTER (WHERE r.adopted))::int
  FROM _wh_adopt_rows r
  GROUP BY r.stream_id, r.tenant_scope, r.event_type
  ORDER BY r.stream_id, r.tenant_scope, r.event_type;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_adopt_redelivered_origin(UUID, UUID[], BIGINT[]) IS
'249 (#1790): reconciles a redelivery bundle with the events the store already holds. An empty origin takes the bundle''s origin and an empty origin commit sequence the event''s sequence (never overwriting, never re-attributing, never for the local service); the digest leaves the local lane for the origin''s; sealed epochs over the moved events are refolded and the origin generation advances when the local lane''s did. Returns per (stream, tenant, type) bucket the events present and the events that adopted something.';

-- <docs>resilience/stream-integrity#unhealable-buckets</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:MarkUnhealable_ATrackedBucket_IsReturnedOnce_AndAKeyWithoutARowIsIgnoredAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:MarkUnhealable_AnEmptyTenant_MatchesTheRowTheLedgerKeyedWithoutOneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_unhealable_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_now               TIMESTAMPTZ
) RETURNS TABLE(tenant_scope TEXT, event_type TEXT, stream_id UUID) AS $$
#variable_conflict use_column
BEGIN
  RETURN QUERY
  WITH keys AS (
    SELECT DISTINCT COALESCE(k.tenant, '') AS tenant, k.etype, k.sid
    FROM unnest(p_tenant_scopes, p_event_types, p_stream_ids) AS k(tenant, etype, sid)
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET unhealable_at = p_now,
      last_touched  = p_now
  FROM keys k
  WHERE l.origin_service_id = p_origin_service_id
    AND l.tenant_scope = k.tenant
    AND l.event_type = k.etype
    AND l.stream_id = k.sid
    AND l.unhealable_at IS NULL
  RETURNING l.tenant_scope, l.event_type, l.stream_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_unhealable_batch(UUID, TEXT[], TEXT[], UUID[], TIMESTAMPTZ) IS
'249 (#1790): marks the tracked ledger rows of the keyed buckets as ones repair cannot change and returns the rows this call marked (a row already marked, or a key with no row, is not returned), so each is reported once.';

-- wh_integrity_try_begin_report: last word 090, with the 249 mark lifted by a changed signature and honored by the cooldown.
-- <docs>resilience/stream-integrity#unhealable-buckets</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginReport_AnUnhealableBucketUnchanged_IsNotReReportedOnTheCooldownAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginReport_ASignatureChange_LiftsTheMarkAndRepairResumesAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerSqlTests.cs:TryBeginReportBatch_RefusesASecondReportInsideTheCooldownAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_report(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID,
  p_origin_lo         BIGINT,
  p_origin_hi         BIGINT,
  p_local_lo          BIGINT,
  p_local_hi          BIGINT,
  p_now               TIMESTAMPTZ,
  p_cooldown_seconds  INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_scope   TEXT := COALESCE(p_tenant_scope, '');
  v_row     __SCHEMA__.wh_integrity_ledger%ROWTYPE;
  v_changed BOOLEAN;
  v_report  BOOLEAN;
BEGIN
  -- Lock the row for the duration so concurrent replicas serialize on this bucket and exactly one
  -- of them is told to report. This is the part in-memory state could never provide.
  SELECT * INTO v_row FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id
  FOR UPDATE;

  IF NOT FOUND THEN
    INSERT INTO __SCHEMA__.wh_integrity_ledger (
      origin_service_id, tenant_scope, event_type, stream_id,
      origin_lo, origin_hi, local_lo, local_hi, last_reported_at, repair_attempts, last_touched,
      first_seen_at)
    VALUES (p_origin_service_id, v_scope, p_event_type, p_stream_id,
            p_origin_lo, p_origin_hi, p_local_lo, p_local_hi, p_now, 0, p_now,
            p_now)
    ON CONFLICT (origin_service_id, tenant_scope, event_type, stream_id) DO NOTHING;
    -- A racing replica may have inserted first; only the winner reports.
    IF FOUND THEN
      RETURN TRUE;
    END IF;
    RETURN FALSE;
  END IF;

  v_changed := v_row.origin_lo IS DISTINCT FROM p_origin_lo
            OR v_row.origin_hi IS DISTINCT FROM p_origin_hi
            OR v_row.local_lo  IS DISTINCT FROM p_local_lo
            OR v_row.local_hi  IS DISTINCT FROM p_local_hi;

  -- 249: a bucket repair cannot change was reported once, when it was marked; the cooldown does not repeat it.
  v_report := v_row.last_reported_at IS NULL
           OR v_changed
           OR (v_row.unhealable_at IS NULL
               AND (p_now - v_row.last_reported_at) >= make_interval(secs => p_cooldown_seconds));

  UPDATE __SCHEMA__.wh_integrity_ledger
  SET origin_lo        = p_origin_lo,
      origin_hi        = p_origin_hi,
      local_lo         = p_local_lo,
      local_hi         = p_local_hi,
      -- A moved signature is a fresh incident: the repair budget starts over.
      repair_attempts  = CASE WHEN v_changed THEN 0 ELSE v_row.repair_attempts END,
      last_repair_at   = CASE WHEN v_changed THEN NULL ELSE v_row.last_repair_at END,
      -- 249: and whatever a redelivery showed about the old signature no longer holds.
      unhealable_at    = CASE WHEN v_changed THEN NULL ELSE v_row.unhealable_at END,
      last_reported_at = CASE WHEN v_report THEN p_now ELSE v_row.last_reported_at END,
      last_touched     = p_now
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id;

  RETURN v_report;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_report(UUID, TEXT, TEXT, UUID, BIGINT, BIGINT, BIGINT, BIGINT, TIMESTAMPTZ, INTEGER) IS
'True when a divergence should be reported now (first sighting, changed signature, or cooldown elapsed while repair can still change it). Row-locked so concurrent replicas cannot both report the same bucket. A changed signature resets the repair budget and lifts the 249 unhealable mark.';

-- wh_integrity_try_begin_repair: last word 099, with no grant for a bucket marked unhealable (249).
-- <docs>resilience/stream-integrity#unhealable-buckets</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginRepair_AnUnhealableBucket_IsRefusedPastItsTerminalCadenceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:TryBeginReport_ASignatureChange_LiftsTheMarkAndRepairResumesAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_repair(
  p_origin_service_id UUID,
  p_tenant_scope      TEXT,
  p_event_type        TEXT,
  p_stream_id         UUID,
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_scope     TEXT := COALESCE(p_tenant_scope, '');
  v_row       __SCHEMA__.wh_integrity_ledger%ROWTYPE;
  v_doublings INTEGER;
  v_wait      INTERVAL;
BEGIN
  SELECT * INTO v_row FROM __SCHEMA__.wh_integrity_ledger
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id
  FOR UPDATE;

  IF NOT FOUND THEN
    INSERT INTO __SCHEMA__.wh_integrity_ledger (
      origin_service_id, tenant_scope, event_type, stream_id,
      origin_lo, origin_hi, local_lo, local_hi, repair_attempts, last_repair_at, last_touched)
    VALUES (p_origin_service_id, v_scope, p_event_type, p_stream_id,
            -9223372036854775808, -9223372036854775808, -9223372036854775808, -9223372036854775808,
            1, p_now, p_now)
    ON CONFLICT (origin_service_id, tenant_scope, event_type, stream_id) DO NOTHING;
    RETURN FOUND;
  END IF;

  -- 249: a redelivery already showed repair cannot change this bucket; asking again ships the same events again.
  IF v_row.unhealable_at IS NOT NULL THEN
    RETURN FALSE;
  END IF;

  IF v_row.last_repair_at IS NOT NULL THEN
    -- Capped at 6 doublings so the wait cannot overflow, matching the in-memory ladder.
    -- At/past the attempt cap the ladder stops climbing and holds the terminal wait.
    IF v_row.repair_attempts >= p_max_attempts THEN
      v_doublings := 6;
    ELSE
      v_doublings := LEAST(GREATEST(v_row.repair_attempts - 1, 0), 6);
    END IF;
    v_wait := make_interval(secs => p_base_backoff_secs * POWER(2, v_doublings));
    IF (p_now - v_row.last_repair_at) < v_wait THEN
      RETURN FALSE;
    END IF;
  END IF;

  UPDATE __SCHEMA__.wh_integrity_ledger
  SET repair_attempts = v_row.repair_attempts + 1,
      last_repair_at  = p_now,
      last_touched    = p_now
  WHERE origin_service_id = p_origin_service_id AND tenant_scope = v_scope
    AND event_type = p_event_type AND stream_id = p_stream_id;

  RETURN TRUE;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_repair(UUID, TEXT, TEXT, UUID, TIMESTAMPTZ, INTEGER, INTEGER) IS
'True when a repair should be requested now, on an exponential ladder that flattens to its terminal cadence (base x 2^6) at p_max_attempts. Attempt counts survive restarts; a capped bucket still earns one ask per terminal interval so a static deficit is never shadow-banned forever, unless a redelivery showed repair cannot change it (249).';

-- wh_integrity_claim_repair_drain: last word 099, skipping buckets marked unhealable (249).
-- <docs>resilience/stream-integrity#unhealable-buckets</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerUnhealableSqlTests.cs:ClaimRepairDrain_SkipsAnUnhealableBucketAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_claim_repair_drain(
  p_origin_ids        UUID[],
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER,
  p_limit             INTEGER
) RETURNS TABLE (
  origin_service_id UUID,
  tenant_scope      TEXT,
  event_type        TEXT,
  stream_id         UUID,
  window_from       BIGINT,
  window_until      BIGINT
)
LANGUAGE plpgsql
AS $$
BEGIN
  RETURN QUERY
  WITH eligible AS (
    SELECT l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id
    FROM __SCHEMA__.wh_integrity_ledger l
    WHERE l.origin_service_id = ANY(p_origin_ids)
      AND l.stream_id <> __EMPTY_UUID__::uuid
      AND l.unhealable_at IS NULL  -- 249
      AND (l.last_repair_at IS NULL
           OR (p_now - l.last_repair_at) >= make_interval(
                secs => p_base_backoff_secs * POWER(2,
                  CASE WHEN l.repair_attempts >= p_max_attempts THEN 6
                       ELSE LEAST(GREATEST(l.repair_attempts - 1, 0), 6) END)))
    ORDER BY l.last_repair_at ASC NULLS FIRST, l.first_seen_at ASC
    LIMIT p_limit
    FOR UPDATE SKIP LOCKED
  )
  UPDATE __SCHEMA__.wh_integrity_ledger l
  SET repair_attempts = l.repair_attempts + 1,
      last_repair_at  = p_now,
      last_touched    = p_now
  FROM eligible e
  WHERE l.origin_service_id = e.origin_service_id
    AND l.tenant_scope = e.tenant_scope
    AND l.event_type = e.event_type
    AND l.stream_id = e.stream_id
  RETURNING l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id, l.window_from, l.window_until;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_claim_repair_drain(UUID[], TIMESTAMPTZ, INTEGER, INTEGER, INTEGER) IS
'Atomically claims up to p_limit repair-eligible ledger rows for the paced drain: same eligibility ladder as wh_integrity_try_begin_repair (backoff capped at 6 doublings, terminal cadence at the attempt cap, never a bucket marked unhealable), least-recently-attempted first, SKIP LOCKED against concurrent drainers, attempt stamped on claim.';
