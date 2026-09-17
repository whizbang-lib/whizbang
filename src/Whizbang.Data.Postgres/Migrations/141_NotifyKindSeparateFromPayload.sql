-- Migration: 141_NotifyKindSeparateFromPayload
-- Date: 2026-09-07
-- Description: The doorbell-debounce KEY and the NOTIFY PAYLOAD are separate arguments (issue #702).
--
--   130 keyed wh_notify_state on (instance_id, payload_kind) with the kind sized to the closed
--   doorbell vocabulary (outbox, inbox, perspective, schedule), and 137 made the fire path insert
--   the value unconditionally. notify_instance_owners(p_payload, p_stream_ids) had a second caller
--   all along: the signal transport passes a signal's wire name, whose default is the fully
--   qualified type name. One parameter carried a closed enum from SQL and an open-ended type name
--   from C#; after 137 any targeted signal with a wire name over twenty characters failed to
--   publish (22001). The C# call site was correct when written; a later migration changed what
--   the parameter meant without changing its name or its other caller.
--
--   Fix: the two concepts stop sharing a parameter.
--     _notify_debounced(instance, kind, payload, window)            keys the state on kind, notifies payload
--     notify_instance_owners_with_payload(kind, payload, stream_ids) the general form (signals use it)
--     notify_instance_owners(payload, stream_ids)                   the DOORBELL form: payload IS kind,
--                                                                   and only the doorbell vocabulary is
--                                                                   accepted (an open-ended value is the
--                                                                   drift that produced #702, rejected up
--                                                                   front with a hint to the general form)
--   The general form is a separate function rather than a second overload: the schema
--   initializer's duplicate-overload sweep treats any framework function with more than one
--   overload as a stale leftover and force-replays its defining migrations on every start.
--   The key column becomes unbounded text: a signal's debounce key is its wire name (the signal's
--   consumer is what the doorbell wakes, so per wire name is the exact analog of per doorbell
--   kind). claim_work never arms a signal's watermark, so a signal is never suppressed ("a fire
--   never arms suppression", 131); its row carries the fire counters only.
--   The 18 SQL doorbell call sites keep calling the two-argument form: for a doorbell the payload
--   is its kind by definition, and passing the same literal twice would invite the opposite
--   drift (a doorbell whose key and payload disagree). Both bodies are reproduced verbatim from
--   137 and 130 with only the parameter split (rule 5). _notify_debounced changes arity, so its
--   old overload is dropped through drop_all_overloads (schema baked in, safe under both runners,
--   unlike the hand-written DROP that bit 137's first cut), and the bare COMMENT ON FUNCTION
--   statements in 130 and 131 carry an argument list now: a replay recreates the three-argument
--   overload beside this one until this file runs again, and a bare name is ambiguous (42725).
--
--   Later, in place: the deterministic-target branch computes the partition number instead of
--   reading it. It had recovered one number per unclaimed stream by reading partition_number out of
--   the queue table for that kind, matching stream_id against the unclaimed set, once per kind. That
--   lookup carries no status predicate and every stream_id index on the queue tables is partial on
--   one, so no index ever applied: each ring read every row those streams had ever written, settled
--   history included. Measured on a deployed fleet during a bulk import, the outbox branch alone
--   took 14,383 sequential scans reading 165 million tuples to announce 36 thousand inserted rows
--   -- 4,550 tuples read per row written -- and the doorbell, not the insert, was where the store
--   spent its time. compute_partition (001) is IMMUTABLE and total in the stream id and is the only
--   writer of partition_number, so the number was always derivable from the argument already in
--   hand; the three branches differed only in which table they read, and collapse into one that
--   reads no queue table at all. The kind still gates the branch, so a schedule doorbell and a
--   signal's wire name reach it exactly as before, which is to say not at all.
--   The identity is not quite universal, so the branch prefers the stream ledger's stored number and
--   computes only for a stream the ledger has never held: recover_dead_letter writes partition 0
--   whatever the stream id hashes to, and the partition count is a consumer setting, so a
--   deployment that changes it stamped its rows with a count compute_partition's default does not
--   know. The ledger carries the number the claim routes on in both cases, and the lookup is one
--   primary-key probe against a table this function already reads twice.
--
-- Dependencies: 130 (wh_notify_state, notify_instance_owners), 137 (_notify_debounced current text), 001 (compute_partition)
-- Objects: wh_notify_state.payload_kind (TEXT), _notify_debounced, notify_instance_owners_with_payload, notify_instance_owners

ALTER TABLE __SCHEMA__.wh_notify_state
  ALTER COLUMN payload_kind TYPE TEXT;

COMMENT ON COLUMN __SCHEMA__.wh_notify_state.payload_kind IS
  'Debounce key (141): a doorbell kind (outbox, inbox, perspective, schedule) or a signal''s wire '
  'name. Separate from the NOTIFY payload: for a doorbell the two coincide by definition; for a '
  'signal the wire name is both, but the key is never sized to the doorbell vocabulary.';

COMMENT ON TABLE __SCHEMA__.wh_notify_state IS
'Doorbell-debounce watermarks (130, key widened in 141): one row per (instance, debounce key) — an outbox doorbell must never swallow a perspective one, last_work_at stamped by claim_work when the instance finds work and slid by suppressed stores. While fresher than the notify_debounce_seconds setting, notifies toward that instance are suppressed — it is draining or lingering and will find the work by polling. Rows for departed instances age out harmlessly (suppression requires a live heartbeat) and are pruned opportunistically. A signal''s row (keyed by wire name) is never armed by claim_work, so a signal is never suppressed.';

-- ============================================================================
-- _notify_debounced — reproduced verbatim from 137; p_kind keys the state, p_payload is notified.
-- ============================================================================
SELECT __SCHEMA__.drop_all_overloads('_notify_debounced');

CREATE OR REPLACE FUNCTION __SCHEMA__._notify_debounced(
  p_instance_id UUID,
  p_kind TEXT,
  p_payload TEXT,
  p_window INTEGER
) RETURNS VOID AS $$
DECLARE
  v_now TIMESTAMPTZ := NOW();
  v_live BOOLEAN;
  v_last_attempt TIMESTAMPTZ;
  v_last_work TIMESTAMPTZ;
  v_rapid_run INTEGER;
  v_floor_ms INTEGER;
  v_rapid_gap_ms INTEGER;
  v_churn_run INTEGER;
  v_gap_ms DOUBLE PRECISION;
  v_effective_ms INTEGER;
  v_suppress BOOLEAN := FALSE;
BEGIN
  -- Adaptive knobs (the ceiling arrives as p_window; read the floor/rapid-gap/churn once).
  SELECT COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_debounce_floor_ms'), 50),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_rapid_gap_ms'), 100),
         COALESCE((SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
                   WHERE setting_key = 'notify_churn_run'), 5)
  INTO v_floor_ms, v_rapid_gap_ms, v_churn_run;

  -- Only a LIVE target may ever be suppressed: a corpse's fresh watermark must not strand work
  -- — its doorbell must fire so the deterministic re-target path engages (130).
  SELECT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_service_instances si
    WHERE si.instance_id = p_instance_id
      AND si.last_heartbeat_at > v_now - INTERVAL '30 seconds')
  INTO v_live;

  -- This target's current state (row may not exist yet — first doorbell after idle).
  SELECT last_attempt_at, last_work_at, rapid_run
    INTO v_last_attempt, v_last_work, v_rapid_run
  FROM __SCHEMA__.wh_notify_state
  WHERE instance_id = p_instance_id AND payload_kind = p_kind;

  -- VOLUME axis: advance the rapid run while doorbells arrive closer than the rapid gap; reset
  -- on the first calm gap. No prior attempt = calm (a lone doorbell after idle).
  v_gap_ms := CASE WHEN v_last_attempt IS NULL THEN NULL
                   ELSE EXTRACT(EPOCH FROM (v_now - v_last_attempt)) * 1000 END;
  IF v_gap_ms IS NOT NULL AND v_gap_ms < v_rapid_gap_ms THEN
    v_rapid_run := COALESCE(v_rapid_run, 0) + 1;
  ELSE
    v_rapid_run := 0;
  END IF;

  -- TIME axis: floor window normally; escalate to the ceiling (p_window seconds) once the run
  -- trips churn. p_window <= 0 is the global OFF switch — suppression disabled (floor ignored).
  IF p_window <= 0 THEN
    v_effective_ms := 0;
  ELSIF v_rapid_run >= v_churn_run THEN
    v_effective_ms := p_window * 1000;
  ELSE
    v_effective_ms := GREATEST(v_floor_ms, 0);
  END IF;

  -- Suppress iff: live AND the drainer is genuinely draining this kind (found-work watermark
  -- fresh within the EFFECTIVE window). A NULL last_work_at (fire-born row, claim_work never
  -- armed) can never satisfy this — "a fire never arms suppression" (131), by construction.
  IF v_live AND v_effective_ms > 0 AND v_last_work IS NOT NULL
     AND v_last_work > v_now - (v_effective_ms * INTERVAL '1 millisecond') THEN
    v_suppress := TRUE;
  END IF;

  IF v_suppress THEN
    -- Slide the found-work watermark (a suppressed store IS work the linger poll will find) and
    -- record the attempt + rate state + the suppressed count for OTel.
    UPDATE __SCHEMA__.wh_notify_state
    SET last_work_at = v_now,
        last_attempt_at = v_now,
        rapid_run = v_rapid_run,
        effective_window_ms = v_effective_ms,
        suppressed_count = suppressed_count + 1
    WHERE instance_id = p_instance_id AND payload_kind = p_kind;
  ELSE
    PERFORM pg_notify('wh_work_i_' || p_instance_id::text, p_payload);
    -- Record the attempt + rate state + fired count WITHOUT arming suppression: on INSERT
    -- last_work_at stays NULL (only claim_work arms it); on CONFLICT it is left untouched.
    INSERT INTO __SCHEMA__.wh_notify_state
      (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run,
       effective_window_ms, fired_count)
    VALUES (p_instance_id, p_kind, NULL, v_now, v_rapid_run, v_effective_ms, 1)
    ON CONFLICT (instance_id, payload_kind) DO UPDATE
      SET last_attempt_at = EXCLUDED.last_attempt_at,
          rapid_run = EXCLUDED.rapid_run,
          effective_window_ms = EXCLUDED.effective_window_ms,
          fired_count = __SCHEMA__.wh_notify_state.fired_count + 1;
    -- Opportunistic hygiene on the rare fire path: rows for long-departed instances, aged by
    -- most-recent activity (either watermark) so fire-born NULL-last_work_at rows also expire.
    DELETE FROM __SCHEMA__.wh_notify_state
    WHERE GREATEST(COALESCE(last_work_at, 'epoch'::timestamptz),
                   COALESCE(last_attempt_at, 'epoch'::timestamptz)) < v_now - INTERVAL '7 days';
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._notify_debounced(UUID, TEXT, TEXT, INTEGER) IS
  'Suppress-or-fire for one target instance (130/131/137, kind split from payload in 141): the '
  'wh_notify_state row is keyed by p_kind; p_payload is what pg_notify carries when the doorbell '
  'fires. Suppression requires a live target whose found-work watermark for this kind is fresh '
  'within the effective (adaptive) window.';

-- ============================================================================
-- notify_instance_owners_with_payload(kind, payload, stream_ids) — the general form, reproduced
-- verbatim from 130's notify_instance_owners with the split.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.notify_instance_owners_with_payload(
  p_kind TEXT,
  p_payload TEXT,
  p_stream_ids UUID[]
) RETURNS VOID AS $$
DECLARE
  v_unclaimed_streams UUID[];
  v_active_count INTEGER;
  v_debounce INTEGER;
BEGIN
  SELECT COALESCE(
    (SELECT setting_value::INTEGER FROM __SCHEMA__.wh_settings
     WHERE setting_key = 'notify_debounce_seconds'), 7)
  INTO v_debounce;

  -- Step 1 (unchanged targeting): per-owner notify for streams in wh_active_streams.
  PERFORM __SCHEMA__._notify_debounced(a.assigned_instance_id, p_kind, p_payload, v_debounce)
  FROM (
    SELECT DISTINCT assigned_instance_id
    FROM __SCHEMA__.wh_active_streams
    WHERE stream_id = ANY(p_stream_ids)
      AND assigned_instance_id IS NOT NULL
  ) a;

  -- Step 2 (unchanged targeting): deterministic-target notify for unclaimed streams.
  SELECT ARRAY_AGG(s) INTO v_unclaimed_streams
  FROM unnest(p_stream_ids) AS s
  WHERE NOT EXISTS (
    SELECT 1 FROM __SCHEMA__.wh_active_streams a
    WHERE a.stream_id = s
      AND a.assigned_instance_id IS NOT NULL
  );

  IF v_unclaimed_streams IS NULL OR cardinality(v_unclaimed_streams) = 0 THEN
    RETURN;
  END IF;

  SELECT COUNT(*)::INTEGER INTO v_active_count
  FROM __SCHEMA__.wh_service_instances
  WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds';

  IF v_active_count = 0 THEN
    RETURN;
  END IF;

  -- The partition number is COMPUTED, never read. This branch needs one number per unclaimed
  -- stream, and compute_partition (001) is IMMUTABLE and total in the stream id: it is the only
  -- thing that ever writes partition_number into the queue tables, and recompute_partition_numbers
  -- (041) exists to restore that identity wherever a partition count changed. Recovering the number
  -- by reading the table asked a question the argument already answered, and asked it the most
  -- expensive way available: "stream_id = ANY(...)" carries no status predicate, every stream_id
  -- index on the queue tables is partial on one, so no index applied and each ring read every row
  -- those streams had ever written, settled history included. Measured on a deployed fleet during a
  -- bulk import, the three branches together read hundreds of millions of tuples sequentially to
  -- announce tens of thousands of inserted rows, and the doorbell, not the insert, was where the
  -- store spent its time.
  --
  -- Two consequences worth naming, both deliberate:
  --   * The three branches collapse into one. They differed only in which table they read the
  --     number out of, and nothing reads a table now, so the kind no longer selects a query. It
  --     still GATES the branch: a doorbell kind other than these three (schedule) and a signal's
  --     wire name reached no branch before and must reach none now, or every targeted signal would
  --     start waking a deterministic owner it never woke.
  --   * A stream the caller names that has no row in the queue table changes from "notify nobody"
  --     to "notify its deterministic owner". That is what the branch is for -- it exists to wake the
  --     instance that would claim a stream nothing has claimed yet -- and deriving the target from
  --     stored rows made it fall silent exactly when the rows were not visible to it.
  IF p_kind IN (__CATEGORY_OUTBOX__, __CATEGORY_INBOX__, __CATEGORY_PERSPECTIVE__) THEN
    PERFORM __SCHEMA__._notify_debounced(targets.target_instance_id, p_kind, p_payload, v_debounce)
    FROM (
      WITH live AS (
        SELECT instance_id,
               (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM __SCHEMA__.wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT DISTINCT live.instance_id AS target_instance_id
      FROM unnest(v_unclaimed_streams) AS s
      JOIN live ON live.rank = (
        -- The stream ledger's number first, computed only for a stream it has never held. The
        -- ledger is keyed by stream id, so this is one primary-key probe per unclaimed stream
        -- against a table this function already reads twice, never a scan of a queue table. It is
        -- also the number the claim itself routes on, which matters in the two places where the
        -- stored number and the computed one part company: a row put back by recover_dead_letter
        -- carries partition 0 whatever its stream id hashes to, and a deployment that configures a
        -- partition count other than the default stamped its rows with that count while
        -- compute_partition here would assume the default. In both cases the ledger agrees with the
        -- claim and a bare computation would not, so the doorbell would wake an instance that is
        -- not allowed to take the work and the real owner would wait out its poll.
        COALESCE(
          (SELECT ast.partition_number
           FROM __SCHEMA__.wh_active_streams ast
           WHERE ast.stream_id = s),
          __SCHEMA__.compute_partition(s)
        ) % v_active_count)
    ) AS targets;
  END IF;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_instance_owners_with_payload(TEXT, TEXT, UUID[]) IS
'Instance-routed NOTIFY emission with doorbell debounce (slice 27 + v0.685 + 130, kind split from payload in 141). Per-owner for pinned streams; rank-deterministic for unclaimed outbox/inbox/perspective streams, where the partition number is computed from the stream id through compute_partition rather than read out of the queue table -- reading it made every ring cost what the named streams had ever written, because the lookup carries no status predicate and every stream_id index on those tables is partial on one. A named stream with no row in the queue table therefore reaches its deterministic owner now instead of nobody, which is what the branch is for. Every emission goes through _notify_debounced(instance, p_kind, p_payload, window). Signals call this form with their wire name as both, and reach only the per-owner path.';

-- ============================================================================
-- notify_instance_owners(payload, stream_ids) — the doorbell form. Payload IS kind; the closed
-- doorbell vocabulary is enforced here so an open-ended value fails loudly at the producer.
-- The signature is unchanged from 045/130, so drop_all_overloads is not needed and every SQL
-- doorbell call site keeps working unmodified.
-- ============================================================================
CREATE OR REPLACE FUNCTION __SCHEMA__.notify_instance_owners(
  p_payload TEXT,
  p_stream_ids UUID[]
) RETURNS VOID AS $$
BEGIN
  -- The parameter keeps its 045/130 name: Postgres refuses to rename an input parameter in place
  -- (42P13), and a replay recreates 045's definition before this one runs again. For a doorbell
  -- the payload IS the kind, so the one argument is both.
  IF p_payload IS NULL OR p_payload NOT IN ('outbox', 'inbox', 'perspective', 'schedule') THEN
    RAISE EXCEPTION
      'notify_instance_owners(payload, stream_ids) is the doorbell form and accepts only outbox, inbox, perspective or schedule (got %); a signal carries its own payload and calls notify_instance_owners_with_payload(kind, payload, stream_ids)',
      COALESCE(p_payload, '<null>')
      USING ERRCODE = 'invalid_parameter_value';
  END IF;
  PERFORM __SCHEMA__.notify_instance_owners_with_payload(p_payload, p_payload, p_stream_ids);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.notify_instance_owners(TEXT, UUID[]) IS
'Doorbell form (141): the payload is the kind by definition, and only the doorbell vocabulary (outbox, inbox, perspective, schedule) is accepted; anything else raises invalid_parameter_value with a pointer to the general form. Every SQL doorbell call site uses this form.';
