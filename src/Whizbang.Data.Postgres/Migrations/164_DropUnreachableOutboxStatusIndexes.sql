-- Migration: 164_DropUnreachableOutboxStatusIndexes.sql
-- Date: 2026-09-19
-- Description: Drops the four outbox indexes that are partial on a status bitmask. Nothing can
--              reach them, and every outbox write was paying for them.
--
--              The outbox once discriminated claimable and completed rows with bits in status; it
--              uses processed_at now. A partial index is considered only when Postgres can prove the
--              query's predicate implies the index's, and that proof is TEXTUAL: "processed_at IS
--              NULL" says nothing about "(status & 4) <> 4". So each of these is unreachable by
--              construction -- not merely unused on current data, but impossible to choose for any
--              query the outbox issues, whatever the data looks like.
--
--              Unreachable is not free. A partial index is still maintained on every insert, update
--              and delete whose row satisfies its predicate, and on the producer side of a bulk load
--              the outbox is the hottest write path in the system. A deployed fleet carried all four
--              at zero scans through an entire bulk import while the table's other fifteen indexes
--              did the work.
--
--              160 already found this for one of them. It named "idx_outbox_stream_pending (031) on
--              (status & 4) <> 4, which a planner cannot prove from processed_at IS NULL" as the
--              reason the stream-emptiness probe scanned, and then added
--              idx_outbox_stream_unpublished beside it rather than removing it. That replacement
--              carries the traffic; this one has been dead weight since.
--
--              Each index is ALSO removed from the place that creates it, or the descriptor pass --
--              which runs before migrations on every boot -- would put it straight back:
--                * idx_outbox_status_lease, idx_outbox_failure_reason, idx_outbox_partition_claiming
--                  came from the Whizbang.Data.Schema outbox descriptor (and two of them from the
--                  EFCore generator's CoreInfrastructureSchema.sql).
--                * idx_outbox_stream_pending came from migration 031.
--              Dropping without that would be undone silently on the next start.
--
--              The Sqlite schema still declares two of these names. It is left alone deliberately:
--              that driver's own queries have not been measured here, and an index that is dead
--              against one engine's query set is not automatically dead against another's.
--
-- Dependencies: 031 (created idx_outbox_stream_pending), 160 (idx_outbox_stream_unpublished, the
--               replacement that carries the traffic)
-- Objects: idx_outbox_status_lease, idx_outbox_failure_reason, idx_outbox_partition_claiming,
--          idx_outbox_stream_pending

DROP INDEX IF EXISTS __SCHEMA__.idx_outbox_status_lease;
DROP INDEX IF EXISTS __SCHEMA__.idx_outbox_failure_reason;
DROP INDEX IF EXISTS __SCHEMA__.idx_outbox_partition_claiming;
DROP INDEX IF EXISTS __SCHEMA__.idx_outbox_stream_pending;
