-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 247_FanoutChildrenAreFiledUnderTheirOwnStreams.sql
-- Date: 2026-10-27
-- Description: Moves stored events to the stream their payload names, for the repair of composite children a
--              receiver filed under the composite's stream (#1798).
--
--              A receiver expands a typed composite at its dispatch seam and stores only the children. Until this
--              release every typed child was filed under the composite's stream, not the stream its payload's
--              [StreamId] names (the stream its producer stored it on). Live apply and the rewind key a row by the
--              stream an event is stored on, so each child was folded onto a row keyed by the composite (a row no
--              reader asks for) and never reached the row it is about; a rebuild of the composite's stream then
--              routed the child by its payload onto that row, from an empty model, and recorded the composite
--              stream's position there, which the ordering guard held against every later rebuild of the row.
--
--              The receiver now files each child under its own stream. Rows already stored are moved by
--              wh_refile_events, which the operator repair (PostgresFanoutChildRefiler) calls with the events it
--              found and the stream each one's payload names. SQL cannot read a payload's [StreamId] (the property
--              differs by type and only the generated extractor knows it), so the choice of events is the caller's
--              and this function does the bookkeeping, atomically and under the streams' append locks.
--
--              How a moved event is numbered, and why:
--                * it takes the next versions of its new stream, after the stream's tail, in producer order (origin
--                  commit sequence, then local commit sequence, then id). Nothing already on that stream is
--                  renumbered, so its versions stay contiguous and every cursor and position recorded against them
--                  stays true. A replay still folds the event in producer order: ReplayEventOrder orders a stream
--                  received from one origin by the origin's commit sequence, not by version;
--                * the stream it leaves is renumbered in its old order, from the version it started at, when events
--                  remain on it, so that stream stays contiguous too. A stream left with no events is the caller's to
--                  purge (its row is a ghost);
--                * it keeps its commit sequence. The position a rebuild of the new stream records covers it, so the
--                  ordering guard admits the rebuild's write over a row that recorded this event's position.
--
--              What else moves with it, in the same transaction:
--                * its integrity digest: the event's hash leaves the old stream's bucket and joins the new one's,
--                  under the predicates every digest fold uses (not ephemeral, not at-most-once, not exempt);
--                * its inbox rows (wh_inbox, wh_inbox_state), so a purge of the stream it left keeps them and their
--                  deduplication entries;
--                * its applied-ledger rows (wh_perspective_applied);
--              and what is dropped:
--                * any pending perspective work for it (wh_perspective_events): that work would apply it to the old
--                  row. The rebuild of the new stream applies it to the right one;
--                * the snapshots of both streams: each folds a history this move changes;
--                * a cursor of the old stream that names a moved event.
--              The integrity ledger is left to the next sweep, which reads the digests this keeps true.
--
--              Idempotent: an event already on the stream it is asked to move to is skipped, so a repeated call
--              changes nothing. A preview is the same call in a transaction the caller rolls back, so its report is
--              exactly what the repair would do.
-- Dependencies: 230 (the inbox emit chain's append lock and digest fold), 216 (__DIGEST_EXEMPT_TYPES__),
--               214 (the at-most-once flag), 180 (wh_purge_streams, which the caller uses for emptied streams)
-- Objects: wh_refile_events
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_refile_events');

-- <docs>operations/infrastructure/purging-streams#refiling-composite-children</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanoutChildRefileSqlTests.cs</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_refile_events(
  p_event_ids UUID[],
  p_target_streams UUID[]
) RETURNS TABLE(event_id UUID, from_stream UUID, to_stream UUID, to_version BIGINT) AS $$
#variable_conflict use_column
DECLARE
  c_flag_ephemeral CONSTANT INTEGER := 8;      -- EventFlags.Ephemeral (1 << 3)
  c_flag_at_most_once CONSTANT INTEGER := 32;  -- EventFlags.AtMostOnce (1 << 5), 214
BEGIN
  IF cardinality(p_event_ids) IS DISTINCT FROM cardinality(p_target_streams) THEN
    RAISE EXCEPTION 'wh_refile_events: % event ids but % target streams', cardinality(p_event_ids), cardinality(p_target_streams);
  END IF;
  IF __EMPTY_UUID__::uuid = ANY(p_target_streams) THEN
    RAISE EXCEPTION 'wh_refile_events: the empty stream id names no stream';
  END IF;

  -- The streams an append to either side would lock, in the emit chain's order, before anything is read.
  PERFORM pg_advisory_xact_lock(hashtext('wh_event_store:' || s.sid::text))
  FROM (
    SELECT DISTINCT x.sid
    FROM (
      SELECT es.stream_id AS sid
      FROM unnest(p_event_ids, p_target_streams) AS m(eid, target)
      JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
      WHERE es.stream_id <> m.target
      UNION
      SELECT m.target
      FROM unnest(p_event_ids, p_target_streams) AS m(eid, target)
      JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
      WHERE es.stream_id <> m.target
    ) x
    ORDER BY x.sid
  ) s;

  CREATE TEMP TABLE IF NOT EXISTS _wh_refile_moves (
    event_id UUID PRIMARY KEY,
    from_stream UUID NOT NULL,
    to_stream UUID NOT NULL,
    to_version BIGINT NOT NULL,
    origin_service_id UUID,
    scope_tenant TEXT NOT NULL,
    event_type TEXT NOT NULL,
    digested BOOLEAN NOT NULL
  ) ON COMMIT DROP;
  TRUNCATE _wh_refile_moves;

  INSERT INTO _wh_refile_moves
  SELECT
    es.event_id,
    es.stream_id,
    m.target,
    COALESCE((SELECT MAX(t.version) FROM __SCHEMA__.wh_event_store t WHERE t.stream_id = m.target), 0)
      + ROW_NUMBER() OVER (
          PARTITION BY m.target
          ORDER BY es.origin_commit_sequence NULLS LAST, es.commit_sequence NULLS LAST, es.event_id),
    es.origin_service_id,
    COALESCE(es.scope ->> 't', ''),
    es.event_type,
    COALESCE(es.flags, 0) & c_flag_ephemeral = 0
      AND COALESCE(es.flags, 0) & c_flag_at_most_once = 0
      AND split_part(es.event_type, ',', 1) <> ALL(__DIGEST_EXEMPT_TYPES__)
  FROM (SELECT DISTINCT ON (u.eid) u.eid, u.target FROM unnest(p_event_ids, p_target_streams) AS u(eid, target) ORDER BY u.eid) m
  JOIN __SCHEMA__.wh_event_store es ON es.event_id = m.eid
  WHERE es.stream_id <> m.target;

  -- The move itself. New versions sit past the target's tail, so no (stream_id, version) pair collides.
  UPDATE __SCHEMA__.wh_event_store es
  SET stream_id = r.to_stream, aggregate_id = r.to_stream, version = r.to_version
  FROM _wh_refile_moves r
  WHERE es.event_id = r.event_id;

  -- The streams left behind keep contiguous versions, in their old order, from the version they started at (a stream
  -- written by a direct append starts at 0, one stored from the inbox at 1). The new numbers are worked out first; the
  -- stream is then negated, so the renumbering never meets a version another row of the stream still holds.
  CREATE TEMP TABLE IF NOT EXISTS _wh_refile_renumber (event_id UUID PRIMARY KEY, new_version BIGINT NOT NULL) ON COMMIT DROP;
  TRUNCATE _wh_refile_renumber;
  INSERT INTO _wh_refile_renumber
  SELECT t.event_id,
         MIN(t.version) OVER (PARTITION BY t.stream_id) + ROW_NUMBER() OVER (PARTITION BY t.stream_id ORDER BY t.version) - 1
  FROM __SCHEMA__.wh_event_store t
  WHERE t.stream_id IN (SELECT DISTINCT r.from_stream FROM _wh_refile_moves r);
  UPDATE __SCHEMA__.wh_event_store es SET version = -es.version - 1
  FROM _wh_refile_renumber n WHERE es.event_id = n.event_id;
  UPDATE __SCHEMA__.wh_event_store es SET version = n.new_version
  FROM _wh_refile_renumber n WHERE es.event_id = n.event_id;

  -- Integrity digests: XOR is self-inverse, so the event's hash leaves one bucket and joins the other.
  WITH leaving AS (
    SELECT COALESCE(r.origin_service_id, __EMPTY_UUID__::uuid) AS origin, r.scope_tenant, r.event_type, r.from_stream AS stream_id,
           bit_xor(hashtextextended(r.event_id::text, 0)) AS lo, bit_xor(hashtextextended(r.event_id::text, 1)) AS hi,
           COUNT(*)::int AS n
    FROM _wh_refile_moves r
    WHERE r.digested
    GROUP BY 1, 2, 3, 4
  )
  UPDATE __SCHEMA__.wh_stream_digests d
  SET digest_lo = d.digest_lo # l.lo, digest_hi = d.digest_hi # l.hi, event_count = d.event_count - l.n, updated_at = NOW()
  FROM leaving l
  WHERE d.origin_service_id = l.origin AND d.scope_tenant = l.scope_tenant AND d.event_type = l.event_type
    AND d.stream_id = l.stream_id;
  DELETE FROM __SCHEMA__.wh_stream_digests d
  WHERE d.event_count <= 0
    AND d.stream_id IN (SELECT DISTINCT r.from_stream FROM _wh_refile_moves r);

  INSERT INTO __SCHEMA__.wh_stream_digests AS d
    (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
  SELECT COALESCE(r.origin_service_id, __EMPTY_UUID__::uuid), r.scope_tenant, r.event_type, r.to_stream,
         bit_xor(hashtextextended(r.event_id::text, 0)), bit_xor(hashtextextended(r.event_id::text, 1)),
         COUNT(*)::int, NOW()
  FROM _wh_refile_moves r
  WHERE r.digested
  GROUP BY 1, 2, 3, 4
  ORDER BY 1, 2, 3, 4
  ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
    digest_lo = d.digest_lo # EXCLUDED.digest_lo,
    digest_hi = d.digest_hi # EXCLUDED.digest_hi,
    event_count = d.event_count + EXCLUDED.event_count,
    updated_at = EXCLUDED.updated_at;

  -- Rows keyed by the event that also name its stream follow it.
  UPDATE __SCHEMA__.wh_inbox i SET stream_id = r.to_stream FROM _wh_refile_moves r WHERE i.message_id = r.event_id;
  UPDATE __SCHEMA__.wh_inbox_state st SET stream_id = r.to_stream FROM _wh_refile_moves r WHERE st.message_id = r.event_id;
  UPDATE __SCHEMA__.wh_perspective_applied pa SET stream_id = r.to_stream FROM _wh_refile_moves r WHERE pa.event_id = r.event_id;

  -- Work that would apply the event to the old row, and folds and positions that no longer describe either stream.
  DELETE FROM __SCHEMA__.wh_perspective_events pe USING _wh_refile_moves r WHERE pe.event_id = r.event_id;
  DELETE FROM __SCHEMA__.wh_perspective_snapshots sn
  WHERE sn.stream_id IN (SELECT r.from_stream FROM _wh_refile_moves r UNION SELECT r.to_stream FROM _wh_refile_moves r);
  DELETE FROM __SCHEMA__.wh_perspective_cursors c USING _wh_refile_moves r
  WHERE c.stream_id = r.from_stream AND c.last_event_id = r.event_id;

  RETURN QUERY SELECT r.event_id, r.from_stream, r.to_stream, r.to_version FROM _wh_refile_moves r ORDER BY r.to_stream, r.to_version;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_refile_events(UUID[], UUID[]) IS
  '247 (#1798): moves each stored event to the stream named beside it, numbered after that stream''s tail in producer '
  'order; renumbers the streams left behind; moves the integrity digest, inbox and applied-ledger rows with it; drops '
  'the pending perspective work, both streams'' snapshots and a cursor naming a moved event. An event already on its '
  'target is skipped. A caller previews it by rolling its transaction back.';
