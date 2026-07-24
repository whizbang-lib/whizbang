# PerspectiveWorker stream-affinity — the missing slice

> **Status:** confirmed gap, 2026-06-23. Fix is the natural extension of the
> existing `PerStreamSerializer` (already used by `ServiceBusConsumerWorker`,
> "slice 2 of plans/stream-affinity-everywhere.md").

## The architectural invariant we want

For any given `stream_id` X, **at any moment, at most one process anywhere
in the cluster is applying perspective writes for X.**

This is the load-bearing invariant downstream of every "monotone Apply"
projection (`SagaItemProjection`, `BulkOperationItem`, every per-item read
model). If two threads anywhere apply different events for X concurrently
to potentially-stale loaded state, the second writer's data overwrites the
first — even when the second writer's event is logically earlier. That is
the cross-pod stale-read race that stranded saga `019ee73d` on 2026-06-20
and saga `019ef473` on 2026-06-23, and is what `tests/Whizbang.Data.EFCore.Postgres.Tests/CrossPodStaleReadRegressionRaceTests.cs`
locks as RED.

The invariant has two halves, each enforced by a different mechanism:

| Scope | Mechanism | Status |
|---|---|---|
| **Cross-pod**: only one pod owns X at a time | `wh_active_streams` ownership row + lease | ✅ Implemented (mig 007) |
| **Intra-pod**: only one thread inside the owning pod applies for X at a time | `PerStreamSerializer<T>` one-channel-one-worker-per-stream | ❌ **Missing on `PerspectiveWorker`** — uses `Parallel.ForEachAsync` over grouped work instead |

## Why the intra-pod half matters

`Parallel.ForEachAsync` over `groupedWork` keyed by `(streamId,
perspectiveName)` guarantees that **within a single batch** one stream's
events go to one parallel task. That is correct for one batch in isolation.

But `PerspectiveWorker` runs **multiple consumer loops in parallel**
(`PerspectiveWorker.cs:328` — `consumers[i] = Task.Run(() =>
_runChannelConsumerLoopAsync(...))`). Each consumer loop independently:

1. Reads work items from a shared channel (`_perspectiveChannelWriter`)
2. Builds a `workBatch` (line 401)
3. Calls `ProcessChannelBatchAsync(workBatch, ...)` (line 436)

There is no per-stream affinity between consumer loops. If events for
stream X are interleaved with events for other streams in the channel,
consumer A's `TryRead` can pick up event 1 for X and consumer B's
`TryRead` can pick up event 2 for X. Both consumers then run their own
`ProcessChannelBatchAsync`, each grouping their own items by stream, each
running `Parallel.ForEachAsync`. Now **two threads are applying perspective
writes for stream X concurrently**, on possibly-stale loaded state — the
strand race.

This is the bug `PerspectiveWorker` has and `ServiceBusConsumerWorker`
doesn't.

## The fix

Wrap `PerspectiveWorker`'s perspective application in
`PerStreamSerializer<TWork>`. Each (streamId, perspectiveName) pair gets
one channel + one worker, identical to the `ServiceBusConsumerWorker`
pattern (`Workers/ServiceBusConsumerWorker.cs:62`).

### Implementation outline

- Add `private readonly PerStreamSerializer<...> _streamSerializer` field
  on `PerspectiveWorker`. Use `(streamId, perspectiveName)` as the key —
  the same group key the current `Parallel.ForEachAsync` uses.
- Replace `Parallel.ForEachAsync(groupedWork, ...)` with a fan-out that
  enqueues each group to `_streamSerializer.EnqueueAsync(...)`.
- The processor function is what currently lives inside the
  `Parallel.ForEachAsync` body (line 789+) — load events, run perspective,
  upsert row, advance cursor.
- Drain mode (line 774-780) goes through the same serializer so the
  per-stream invariant holds across both paths.

The shared `PerStreamSerializer` instance gates across consumer loops:
when consumer A is processing stream X, consumer B's enqueue for X waits
on X's per-stream channel — it doesn't spawn a parallel processor.

Cross-stream parallelism is preserved exactly as today (different
streams → different per-stream workers).

### What this does NOT change

- `Parallel.ForEachAsync`'s `MaxDegreeOfParallelism` semantics: now
  expressed by how many distinct streams are active at once, which is
  the more accurate notion of perspective parallelism anyway.
- `wh_active_streams` cross-pod pinning: still the source of truth for
  which pod owns the stream. The serializer only operates within a pod.
- Channel-based work delivery: the existing `_perspectiveChannelWriter`
  / `_perspectiveDrainChannel` shape is unchanged; the serializer sits
  on the *processing* side, not the *delivery* side.

## Test coverage

After the fix:

- `CrossPodStaleReadRegressionRaceTests.StaleSecondWriter_...` and
  `CrossPodStaleReadRegressionRaceTests.SlotThree_ThreeFiftyItemStrand_...`
  remain meaningful as **storage-layer** regression locks (they assert
  the storage doesn't independently protect against the race) but the
  *pipeline-level* race they reproduce no longer occurs in production
  because the per-stream serializer prevents it before the UPSERT.
- New `PerspectiveWorkerStreamAffinityTests` integration test (in
  `tests/Whizbang.Data.EFCore.Postgres.Tests/`) drives two consumer
  loops concurrently for the same stream and asserts they serialize.

## Out of scope for this slice

- Tightening the storage-layer UPSERT (the `BaseUpsertStrategy`
  WHERE-clause work) — the per-stream serializer is the structural fix;
  the storage-layer guard would be defense-in-depth and has a separate
  design conflict with the stamper-lag forwarding invariant
  (`PerspectiveApplyIdempotencyTests.RunWithEvents_MetadataHasCommitSequence_EnvelopeMissingCommitSequence_LexSmallerEventId_IsAppliedAsync`).
- Multi-instance reconcile of `wh_active_streams` — the existing claim /
  orphan-recovery paths stay as-is.
