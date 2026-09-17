-- Migration: 160_OutboxStreamEmptinessProbeIndex.sql
-- Date: 2026-09-17
-- Description: The store's outbox emptiness probe answers from an index instead of scanning.
--
--              Before its first insert for a stream, store_outbox_messages (149) asks whether that
--              stream has a pending outbox row, so it knows whether the row it is about to write
--              needs a doorbell:
--
--                PERFORM 1 FROM __SCHEMA__.wh_outbox o
--                  WHERE o.stream_id = v_msg.stream_id
--                    AND o.processed_at IS NULL
--                    AND o.published_at IS NULL
--                    AND (o.scheduled_for IS NULL OR o.scheduled_for <= p_now)
--                  LIMIT 1 FOR SHARE OF o SKIP LOCKED;
--
--              Nothing served that predicate. Every stream_id index on wh_outbox is partial on a
--              condition it does not satisfy: idx_outbox_stream_pending (031) on (status & 4) <> 4,
--              which a planner cannot prove from "processed_at IS NULL"; idx_outbox_stream_blocked
--              and idx_outbox_scheduled_for (both) on scheduled_for IS NOT NULL, which an ordinary
--              message does not have. So the probe scanned, and the answer it wants most often --
--              "this stream has nothing pending" -- is the case that scans to exhaustion.
--
--              This is the defect the doorbell had (141), one function over. Measured on a deployed
--              fleet it was 582 blocks a call over about twenty thousand calls, roughly a third of
--              what remained of that database's block reads after the doorbell was fixed.
--              Reproduced on a container, ten store calls of four messages each against 200 streams
--              holding 100 pending unpublished rows apiece:
--
--                                                      before        after
--                tuples read per store call            80,078          ~0
--                tuples read per row written          20,019.5         ~0
--                sequential scans per store call            4           0
--                blocks, the probe alone, per call         647           4
--                blocks, the whole store call              2,819       257
--
--              A partial index on the predicate's own terms is all it needs. stream_id is the key,
--              so a fresh stream is one descent and no rows; the partial clause keeps only pending
--              unpublished rows, so the index stays a fraction of the table and does not grow with
--              settled history. scheduled_for rides in INCLUDE rather than the key: it is a
--              disjunction and cannot be an index condition, but carried in the index the filter is
--              satisfied without a heap fetch, which is the difference between four blocks a call
--              and a few more.
--
--              The probe itself is unchanged, FOR SHARE OF ... SKIP LOCKED included. That clause
--              serializes against an in-flight completion of a stream's last pending row and is the
--              MVCC lost-wakeup guard 114 and 140 exist for; this migration only gives it an index
--              to answer from.
--
--              The other two probes in the same loop already have one and are left alone:
--              wh_perspective_events is served by idx_perspective_event_order (stream_id,
--              perspective_name, event_id) and wh_inbox by idx_inbox_pending_stream_order (138),
--              both measured at three buffers. wh_outbox was the only one of the three where every
--              candidate was partial on something its predicate could not satisfy.
--
-- Dependencies: 031 (idx_outbox_stream_pending, the partial index that cannot serve this), 149 (store_outbox_messages)
-- Objects: idx_outbox_stream_unpublished

CREATE INDEX IF NOT EXISTS idx_outbox_stream_unpublished
  ON __SCHEMA__.wh_outbox (stream_id)
  INCLUDE (scheduled_for)
  WHERE processed_at IS NULL AND published_at IS NULL;
COMMENT ON INDEX __SCHEMA__.idx_outbox_stream_unpublished IS
  'Pending unpublished outbox rows by stream (160), so the store''s emptiness probe answers "has this '
  'stream anything pending" with one index descent. Without it the probe matched no index and scanned '
  'every unpublished row to prove a fresh stream empty, which is the answer it most often wants. '
  'scheduled_for is included rather than keyed: the predicate is a disjunction and cannot be an index '
  'condition, but carrying it keeps the filter off the heap.';
