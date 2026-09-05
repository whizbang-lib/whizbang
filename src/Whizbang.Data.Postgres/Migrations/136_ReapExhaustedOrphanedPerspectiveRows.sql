-- ============================================================================
-- 136_ReapExhaustedOrphanedPerspectiveRows.sql
-- ============================================================================
-- reap_exhausted_orphaned_perspective_rows(p_instance_id, p_stream_ids, p_max_attempts):
-- the REACTIVE half of the orphan defense (#679). When the perspective drainer fetches a
-- leased stream's events and the inner join returns NOTHING, the stream's rows may be
-- orphaned — their source event is absent from wh_event_store, so they can never project and
-- would re-claim forever (the "hard wedge"). This disposes such rows ON CONTACT, keyed on
-- ATTEMPTS rather than age: a row the drainer has already attempted p_max_attempts times with
-- no surviving event is unambiguously an orphan (a just-committed write resolves in a cycle or
-- two, never 10), so no age grace is needed here — attempts ARE the safety. Deleting is correct:
-- the event is gone, there is nothing to project, and the cursor never advanced past the row.
-- The age-bounded maintenance sweep (Task 12, #687) remains the janitorial backstop for orphans
-- that never accumulate enough attempts to be caught here.

SELECT __SCHEMA__.drop_all_overloads('reap_exhausted_orphaned_perspective_rows');

CREATE OR REPLACE FUNCTION __SCHEMA__.reap_exhausted_orphaned_perspective_rows(
  p_instance_id UUID,
  p_stream_ids  UUID[],
  p_max_attempts INTEGER
) RETURNS INTEGER AS $$
DECLARE
  v_count INTEGER;
BEGIN
  DELETE FROM __SCHEMA__.wh_perspective_events pe
  WHERE pe.processed_at IS NULL
    AND pe.stream_id = ANY(p_stream_ids)
    AND pe.instance_id = p_instance_id
    AND pe.attempts >= p_max_attempts
    AND NOT EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id = pe.event_id
    );
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.reap_exhausted_orphaned_perspective_rows IS
'Reactive orphan disposal (136, #679): deletes pending perspective-event rows for the given streams, leased to the calling instance, that have been attempted p_max_attempts+ times and whose source event is absent from wh_event_store. Attempts (not age) are the safety, so the drainer can dispose an orphan the moment it confirms the event is gone, instead of livelocking. Returns the number reaped.';
