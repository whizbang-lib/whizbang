-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 452_StaleCopiesAboveTheNewestAreRewalked.sql
-- Date: 2026-11-15
-- Description: The superseding history is walked again under the origin's order, so a stream already holding older
--              copies of a key above its newest one is repaired with no manual step (#2366).
--
--              Before 449, a redelivered older copy of a superseding key was stored at the end of a consumer's stream,
--              above the newest copy by local version, and nothing that ordered copies knew better. 449 orders them by
--              the origin's commit sequence and stops new stale copies at the store, but the copies already stored stay:
--              each superseding type's history walk had completed under the old rule. A perspective on such a stream
--              replays the same out-of-order input on every rewind, so the rewind never converges.
--
--              wh_restart_superseding_history_walks() restarts every completed superseding walk from its first event.
--              The superseding-history-reconcile maintenance step then walks each type again, paced and bounded as it
--              always is, under 449's rule: it stamps the key on any copy that lacks one and queues for the reaper every
--              copy a held copy supersedes in the origin's order, in both directions. The reaper removes them past its
--              usual gates. Called once here.
--
--              Number 452: reserved for this change.
--
-- Reversal: reversible (the function goes; a walk it restarted completes again and changes nothing)
-- Dependencies: 380 (wh_type_history_walks), 449 (_wh_supersedes in the superseding walk)
-- Objects: wh_restart_superseding_history_walks
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- Restarts every completed superseding history walk from its first event. Returns the walks restarted.
-- <docs>fundamentals/events/superseding-events#stale-copies-already-stored</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Repair_AStreamHoldingOlderCopiesAboveItsNewest_QueuesTheCopies_OnceTheWalkRestartsAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_restart_superseding_history_walks() RETURNS INTEGER AS $$
DECLARE
  v_restarted INTEGER;
BEGIN
  UPDATE __SCHEMA__.wh_type_history_walks h SET
    last_commit_sequence = NULL,
    events_total = (SELECT count(*) FROM __SCHEMA__.wh_event_store es WHERE es.event_type = h.event_type),
    events_matched = 0,
    events_changed = 0,
    events_queued = 0,
    events_blocked = 0,
    started_at = NOW(),
    completed_at = NULL,
    updated_at = NOW()
  WHERE h.operation = 'superseding'
    AND h.completed_at IS NOT NULL;
  GET DIAGNOSTICS v_restarted = ROW_COUNT;
  RETURN v_restarted;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_restart_superseding_history_walks() IS
'Restarts every completed superseding history walk from its first event, so the walk applies the origin-ordered rule of 449 to the copies already stored (#2366). Returns the walks restarted.';

SELECT __SCHEMA__.wh_restart_superseding_history_walks();
