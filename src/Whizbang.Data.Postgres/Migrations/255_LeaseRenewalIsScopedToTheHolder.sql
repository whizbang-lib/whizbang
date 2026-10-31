-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 255_LeaseRenewalIsScopedToTheHolder.sql
-- Date: 2026-10-28
-- Description: A lease renewal extends only the leases the renewing instance holds, and reports the rows it did
--              not renew (#1852).
--
--              renew_leases (029, last word 172) matched rows by id alone, in all three categories. An instance
--              whose lease on a row lapsed, and whose row another instance then claimed, still had the row in its
--              in-process set, and its next renewal extended the NEW holder's lease and stream lease. That kept
--              the row away from the instance actually working it for another lease, and both instances treated
--              it as theirs. Nothing told the former holder it had lost the row, so it kept renewing.
--
--              wh_renew_held_leases takes the renewing instance and renews a row only while that instance holds
--              it (instance_id = p_instance_id, unprocessed), with the stream leases of the streams those rows
--              are on and that the instance is assigned. It returns the ids it did not renew: a row another
--              instance holds now, a row that completed, or a row the store no longer has. The caller stops
--              holding those.
--
--              Shape, per category: one UPDATE over the caller's ids that are its own rows, returning the rows and
--              their streams, then one ledger UPDATE over those streams. That is one statement over the owned
--              rows and one over their streams; 172 additionally re-read the outbox rows to find their holders,
--              which the RETURNING list now carries. The ids not renewed are the caller's ids less the renewed
--              ones, computed from the two arrays without reading a table.
--
--              Locks: each category's rows, then wh_active_streams (the canonical table order, 162 and 172). The
--              outbox rows are taken in (stream_id, created_at, message_id) order as 172 takes them; the ledger
--              rows of every category in stream_id order (172's rule 2, which the inbox and perspective-event
--              arms of renew_leases did not follow). A row a claim re-leased after this statement's snapshot is
--              re-checked when its lock is granted, so it is not renewed (172's rule 4).
--
--              A new name rather than a new arity of renew_leases. renew_leases is defined unguarded at one arity
--              in 148, 162 and 172, which cannot be edited; a second arity would make each of them add an overload
--              beside it when replayed (OverloadGuardsCoverEveryDefinitionSiteTests). renew_leases itself is left
--              as 172 defined it, so an instance still running the previous release keeps renewing during a
--              rolling deployment; nothing in this release calls it.
--
-- Dependencies: 172 (renew_leases, the ordered outbox branch, copied with the deltas above), 162 (wh_inbox_state),
--               148 (the stream lease)
-- Objects: wh_renew_held_leases
-- Constants: the double-underscore tokens in this file (for example __CATEGORY_INBOX__) are substituted from
--            Migrations/constants.txt at apply time (README rule 12).

-- Exactly one overload per framework function (OverloadGuardsCoverEveryDefinitionSiteTests).
SELECT __SCHEMA__.drop_all_overloads('wh_renew_held_leases');

-- <docs>fundamentals/work-coordinator/batched-flushers</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Inbox_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Inbox_TheHolder_RenewsTheRowAndItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Outbox_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_Outbox_TheHolder_RenewsTheRowAndItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_PerspectiveEvent_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_PerspectiveEvent_TheHolder_RenewsTheRowAndItsStreamAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_ARowThatIsGoneOrCompleted_IsReportedNotRenewedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_NoIds_ReportsNothingAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HeldLeaseRenewalSqlTests.cs:RenewHeldLeases_AnUnknownCategory_IsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/WorkTablesAreLockedInOneOrderTests.cs:TheHeldLeaseRenewal_TakesEachCategoryBeforeTheLedgerAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_renew_held_leases(
  p_category TEXT,
  p_instance_id UUID,
  p_ids UUID[],
  p_lease_seconds INTEGER DEFAULT 300
) RETURNS UUID[] AS $$
DECLARE
  v_new_expiry TIMESTAMPTZ := NOW() + (p_lease_seconds || ' seconds')::INTERVAL;
  v_renewed UUID[];
  v_streams UUID[];
BEGIN
  IF p_ids IS NULL OR array_length(p_ids, 1) IS NULL THEN
    RETURN ARRAY[]::UUID[];
  END IF;

  CASE p_category
    WHEN __CATEGORY_OUTBOX__ THEN
      -- 172's order: outbox rows in (stream_id, created_at, message_id) order, the order every waiting writer of
      -- the outbox uses. The lock re-asserts the holder, so a row another instance claimed is not renewed.
      WITH renewed AS (
        UPDATE __SCHEMA__.wh_outbox o
          SET lease_expiry = v_new_expiry
          FROM (
            SELECT l.message_id
            FROM __SCHEMA__.wh_outbox l
            WHERE l.message_id = ANY(p_ids)
              AND l.instance_id = p_instance_id
              AND l.processed_at IS NULL
            ORDER BY l.stream_id, l.created_at, l.message_id
            FOR UPDATE OF l
          ) locked
          WHERE o.message_id = locked.message_id
          RETURNING o.message_id, o.stream_id
      )
      SELECT array_agg(r.message_id), array_agg(r.stream_id) FILTER (WHERE r.stream_id IS NOT NULL)
        INTO v_renewed, v_streams
        FROM renewed r;
    WHEN __CATEGORY_INBOX__ THEN
      WITH renewed AS (
        UPDATE __SCHEMA__.wh_inbox_state s
          SET lease_expiry = v_new_expiry
          WHERE s.message_id = ANY(p_ids)
            AND s.instance_id = p_instance_id
            AND s.processed_at IS NULL
          RETURNING s.message_id, s.stream_id
      )
      SELECT array_agg(r.message_id), array_agg(r.stream_id) FILTER (WHERE r.stream_id IS NOT NULL)
        INTO v_renewed, v_streams
        FROM renewed r;
    WHEN 'perspective_event' THEN
      WITH renewed AS (
        UPDATE __SCHEMA__.wh_perspective_events pe
          SET lease_expiry = v_new_expiry
          WHERE pe.event_work_id = ANY(p_ids)
            AND pe.instance_id = p_instance_id
            AND pe.processed_at IS NULL
          RETURNING pe.event_work_id, pe.stream_id
      )
      SELECT array_agg(r.event_work_id), array_agg(r.stream_id) FILTER (WHERE r.stream_id IS NOT NULL)
        INTO v_renewed, v_streams
        FROM renewed r;
    ELSE
      RAISE EXCEPTION 'wh_renew_held_leases: unknown category %', p_category
        USING HINT = 'Valid categories: outbox, inbox, perspective_event';
  END CASE;

  -- The stream leases of the renewed rows' streams that this instance is assigned (148), in stream_id order (172).
  IF v_streams IS NOT NULL THEN
    UPDATE __SCHEMA__.wh_active_streams ast
      SET lease_expiry = v_new_expiry
      FROM (
        SELECT a.stream_id
        FROM __SCHEMA__.wh_active_streams a
        WHERE a.stream_id = ANY(v_streams)
          AND a.assigned_instance_id = p_instance_id
        ORDER BY a.stream_id
        FOR UPDATE OF a
      ) locked
      WHERE ast.stream_id = locked.stream_id;
  END IF;

  -- The caller's ids this call did not renew, from the two arrays alone.
  RETURN ARRAY(
    SELECT asked.id FROM unnest(p_ids) AS asked(id)
    EXCEPT
    SELECT renewed.id FROM unnest(v_renewed) AS renewed(id)
  );
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_renew_held_leases(TEXT, UUID, UUID[], INTEGER) IS
  'Batched lease extension per category for the supplied ids that p_instance_id holds and that are not yet processed, '
  'with the stream leases of their streams that it is assigned (148). Returns the ids it did not renew (another '
  'instance holds the row, it completed, or it is gone), which the caller stops holding (#1852). Outbox rows are '
  'locked in (stream_id, created_at, message_id) order and ledger rows in stream_id order (172).';
