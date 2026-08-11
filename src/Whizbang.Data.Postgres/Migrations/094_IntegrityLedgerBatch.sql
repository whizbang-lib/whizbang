-- 094_IntegrityLedgerBatch.sql
--
-- Set-based entry points for the stream-integrity ledger (docs: resilience/stream-integrity).
--
-- A manifest chunk carries up to 500 buckets, and the comparator consulted the ledger PER BUCKET —
-- up to ~1000 sequential round trips per chunk. Observed live: each comparison took seconds,
-- manifests arrived faster than they could be compared, and every waiting manifest held its
-- fully-deserialized payload in memory behind the comparator's gate — a fleet-wide OOM-crashloop
-- whose restart storm then amplified the arrival rate it could not keep up with.
--
-- Each batch function LOOPS THE EXISTING SINGLE-KEY FUNCTION inside one call, so the semantics
-- (cooldown, signature-change reset, backoff, attempt budget, row locking) cannot drift — the
-- batch is exactly N singles minus N-1 round trips. The repair batch additionally stops CALLING
-- once p_max_grants have been granted: the single function records an attempt when it grants, so
-- granting past the caller's budget and discarding would burn attempt budget for nothing.
--
-- Dependencies: 090 (wh_integrity_ledger + the single-key functions)

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_report_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_origin_los        BIGINT[],
  p_origin_his        BIGINT[],
  p_local_los         BIGINT[],
  p_local_his         BIGINT[],
  p_now               TIMESTAMPTZ,
  p_cooldown_seconds  INTEGER
) RETURNS BOOLEAN[]
LANGUAGE plpgsql
AS $$
DECLARE
  v_n       INTEGER := COALESCE(array_length(p_stream_ids, 1), 0);
  v_results BOOLEAN[] := '{}';
  v_i       INTEGER;
BEGIN
  FOR v_i IN 1 .. v_n LOOP
    v_results := v_results || __SCHEMA__.wh_integrity_try_begin_report(
      p_origin_service_id, p_tenant_scopes[v_i], p_event_types[v_i], p_stream_ids[v_i],
      p_origin_los[v_i], p_origin_his[v_i], p_local_los[v_i], p_local_his[v_i],
      p_now, p_cooldown_seconds);
  END LOOP;
  RETURN v_results;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_report_batch IS
'One round trip for a manifest chunk''s report decisions — element i answers key i, via the single-key function (identical semantics).';

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_try_begin_repair_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[],
  p_now               TIMESTAMPTZ,
  p_base_backoff_secs INTEGER,
  p_max_attempts      INTEGER,
  p_max_grants        INTEGER
) RETURNS BOOLEAN[]
LANGUAGE plpgsql
AS $$
DECLARE
  v_n       INTEGER := COALESCE(array_length(p_stream_ids, 1), 0);
  v_results BOOLEAN[] := '{}';
  v_granted INTEGER := 0;
  v_one     BOOLEAN;
  v_i       INTEGER;
BEGIN
  FOR v_i IN 1 .. v_n LOOP
    IF v_granted >= p_max_grants THEN
      -- Budget spent: do not even ASK — the single function records an attempt when it grants,
      -- and a grant the caller must discard burns backoff budget for nothing.
      v_results := v_results || FALSE;
      CONTINUE;
    END IF;
    v_one := __SCHEMA__.wh_integrity_try_begin_repair(
      p_origin_service_id, p_tenant_scopes[v_i], p_event_types[v_i], p_stream_ids[v_i],
      p_now, p_base_backoff_secs, p_max_attempts);
    IF v_one THEN
      v_granted := v_granted + 1;
    END IF;
    v_results := v_results || v_one;
  END LOOP;
  RETURN v_results;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_try_begin_repair_batch IS
'One round trip for a chunk''s repair decisions, capped at p_max_grants IN ORDER — past the cap keys are not consulted at all, so no attempt budget is burned on grants the caller cannot use.';

CREATE OR REPLACE FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch(
  p_origin_service_id UUID,
  p_tenant_scopes     TEXT[],
  p_event_types       TEXT[],
  p_stream_ids        UUID[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
  v_n INTEGER := COALESCE(array_length(p_stream_ids, 1), 0);
  v_i INTEGER;
BEGIN
  FOR v_i IN 1 .. v_n LOOP
    PERFORM __SCHEMA__.wh_integrity_mark_healed(
      p_origin_service_id, p_tenant_scopes[v_i], p_event_types[v_i], p_stream_ids[v_i]);
  END LOOP;
END;
$$;

COMMENT ON FUNCTION __SCHEMA__.wh_integrity_mark_healed_batch IS
'One round trip to forget a chunk''s healed buckets.';
