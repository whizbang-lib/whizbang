-- Migration: 158_ClaimReofferBoundedByBatch.sql
-- Date: 2026-09-16
-- Description: One claim poll costs what the batch it returns costs, never what the instance holds.
--
--              With acquisition bounded (157), one claim poll on a busy instance still touched
--              thousands of blocks to return a batch of a hundred rows. A busy instance holds as
--              many leased rows as its budget allows and polls several times a second to re-offer
--              the streams it holds, and every part of the poll priced itself by the holdings: the
--              orphan guards examined every pending row to prove none was unowned or expired; the
--              outbox re-offer ranked every held row with a window function whose result it never
--              used; the inbox re-offer ranked every held row with three window functions, fetching
--              every held row's heap page to do it; the perspective re-offer aggregated every held
--              event; and the inbox event-store chain, which is how an inbox event leased at store
--              time gets its event-store row and its perspective work, re-checked every held event
--              against the event store on every poll, twice, when all but the newest had been
--              chained long ago.
--
--              Measured on a container, one steady-state poll of an instance re-offering a batch of
--              100 per category, returning 300 rows, with payloads wide enough that no row is
--              updated in place. Blocks are heap and index, hit or read, per table and its indexes;
--              tuples are index entries read plus sequential tuples read; both are the sum over the
--              outbox, inbox, perspective-event and event-store tables:
--
--                held rows per table |      before      |     after
--                                    | blocks   tuples  | blocks  tuples
--                        5,000       |  5,425   35,012  |    700     305
--                       10,000       | 10,705   70,012  |    912     305
--                       40,000       | 42,388  280,012  |    925     304
--
--              Eight times the holdings cost eight times as much before and 1.3 times as much
--              after, and the tuples one poll examines no longer depend on the holdings at all.
--              Per row returned that is 18, 36 and 141 blocks before against 2.3, 3.0 and 3.1 after.
--
--              wh_inbox.chain_emitted_at records that the chain is finished with a row: its event is
--              in the event store. The chain reads only unstamped rows, through
--              idx_inbox_chain_pending, and stamps the ones whose event it finds there, so a row
--              whose insert conflicted stays unstamped and the next poll re-attempts it. A row
--              deployed before this column is stamped by the first poll that sees it, and the scan
--              converges to the rows stored since the last poll.
--
--              idx_outbox_held_arrival: the outbox re-offer walks the holder's pending singles in
--              arrival order and stops at the batch; every column it filters or returns is in the
--              index, so nothing beyond the batch is fetched.
--
--              idx_inbox_held_lanes and idx_perspective_held_lanes: the inbox and perspective
--              re-offers enumerate the streams a holder has one index probe per stream, lane by
--              lane in the order the batch is ordered (bucket, and for the inbox kind as well),
--              each lane's walk starting at a stream id drawn per poll and wrapping once, and stop
--              at the batch. One step lands on one stream, so a lane costs the streams it returns.
--
--              idx_outbox_lease_expiry, idx_inbox_lease_expiry, idx_perspective_lease_expiry: the
--              orphan guards in claim_work probe twice, unowned rows through the outstanding-by-
--              instance indexes (123) and expired leases at the head of these, so an instance whose
--              leases are all live proves it at the first entry.
--
-- Dependencies: 123 (outstanding-by-instance indexes), 149 (priority columns), 150 (claim_work), 157 (bounded acquisition)
-- Objects: wh_inbox.chain_emitted_at, idx_inbox_chain_pending, idx_outbox_held_arrival, idx_inbox_held_lanes, idx_perspective_held_lanes, idx_outbox_lease_expiry, idx_inbox_lease_expiry, idx_perspective_lease_expiry

ALTER TABLE __SCHEMA__.wh_inbox ADD COLUMN IF NOT EXISTS chain_emitted_at TIMESTAMPTZ;
COMMENT ON COLUMN __SCHEMA__.wh_inbox.chain_emitted_at IS
  'When the inbox event-store chain last confirmed this row''s event is in the event store (158). NULL means '
  'the chain is not finished with the row. The claim poll reads only unstamped rows, so a held row is checked '
  'against the event store once instead of on every poll.';

CREATE INDEX IF NOT EXISTS idx_inbox_chain_pending
  ON __SCHEMA__.wh_inbox (instance_id)
  WHERE processed_at IS NULL AND is_event = TRUE AND stream_id IS NOT NULL AND chain_emitted_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_inbox_chain_pending IS
  'Pending inbox events the event-store chain has not read yet, by holder (158). Empty for a holder in steady '
  'state, so the claim poll skips the chain without touching a held row.';

CREATE INDEX IF NOT EXISTS idx_outbox_held_arrival
  ON __SCHEMA__.wh_outbox (instance_id, created_at, message_id)
  INCLUDE (stream_id, partition_number, status, attempts, lease_expiry, scheduled_for, published_at)
  WHERE processed_at IS NULL AND coalesce_group IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_outbox_held_arrival IS
  'Covering partial index over pending outbox singles by holder and arrival (158), so claim_work re-offers the '
  'rows an instance holds by walking to its batch and stopping, instead of ranking every held row per poll.';

-- The lane of a pending inbox row: its holder, its priority bucket (150), its kind (command or event,
-- 145) and whether it has been tried (126), then the stream and the row's place in the stream.
-- claim_work walks a lane one index-only probe per stream: the first entry of a stream in a lane is
-- that stream's oldest held row there, and the next stream is one probe away. Every column the walk
-- returns is in the index, so the walk never reaches the heap, which is what the previous shape did
-- once per held row. The bucket expression is the one claim_work writes, so the planner matches it;
-- keep the two identical.
CREATE INDEX IF NOT EXISTS idx_inbox_held_lanes
  ON __SCHEMA__.wh_inbox (
    instance_id,
    (CASE WHEN priority <= 99 THEN 0 WHEN priority <= 199 THEN 1 ELSE 2 END),
    is_event,
    (attempts = 0),
    stream_id,
    received_at,
    message_id)
  INCLUDE (priority, attempts, partition_number, status, lease_expiry)
  WHERE processed_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_inbox_held_lanes IS
  'Pending inbox rows by holder, priority bucket, kind, tried-or-fresh, stream and arrival (158). claim_work '
  'enumerates the streams an instance holds one index-only probe per stream through this index, lane by lane '
  'in batch order, and re-offers a batch of them instead of ranking every held row per poll.';

-- The lane of a pending perspective event: its holder and its priority bucket (150), then the stream and
-- the event. claim_work walks a bucket one probe per stream; a stream's first entry is its oldest held
-- event in the bucket. The bucket expression is the one claim_work writes; keep the two identical.
CREATE INDEX IF NOT EXISTS idx_perspective_held_lanes
  ON __SCHEMA__.wh_perspective_events (
    instance_id,
    (CASE WHEN priority <= 99 THEN 0 WHEN priority <= 199 THEN 1 ELSE 2 END),
    stream_id,
    event_id)
  INCLUDE (lease_expiry)
  WHERE processed_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_perspective_held_lanes IS
  'Pending perspective events by holder, priority bucket, stream and event (158). claim_work enumerates the '
  'streams an instance holds one probe per stream through this index, most urgent bucket first, oldest event '
  'first, and re-offers a batch of them instead of aggregating every held event per poll.';

-- The orphan guards in claim_work ask whether anything is orphaned before calling an acquisition. A single
-- predicate over "unowned or expired" examines every pending row to prove there is none, which on a busy
-- instance is its whole holdings on every poll. The guards now probe twice: unowned rows under
-- instance_id IS NULL through the outstanding-by-instance indexes (123), and expired leases at the head of
-- these. Each holds only leased pending rows, so it stays small and its head is the oldest live lease.
CREATE INDEX IF NOT EXISTS idx_outbox_lease_expiry
  ON __SCHEMA__.wh_outbox (lease_expiry)
  WHERE processed_at IS NULL AND coalesce_group IS NULL AND instance_id IS NOT NULL;
COMMENT ON INDEX __SCHEMA__.idx_outbox_lease_expiry IS
  'Leased pending outbox singles by lease expiry (158), so claim_work''s orphan guard finds an expired lease at '
  'the index head and proves there is none without walking the holdings.';

CREATE INDEX IF NOT EXISTS idx_inbox_lease_expiry
  ON __SCHEMA__.wh_inbox (lease_expiry)
  WHERE processed_at IS NULL AND instance_id IS NOT NULL;
COMMENT ON INDEX __SCHEMA__.idx_inbox_lease_expiry IS
  'Leased pending inbox rows by lease expiry (158), so claim_work''s orphan guard finds an expired lease at '
  'the index head and proves there is none without walking the holdings.';

CREATE INDEX IF NOT EXISTS idx_perspective_lease_expiry
  ON __SCHEMA__.wh_perspective_events (lease_expiry)
  WHERE processed_at IS NULL AND instance_id IS NOT NULL;
COMMENT ON INDEX __SCHEMA__.idx_perspective_lease_expiry IS
  'Leased pending perspective events by lease expiry (158), so claim_work''s orphan guard finds an expired lease '
  'at the index head and proves there is none without walking the holdings.';
