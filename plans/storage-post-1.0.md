# WS-7: storage after 1.0

Part of [storage-reclamation.md](storage-reclamation.md). Release: 2.0. Depends on WS-2 (slim
metadata, superseding types) and WS-3 (end-of-life policies, `IArchiveStore` with read-through).

## Context

The 1.0 workstreams stop writing bytes nobody reads, compress what the server can, and give
streams an end of life. What remains after them is the cost of bodies that must be kept: large
payloads (events carrying ~24 KB embedding vectors, full document snapshots), copies of another
service's events held for integrity sync, identical payloads stored many times, and small JSON rows
that lz4 barely shrinks (about 1.3x where a trained dictionary would reach 3 to 5x). Each answer
below changes the storage format or the read path, which is why they wait for 2.0.

## Goal

Keep the hot database proportional to live state: large and cold bodies leave the row store,
duplicate bodies are stored once, and retention by age becomes a partition drop.

## Slices

Each item is a sequence of slices; the first slice of each is a docs-first proposal page, then
red/green increments. Config keys are proposed names, all off by default.

### 1. A4: blob claim-check for event bodies

- **What changes.** The wire already has a claim-check: `IMessageBodyStore`
  (`src/Whizbang.Core/Offloads/IMessageBodyStore.cs:30`) uploads an oversize body and sends a
  `MessageBodyClaim` (`MessageBodyClaim.cs:23`) instead. Apply the same to stored bodies:
  `wh_event_body.event_data` holds a claim above a size threshold (or past an age), and every reader
  resolves it: event store reads (`DapperPostgresEventStore.cs`, `EFCoreEventStore.cs`), the
  perspective runner, and redelivery (`SelectRedeliveryEventsAsync`,
  `src/Whizbang.Core/Messaging/IWorkCoordinator.cs:918`). The emit chain builds bodies server-side
  from the outbox envelope (`149_MessagePriority.sql:546`), so offload happens either before the
  outbox (the wire claim travels into the store unchanged) or later in a maintenance task.
- **Slices.** (a) Readers resolve a stored claim, with a content-hash check. (b) A maintenance task
  offloads bodies past threshold and age, batched, with `rows_affected`. (c) Deleting a pointer
  (reapers, purge, close) deletes or releases its blob; this extends the existing
  `Whizbang:BodyOffload:ActiveCleanup` behavior.
- **RED first.** Read of a claimed body equals the original; a hash mismatch fails loudly; a
  missing blob surfaces as a typed error, never as an empty payload; reaping a claimed event
  removes its blob; a body under the threshold is never offloaded.
- **Docs.** `v1.0.0/fundamentals/offloads/message-body-store.md` (stored bodies); new
  `proposals/event-body-claim-check.md`.
- **Config.** `Whizbang:EventStore:BodyClaimCheck:ThresholdBytes`
  (`Whizbang__EventStore__BodyClaimCheck__ThresholdBytes`, default `0` = off),
  `...:MinAgeDays` (default `0`), `...:Provider` (default: the registered offload provider).

### 2. A5: foreign-copy pointers

- **What changes.** A service that receives another service's events stores a full copy so
  integrity sync can compare and rebuild locally. In 1.0 that stays (decision 7). In 2.0 a
  subscriber may store a pointer (origin service, event id) and resolve the body through the
  origin's archive read-through (WS-3, B3) on demand. Digests are computed over event ids, not
  bodies, so integrity comparison survives; a subscriber's perspective rebuild then depends on the
  archive being reachable.
- **RED first.** Pointer copy folds into the same digest as a full copy; a rebuild resolves bodies
  through the archive; an unreachable archive fails the rebuild with a typed error and leaves
  cursors where they were.
- **Docs.** New `proposals/foreign-copy-pointers.md`; `v1.0.0/resilience/stream-integrity.md`.
- **Config.** `Whizbang:EventStore:ForeignCopies:Mode` (`Whizbang__EventStore__ForeignCopies__Mode`,
  default `Full`; values `Full`, `Pointer`).

### 3. Event-store partitioning by `event_id` range

- **What changes.** Declarative range partitioning on `event_id`. TrackedGuid ids are time-ordered,
  so the primary key stays `event_id`, id lookups prune to one partition, and the cursor foreign key
  (`fk_perspective_cursors_event`) still targets the primary key. Retention becomes
  `DETACH`/`DROP PARTITION` with no dead tuples. `wh_event_body` partitions on the same ranges. The
  maintenance worker creates partitions ahead and drops them behind, as WS-5 does for ledgers.
- **Hard constraint found while planning.** Every unique index on a partitioned table must include
  the partition key. `wh_event_store` has two unique indexes without it: `idx_event_store_stream`
  (`stream_id, version`) and `idx_event_store_aggregate` (`aggregate_id, version`)
  (`src/Whizbang.Data.Schema/Schemas/EventStoreSchema.cs:127-136`). The emit chain's
  `ON CONFLICT DO NOTHING` relies on the first (`149_MessagePriority.sql:577-581`), and
  the direct append path uses its violation as optimistic concurrency, retrying on 23505
  (`DapperPostgresEventStore.cs:319`). Partitioning therefore needs another source of per-stream
  version uniqueness: the per-stream advisory lock the emit chain already takes (149:500) extended
  to the direct append path, or a separate unpartitioned `(stream_id, version)` index table.
- **Retention is per stream, not per month.** A partition can drop only after every live stream
  with events in it has been archived or compacted, so this lands with B1 and B3, not alone.
- **RED first.** Version uniqueness holds under concurrent appends to one stream across a partition
  boundary; an id lookup touches one partition (`EXPLAIN`); a partition holding a live stream's
  unarchived events refuses to drop; non-time-ordered ids land in a default partition and are
  reported.
- **Docs.** New `proposals/event-store-partitioning.md`. Correct
  `v1.0.0/data/event-store.md#event-store-performance`, whose aggregate-type and `created_at`
  examples would break the same unique indexes.
- **Config.** `Whizbang:EventStore:Partitioning:Mode` (`Whizbang__EventStore__Partitioning__Mode`,
  default `Off`), `...:Span` (default `30.00:00:00`), `...:CreateAhead` (default `2`).

### 4. K2: content-addressed body dedupe

- **What changes.** Store each distinct payload once, keyed by its hash, and have `wh_event_body`
  reference it. Fits payloads re-emitted unchanged (full-state permission snapshots, repeated
  document snapshots). Costs: hashing on write, reference counting so a reaper deletes a payload
  only when its last reference goes, and an interaction with per-subject encryption (crypto-shred
  makes identical payloads differ, which is correct). Dedupe stays within a tenant so sharing a
  payload never becomes a cross-tenant signal.
- **RED first.** Two identical payloads in one tenant store one payload row; across tenants, two;
  reaping one reference keeps the payload, reaping the last removes it; integrity digests are
  unchanged by dedupe.
- **Docs.** New `proposals/content-addressed-bodies.md`.
- **Config.** `Whizbang:EventStore:Dedupe:Enabled` (`Whizbang__EventStore__Dedupe__Enabled`,
  default `false`), `...:MinBytes` (default `1024`).

### 5. K3: stream packs with zstd trained dictionaries

- **What changes.** Cold stream prefixes (closed, idle, or past an age) are packed: many event
  payloads in one row, compressed with zstd using a dictionary trained per event type from samples.
  Dictionaries live in `wh_compression_dictionaries` (id, event type, created, bytes) and are never
  deleted while a pack references them.
- **Feasibility (from the A6 analysis).**
  - **Payload only.** No SQL reads payload contents; SQL only extracts the payload from the
    envelope at emit time (`149_MessagePriority.sql:545`).
  - **Metadata stays JSONB.** SQL and C# read `deliveryGuarantee` and `ephemeral_expires_at` from it
    in about ten places (digest folds, redelivery, the ephemeral reaper in `perform_maintenance`,
    `EFCoreWorkCoordinator.cs:1974`).
  - **Emit-chain constraint.** Bodies are built server-side from the outbox envelope, so compressing
    in the app before the outbox would break that extraction. Compression therefore happens during
    packing, on cold data only, where losing psql readability is acceptable.
  - **Readers decompress.** Event store reads, rebuilds and redelivery go through one unpack path.
  - **AOT.** The zstd binding must be AOT-safe (source-generated `LibraryImport`, no reflection).
- **RED first.** Pack then read returns byte-identical payloads in order; a pack referencing a
  dictionary keeps the dictionary alive; retraining adds a dictionary and leaves old packs readable;
  redelivery of a packed range equals redelivery before packing; digests unchanged.
- **Docs.** New `proposals/stream-packs.md`; link from `proposals/archival-compaction.md`.
- **Config.** `Whizbang:EventStore:Packs:Enabled` (`Whizbang__EventStore__Packs__Enabled`, default
  `false`), `...:MinAgeDays` (default `30`), `...:DictionarySamples` (default `1000`),
  `...:Level` (default `3`).

## Verification

- Per item: the RED tests above, 100% line and branch coverage on new code, and a before/after on a
  representative database: hot-store bytes per event, blob store bytes, payload rows per body
  (K2), compression ratio per event type (K3), and partition drop time against `DELETE` time.
- Rebuild a perspective from scratch over claimed, deduped, packed and pointer-copied bodies and
  compare the result with a rebuild over plain rows.

## Open questions

1. A4 trigger: size threshold, age, or both? And offload at write (claim travels from the wire) or
   only in maintenance?
2. A5: is a rebuild that depends on a reachable archive acceptable for a subscriber, or does
   `Pointer` mode require a local cache of recent bodies?
3. Partitioning: keep version uniqueness with the advisory lock on every append path, or with a
   side index table? Is a default partition for non-time-ordered ids acceptable?
4. K2 and K3 overlap on the same rows (a deduped payload inside a pack). Ship one first, and which?
5. K3: which zstd binding, and is a native dependency acceptable in the core data package or does
   packing live in its own package?
