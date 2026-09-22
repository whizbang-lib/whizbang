-- Migration: 168_AdvisoryLedger.sql
-- Date: 2026-09-22
-- Description: Advisory findings are remembered in the database, so a finding is raised once for
--              the whole service instead of once per replica per restart.
--
--              The index advisory decides that a table has grown large enough for a missing index
--              to cost real time, and says so. It remembered what it had already said in process
--              memory, which is sound for one long-lived instance and wrong for everything else.
--              Every replica reaches the same conclusion about the same table independently, so
--              the advice arrives once per pod; and a restart clears the memory, so a deployment
--              that restarts on a schedule receives the same advice on that schedule forever. The
--              failure mode is not a storm, it is that the advice stops being read, which costs
--              exactly as much as never having emitted it.
--
--              This is the same problem the integrity ledger (090) was built for and the same
--              shape of answer: the primary key IS the identity of a finding, so "have we already
--              said this" is a primary-key lookup that survives a restart and is shared by every
--              replica.
--
--              Two things make a finding worth raising again. Advice that has CHANGED is new
--              advice -- another field is now exposed to a query, or one has been indexed since --
--              and goes out immediately, which is why the signature is stored rather than only the
--              key. And advice nobody acted on comes back once the cooldown elapses, because a
--              finding suppressed forever cannot be told apart from a finding that was fixed, and
--              the table is still being scanned either way.
--
--              Deliberately not bounded by a reaper, unlike 090. That ledger keys on a stream, so
--              a pathological divergent set grows without limit. This one keys on a model and its
--              table, so the row count is a property of the deployed schema: dozens, not millions.
--              A row for a model that no longer exists is a few bytes and answers a real question
--              (it was advised about once), so reclaiming it would cost more to run than to keep.
--              last_touched is recorded for the operator reading this table, not for a reaper, and
--              carries no index for the same reason.
--
-- Dependencies: 000 (schema)

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_advisory_ledger (
  -- What the finding is about: stable across processes and restarts by construction, because a
  -- runtime handle to a type would not be.
  finding_key       TEXT        NOT NULL PRIMARY KEY,
  -- What the finding said when it was last reported. A change is new advice, not a repeat.
  signature         TEXT        NOT NULL,
  -- When this finding was FIRST raised, never updated afterwards: how long it has gone unaddressed
  -- is the question an operator acts on, and last_reported_at cannot answer it.
  first_seen_at     TIMESTAMPTZ NOT NULL,
  last_reported_at  TIMESTAMPTZ NOT NULL,
  -- How many times it has actually been reported, so a finding that keeps coming back is visible
  -- as such rather than looking like a first sighting every time.
  report_count      INTEGER     NOT NULL DEFAULT 1,
  -- When it was last DETECTED, reported or not. The gap between this and last_reported_at is the
  -- suppression working.
  last_touched      TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE __SCHEMA__.wh_advisory_ledger IS
'One row per advisory finding. The primary key is the identity of a finding, so a repeat sighting updates a row instead of raising the advice again, across restarts and across replicas.';

-- True when this finding should be REPORTED now: first sighting, changed advice, or the cooldown
-- elapsed since it was last reported. Records the sighting either way.
--
-- The decision and the record are one statement on purpose. Two replicas reaching the same
-- conclusion in the same instant both arrive here; ON CONFLICT DO UPDATE takes the row lock, so
-- the second one's condition is evaluated against the first one's write and it is refused. Read
-- first and write second and they would both be granted, which is the bug this table exists to
-- fix, only harder to see.
--
-- <docs>operations/diagnostics/whiz306</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLedgerSqlTests.cs:AFirstSightingIsReportedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLedgerSqlTests.cs:TheSameAdviceWithinTheCooldownIsRefusedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLedgerSqlTests.cs:AdviceThatHasChangedIsReportedAtOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLedgerSqlTests.cs:TheSameAdviceAfterTheCooldownIsReportedAgainAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLedgerSqlTests.cs:ARefusedSightingStillRecordsThatItWasSeenAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLedgerSqlTests.cs:FirstSeenAtSurvivesEveryLaterReportAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_advisory_try_begin_report(
  p_finding_key TEXT,
  p_signature   TEXT,
  p_now         TIMESTAMPTZ,
  p_cooldown    INTERVAL
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_granted BOOLEAN;
BEGIN
  INSERT INTO __SCHEMA__.wh_advisory_ledger AS l
    (finding_key, signature, first_seen_at, last_reported_at, report_count, last_touched)
  VALUES
    (p_finding_key, p_signature, p_now, p_now, 1, p_now)
  ON CONFLICT (finding_key) DO UPDATE
    SET signature        = EXCLUDED.signature,
        last_reported_at = EXCLUDED.last_reported_at,
        report_count     = l.report_count + 1,
        last_touched     = EXCLUDED.last_touched
    -- first_seen_at is absent on purpose: it is the one column a later report must not move.
    WHERE l.signature IS DISTINCT FROM EXCLUDED.signature
       OR l.last_reported_at <= EXCLUDED.last_reported_at - p_cooldown
  RETURNING TRUE INTO v_granted;

  IF v_granted IS TRUE THEN
    RETURN TRUE;
  END IF;

  -- Refused, but it WAS seen, and the gap between the two timestamps is how an operator tells
  -- suppression from a finding that stopped being detected.
  UPDATE __SCHEMA__.wh_advisory_ledger
     SET last_touched = p_now
   WHERE finding_key = p_finding_key;

  RETURN FALSE;
END;
$$;
