-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 602_ARewindStartsOnlyFromAnEventThePerspectiveReads.sql (#2601): 600's floor comes back verbatim, and the
-- repair function and its setting go.
--
-- What the repair changed is not restored. It cleared rewinds whose perspective reads nothing at or after the trigger,
-- moved others up to the first event their perspective reads, and deleted queued rows nothing would apply. Putting any of
-- that back would bring back the stall the repair ended: a row leased forever with nothing recorded against it, and a
-- startup in Blocking mode waiting on a rewind that cannot run.

SELECT __SCHEMA__.drop_all_overloads('wh_rewind_reap_floor');
-- <docs>resilience/stream-integrity#a-rewind-never-reaches-below-the-reap-floor</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillSqlTests.cs:ReapFloor_IsTheFirstEventWithABodyThePerspectiveReads_AtOrAfterTheTriggerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_rewind_reap_floor(
  p_stream_ids UUID[],
  p_perspective_names TEXT[],
  p_trigger_event_ids UUID[]
) RETURNS TABLE(stream_id UUID, perspective_name TEXT, asked_trigger_event_id UUID, trigger_event_id UUID) AS $$
  SELECT r.sid, r.pname, r.tid, fl.event_id
  FROM unnest(p_stream_ids, p_perspective_names, p_trigger_event_ids) WITH ORDINALITY AS r(sid, pname, tid, ord)
  JOIN __SCHEMA__.wh_event_store t ON t.event_id = r.tid AND t.stream_id = r.sid
  LEFT JOIN LATERAL (
    SELECT es.event_id
    FROM __SCHEMA__.wh_event_store es
    WHERE es.stream_id = r.sid
      AND es.version >= t.version
      AND EXISTS (SELECT 1 FROM __SCHEMA__.wh_event_body b WHERE b.event_id = es.event_id)
      AND (es.event_id = r.tid
           OR NOT EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_message_associations ma
             WHERE ma.target_name = r.pname
               AND ma.association_type = __CATEGORY_PERSPECTIVE__
               AND ma.normalized_message_type IS NOT NULL)
           OR EXISTS (
             SELECT 1 FROM __SCHEMA__.wh_message_associations ma
             WHERE ma.target_name = r.pname
               AND ma.association_type = __CATEGORY_PERSPECTIVE__
               AND ma.normalized_message_type = es.event_type))
    ORDER BY es.version
    LIMIT 1
  ) fl ON TRUE
  ORDER BY r.ord;
$$ LANGUAGE sql STABLE;

COMMENT ON FUNCTION __SCHEMA__.wh_rewind_reap_floor(UUID[], TEXT[], UUID[]) IS
'600 (#2591): for each (stream, perspective, trigger) triple whose trigger the store holds, the first event of the stream at or after the trigger''s version that still has a body and that the perspective reads (the types its association rows name, every type when it has none, and always the trigger itself); NULL when there is none, and then the replay cannot happen.';

SELECT __SCHEMA__.drop_all_overloads('_wh_repair_rewinds_from_unread_events');
DELETE FROM __SCHEMA__.wh_settings WHERE setting_key = 'rewind_read_floor_repaired';
SELECT __SCHEMA__.drop_all_overloads('wh_remove_unregistered_perspective_cursors');
