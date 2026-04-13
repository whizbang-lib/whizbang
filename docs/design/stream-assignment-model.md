# Design: Stream Assignment Model for process_work_batch

## Status: PROPOSED — not yet implemented

## Problem

`process_work_batch` returns up to 300 work items with full `event_data::TEXT` envelopes every tick (~1 second). Most of these are messages the worker already received on a previous tick but hasn't finished processing yet. This creates:

- ~600KB+ of redundant data transfer from PostgreSQL per tick
- Serialization overhead on PostgreSQL for large TEXT columns
- GC pressure on the C# worker from repeated deserialization

## Proposed Model

### Current: message-level return (heavy)

```
SQL tick → returns 300 work items with full data
Worker → processes some, next tick gets same 300 + new ones
```

### Proposed: stream-level assignment (lightweight)

```
SQL tick → returns ~10-20 stream/perspective assignments (IDs only)
Worker → fetches unprocessed messages for assigned streams on demand
Worker → caches messages in memory, only fetches what's new
```

### Flow

1. **SQL tick** (lightweight):
   - Returns stream/perspective pairs assigned to this instance
   - Includes cursor state (last_event_id, status, rewind flags)
   - No message data, no envelopes
   - Payload: ~20 rows × 3 GUIDs = ~1KB

2. **Worker fetch** (on demand):
   - `SELECT * FROM wh_perspective_events WHERE stream_id = ANY(@streams) AND processed_at IS NULL`
   - Only fetches for streams it doesn't already have in cache
   - For outbox/inbox: `SELECT message_id, event_data FROM wh_outbox WHERE stream_id = ANY(@streams) AND processed_at IS NULL`

3. **Worker cache**:
   - Holds messages in memory keyed by (stream_id, message_id)
   - On fetch: new messages → add to cache, missing messages → evict
   - No ordering logic — just "give me what's pending"
   - Rewind system handles out-of-order events separately

### SQL governs, C# caches

- SQL decides which streams belong to which instance (wh_active_streams)
- If SQL reassigns a stream to another instance, the first instance stops seeing it in tick results → cache evicted
- C# cache is purely an optimization — never contradicts SQL ownership

## Benefits

| Metric | Current | Proposed |
|--------|---------|----------|
| Data per tick | ~600KB (300 items × 2KB) | ~1KB (20 stream assignments) |
| Redundant transfers | ~80% (re-sending unprocessed items) | ~0% (only new streams) |
| PostgreSQL serialization | 300 TEXT columns/tick | 20 UUID columns/tick |
| Round-trips | 1 (everything in process_work_batch) | 2 (tick + fetch), but fetch is targeted |

## Scope of change

This affects:
- `process_work_batch` return contract (Phase 7)
- `WorkBatchRow` / `WorkBatch` C# types
- `WorkCoordinatorPublisherWorker` (outbox processing)
- `PerspectiveWorker` (perspective processing)
- `TransportConsumerWorker` (inbox processing — already partially separated)
- `IWorkCoordinator` interface (new fetch methods)

## Dependencies

- Existing stream ownership via `wh_active_streams` (already works)
- Cursor system (already tracks per-stream state)
- Rewind/debounce (already handles out-of-order at stream level)

## Risk

- High: changes the fundamental work distribution contract
- Mitigated by: existing stream affinity model, existing tests
- Recommendation: implement for perspectives first (already closest to this model), then extend to outbox/inbox
