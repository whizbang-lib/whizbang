-- ============================================================================
-- Migration 042: purge_orphan_inbox
-- ----------------------------------------------------------------------------
-- Slice 3 of resilient-transport-and-deserialization. Provides a SQL function
-- the host can call (at startup and on a maintenance cadence) to delete inbox
-- rows whose message_type isn't handled by any local receptor or perspective.
--
-- Slice 2 prevents NEW orphan rows from being stored at receive time; this
-- function cleans up rows that already accumulated before the deploy AND any
-- that slip through (e.g., handler removed in a service update while old
-- messages remain in flight).
--
-- Caller responsibility: pass the union of locally-handled CLR type names
-- (compiled from the receptor + perspective registries). Empty array = drop
-- nothing (safe no-op).
--
-- Filter excludes leased rows (instance_id IS NOT NULL) to avoid stomping
-- on actively-dispatching work — an in-flight handler will complete or fail
-- normally; if the message turns out to be orphaned, the next janitor run
-- after the lease expires picks it up.
-- ============================================================================

CREATE OR REPLACE FUNCTION __SCHEMA__.purge_orphan_inbox(p_handled_types TEXT[])
RETURNS TABLE(message_id UUID, message_type TEXT, handler_name TEXT) AS $$
BEGIN
  IF p_handled_types IS NULL OR array_length(p_handled_types, 1) IS NULL THEN
    -- No types handed in: do nothing. Caller has nothing to filter against,
    -- and we don't want to accidentally truncate the inbox if the registry
    -- is empty during cold start.
    RETURN;
  END IF;

  RETURN QUERY
  DELETE FROM __SCHEMA__.wh_inbox AS i
  WHERE i.message_type <> ALL(p_handled_types)
    AND i.processed_at IS NULL
    AND i.instance_id IS NULL
  RETURNING i.message_id, i.message_type, i.handler_name;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.purge_orphan_inbox(TEXT[]) IS
  'Deletes wh_inbox rows whose message_type is not in the provided list of locally-handled types. Skips leased rows. Returns the deleted (message_id, message_type, handler_name) tuples for logging.';
