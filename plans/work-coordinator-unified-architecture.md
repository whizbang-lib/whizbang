# Work Coordinator Unified Architecture

**Created:** 2025-12-06
**Status:** Implementation in Progress (Phase 8 Complete, Testing Pending)

## Vision

All inbox and outbox operations flow through a **single PostgreSQL function** (`process_work_batch`) for atomic operations, while using **separate strategy instances** for different use cases (inbox vs outbox, different latency requirements).

## Core Principle

```
Everything → process_work_batch → Ordered Processing → Report Status → process_work_batch
```

## Architecture Diagram

```
┌──────────────────────────────────────────────────────────────┐
│              process_work_batch(...)                         │
│                                                               │
│  • Atomic inbox INSERT with deduplication (ON CONFLICT)      │
│  • Atomic outbox INSERT                                      │
│  • Partition-based work distribution (10k partitions)        │
│  • Lease management (prevent duplicate processing)           │
│  • Returns InboxWork[] + OutboxWork[] for processing         │
│  • Granular status tracking (bitwise flags)                  │
│  • Stream ordering via sequence_order                        │
└──────────────────────────────────────────────────────────────┘
         ▲                                    ▲
         │                                    │
    ┌────┴──────────┐                  ┌──────┴────────┐
    │ Inbox         │                  │ Outbox        │
    │ Strategy      │                  │ Strategy      │
    │ (Scoped)      │                  │ (Interval)    │
    └───────────────┘                  └───────────────┘
         ▲                                    ▲
         │                                    │
         │                                    │
┌────────┴─────────┐              ┌───────────┴────────────────┐
│ ServiceBus       │              │ WorkCoordinator            │
│ ConsumerWorker   │              │ PublisherWorker            │
│                  │              │                            │
│ • Receives msg   │              │ • Polls every 100ms        │
│ • Queues inbox   │              │ • Batches operations       │
│ • Flushes scope  │              │ • Publishes to transport   │
│ • Processes work │              │ • Reports completions      │
└──────────────────┘              └────────────────────────────┘
```

## Strategy Pattern

### Multiple Strategy Instances

Each use case gets its own strategy instance with appropriate configuration:

```csharp
// INBOX: Scoped strategy (per-message scope)
services.AddScoped<IWorkCoordinatorStrategy>(sp =>
  new ScopedWorkCoordinatorStrategy(
    sp.GetRequiredService<IWorkCoordinator>(),
    sp.GetRequiredService<IServiceInstanceProvider>(),
    new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Scoped,
      PartitionCount = 10_000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300
    }
  ));

// OUTBOX: Interval strategy (singleton background worker)
services.AddSingleton<IWorkCoordinatorStrategy>(sp =>
  new IntervalWorkCoordinatorStrategy(
    sp.GetRequiredService<IWorkCoordinator>(),
    sp.GetRequiredService<IServiceInstanceProvider>(),
    new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Interval,
      IntervalMilliseconds = 100,
      PartitionCount = 10_000,
      MaxPartitionsPerInstance = 100
    }
  ));

// DISPATCHER: Scoped strategy (per-request in APIs)
// Uses same registration as inbox (scoped lifetime)
```

### Strategy Types

1. **Immediate** - Flushes on every operation (lowest latency, highest DB load)
2. **Scoped** - Flushes on scope disposal (per-request batching)
3. **Interval** - Flushes on timer (highest throughput, batching)

## Component Integration

### 1. ServiceBusConsumerWorker (Inbox Processing)

**Flow:**
```
ServiceBus Message → Queue to Inbox Strategy → Flush (atomic dedup)
                                                   ↓
                                    Returns InboxWork[] (if not duplicate)
                                                   ↓
                              OrderedStreamProcessor (stream ordering)
                                                   ↓
                              Invoke Perspectives (business logic)
                                                   ↓
                              Queue Completions → Flush (update status)
```

**Key Code:**
```csharp
async Task HandleMessageAsync(IMessageEnvelope envelope, CancellationToken ct) {
  await using var scope = _scopeFactory.CreateAsyncScope();
  var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

  // 1. Serialize envelope to NewInboxMessage
  var newInboxMessage = new NewInboxMessage {
    MessageId = envelope.MessageId.Value,
    HandlerName = DetermineHandlerName(envelope),
    EventType = GetPayloadTypeName(envelope),
    EventData = SerializePayload(envelope),
    Metadata = SerializeMetadata(envelope),
    Scope = SerializeScope(envelope),
    StreamId = ExtractStreamId(envelope)
  };

  // 2. Queue for atomic deduplication
  strategy.QueueInboxMessage(newInboxMessage);

  // 3. Flush - calls process_work_batch with atomic INSERT ... ON CONFLICT DO NOTHING
  var workBatch = await strategy.FlushAsync(WorkBatchFlags.None, ct);

  // 4. If work returned, this message was NOT a duplicate
  var myWork = workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value).ToList();

  if (myWork.Count == 0) {
    _logger.LogInformation("Message {MessageId} already processed (duplicate)", envelope.MessageId);
    return; // Duplicate, already processed
  }

  // 5. Process using OrderedStreamProcessor (maintains stream ordering)
  await _orderedProcessor.ProcessInboxWorkAsync(
    myWork,
    processor: async (work) => {
      // Deserialize and invoke perspectives
      var @event = DeserializeEvent(work);
      await InvokePerspectivesAsync(@event, ct);
      return MessageProcessingStatus.ReceptorProcessed | MessageProcessingStatus.PerspectiveProcessed;
    },
    completionHandler: (msgId, status) => strategy.QueueInboxCompletion(msgId, status),
    failureHandler: (msgId, status, error) => strategy.QueueInboxFailure(msgId, status, error),
    ct
  );

  // 6. Report completions/failures back to database
  await strategy.FlushAsync(WorkBatchFlags.None, ct);

  // Scope disposal happens automatically
}
```

**Benefits:**
- ✅ Atomic deduplication (no race conditions)
- ✅ Stream-based ordering preserved
- ✅ Granular status tracking
- ✅ Per-message scope ensures cleanup

### 2. Dispatcher (Outbox Queueing)

**Flow:**
```
SendAsync/PublishAsync → Serialize Envelope → Queue to Outbox Strategy → Flush
                                                                           ↓
                                              Returns OutboxWork[] (immediate processing)
```

**Implemented:**
- ✅ Strategy pattern integration (IWorkCoordinatorStrategy required)
- ✅ Stream ID extraction from aggregate ID
- ✅ Envelope serialization helpers
- ✅ IOutbox dependency removed (Phase 6 Cleanup)

### 3. WorkCoordinatorPublisherWorker (Outbox Publishing)

**Flow:**
```
Timer (100ms) → Flush Interval Strategy → Returns OutboxWork[]
                                              ↓
                              OrderedStreamProcessor (stream ordering)
                                              ↓
                              Publish to Transport (Azure Service Bus)
                                              ↓
                              Queue Completions → Flush (update status)
```

**Already Implemented:**
- ✅ Uses granular status tracking
- ✅ Reports completions with MessageCompletion[]
- ✅ Reports failures with MessageFailure[]

### 4. EFCoreInbox & EFCoreOutbox

**Status:** ❌ DELETED (Phase 6 Cleanup)

**Rationale:** Direct ORM access violated the unified architecture principle. ALL inbox/outbox operations must go through `IWorkCoordinatorStrategy → IWorkCoordinator → process_work_batch`. No bypass paths allowed.

## Database Schema (process_work_batch)

### Key Columns Added

**inbox & outbox tables:**
```sql
stream_id UUID                    -- For stream-based ordering
partition_number INTEGER          -- Consistent hash of stream_id
status INTEGER                    -- Bitwise flags (MessageProcessingStatus)
flags INTEGER                     -- WorkBatchFlags (NewlyStored, Orphaned, etc.)
sequence_order BIGSERIAL          -- Per-stream ordering
instance_id UUID                  -- Lease owner
lease_expiry TIMESTAMPTZ          -- Lease timeout
```

### Virtual Partition Assignment (Hash-Based)

**Architecture**: No partition assignments table - purely algorithmic distribution using consistent hashing on UUIDv7 identifiers.

**How It Works**:

1. **Partition Computation** (per message):
   ```sql
   partition_number = abs(hashtext(stream_id::TEXT)) % partition_count
   ```
   - Same `stream_id` always maps to same partition number
   - Default: 10,000 partitions

2. **Instance Ownership** (per claim attempt):
   ```sql
   (hashtext(stream_id::TEXT) % active_instance_count) = (hashtext(instance_id::TEXT) % active_instance_count)
   ```
   - Both `stream_id` and `instance_id` are UUIDv7 (time-ordered)
   - Self-contained: depends only on UUID properties
   - Deterministic: same UUIDs always produce same result

3. **Instance Assignment Preservation**:
   - Each record stores `instance_id` when claimed
   - Preserves assignment even when `active_instance_count` changes
   - Prevents stealing messages mid-processing

**Benefits**:
- No database table for partition assignments
- Automatic rebalancing when instances join/leave
- Fair distribution across instances
- Self-contained assignment logic

## Status Tracking (Bitwise Flags)

```csharp
[Flags]
public enum MessageProcessingStatus {
  None = 0,
  Stored = 1 << 0,              // Message inserted to inbox/outbox
  EventStored = 1 << 1,         // Written to event store (if applicable)
  Published = 1 << 2,           // Published to transport (outbox only)
  ReceptorProcessed = 1 << 3,   // Receptor/handler invoked (inbox only)
  PerspectiveProcessed = 1 << 4 // Perspectives invoked (inbox only)
}
```

**Benefits:**
- Track partial completion (e.g., Stored + Published, but not EventStored)
- Enable smart retry (only retry failed stages)
- Debugging visibility (know exactly what succeeded)

## Stream Ordering

### Problem

Without ordering: Events from same aggregate can be processed out of order
```
ProductCreated (seq 1) → Instance B
ProductPriceUpdated (seq 2) → Instance A (processes first!)
ProductPriceUpdated (seq 3) → Instance B
```

### Solution

**Partition-based assignment:**
1. Hash stream_id to partition (0-9999)
2. Instance claims partitions (max 100)
3. All events from same stream → same partition → same instance
4. OrderedStreamProcessor processes each stream sequentially

**Result:**
```
ProductCreated (seq 1, partition 1234) → Instance A
ProductPriceUpdated (seq 2, partition 1234) → Instance A (waits for seq 1)
ProductPriceUpdated (seq 3, partition 1234) → Instance A (waits for seq 2)
```

## Performance Characteristics

### Latency Comparison

| Pattern | Latency | DB Load | Use Case |
|---------|---------|---------|----------|
| **Immediate** | ~10ms | High (1 call/msg) | Real-time critical operations |
| **Scoped** | ~50ms | Medium (1 call/request) | Web APIs, per-request batching |
| **Interval (100ms)** | ~100ms | Low (1 call/100ms) | Background workers, high throughput |

### Throughput

- **Before (polling):** 1000 msg/sec → 1000 queries/sec
- **After (batching):** 1000 msg/sec → 10 queries/sec (100ms interval, 100 msg/batch)
- **Reduction:** 99% fewer database calls

## Phases Completed

✅ **Phase 1-3:** Schema changes, enums, SQL function rewrite
✅ **Phase 4:** OrderedStreamProcessor for stream ordering
✅ **Phase 5:** Strategy interface + three implementations
✅ **Phase 6:** Complete integration of strategy pattern
✅ **Phase 7:** Event Store Integration
✅ **Phase 8:** EF Core Entity Updates

### Phase 6 Completion Summary

**Completed Tasks:**
1. ✅ Dispatcher integration with strategy pattern and stream ordering
2. ✅ ServiceBusConsumerWorker integration with OrderedStreamProcessor
3. ✅ WorkCoordinatorPublisherWorker using granular status tracking
4. ✅ Deleted legacy inbox/outbox files (6 files):
   - `EFCoreInbox.cs`
   - `IOutbox.cs`
   - `IInbox.cs`
   - `InMemoryOutbox.cs`
   - `InMemoryInbox.cs`
   - `OutboxPublisher.cs`
5. ✅ Updated Dispatcher.cs to remove IOutbox dependency
   - Removed `IOutbox? outbox` parameter from constructor
   - Removed `_outbox` field
   - Removed IOutbox fallback logic in `SendAsync`
   - Removed IOutbox fallback logic in `PublishAsync`
   - Updated `SendToOutboxViaScopeAsync` to use only IWorkCoordinatorStrategy
   - Updated `PublishToOutboxViaScopeAsync` to use only IWorkCoordinatorStrategy
6. ✅ Verified compilation (successful)
7. ✅ Ran dotnet format

**Architecture Enforcement:**
```
ALL Components → IWorkCoordinatorStrategy → IWorkCoordinator → process_work_batch
```
No bypass paths through direct ORM allowed.

### Phase 7 Completion Summary

**Completed Tasks:**
1. ✅ Added `wb_event_sequence` PostgreSQL sequence for global event ordering
2. ✅ Added Section 7.5 to `process_work_batch` function for event store integration
   - Automatically persists events to `wb_event_store` table
   - Dual INSERT: one for outbox events, one for inbox events
   - Convention-based: events must end with "Event" suffix
   - Automatic version incrementing per stream using `COALESCE(MAX(version) + 1, 1)`
   - Optimistic concurrency via `ON CONFLICT (stream_id, version) DO NOTHING`
   - Aggregate type extraction from event type name
3. ✅ Added `IsEvent` property to `NewOutboxMessage` and `NewInboxMessage` C# records
4. ✅ Updated `DapperWorkCoordinator.SerializeNewOutboxMessages` to include `is_event` field
5. ✅ Updated `DapperWorkCoordinator.SerializeNewInboxMessages` to include `is_event` field
6. ✅ Updated `EFCoreWorkCoordinator.SerializeNewOutboxMessages` to include `is_event` field
7. ✅ Updated `EFCoreWorkCoordinator.SerializeNewInboxMessages` to include `is_event` field
8. ✅ Updated `Dispatcher._serializeToNewOutboxMessage` to set `IsEvent = payload is IEvent`
9. ✅ Updated `ServiceBusConsumerWorker._serializeToNewInboxMessage` to set `IsEvent = payload is IEvent`
10. ✅ Verified compilation (core library projects compiled successfully)
11. ✅ Ran dotnet format

**Benefits Achieved:**
- ✅ Atomic event store + inbox/outbox insert in single transaction
- ✅ No separate IEventStore.AppendAsync call needed
- ✅ Automatic version numbering per stream
- ✅ Optimistic concurrency built-in
- ✅ Global event sequence for cross-stream ordering
- ✅ Convention-based aggregate type extraction

**Event Store Integration Flow:**
1. Message marked as event (`IsEvent = true`) by Dispatcher or ServiceBusConsumerWorker
2. Work coordinator serializes `is_event` flag to JSONB
3. `process_work_batch` checks `(elem->>'is_event')::BOOLEAN = true`
4. If true AND stream_id present AND type ends with "Event", INSERT to `wb_event_store`
5. Auto-increment version per stream, use global sequence for ordering

### Phase 8 Completion Summary

**Completed Tasks:**
1. ✅ Updated `OutboxRecord` entity with new properties:
   - `StreamId` (Guid?) - For stream-based ordering
   - `PartitionNumber` (int?) - Consistent hash result
   - `StatusFlags` (int) - Bitwise MessageProcessingStatus
   - `Flags` (int) - WorkBatchFlags
   - `SequenceOrder` (long) - Epoch milliseconds for ordering
2. ✅ Updated `InboxRecord` entity with same properties
3. ✅ Verified entity configuration (uses source generation)
4. ✅ Build verification successful
5. ✅ Ran dotnet format

**Note:** No migration scripts needed since this is the initial release - schema is correct from the start.

## Phases Remaining

### Phase 9: Comprehensive Testing (100% Branch Coverage Goal)

**Status:** Test plan created, implementation in progress

**Detailed Plan:** See `PHASE_9_TEST_COVERAGE_PLAN.md` for comprehensive testing strategy

**Current Coverage Analysis:**
- **Existing Tests:** 8 integration tests in DapperWorkCoordinatorTests.cs
- **Coverage:** ~60% (core patterns covered, Phase 7+ features missing)
- **Missing:** 33 additional tests needed for 100% coverage

**Priority 1 Tests (Event Store + New Messages):**
1. Event Store Integration (5 tests)
   - Event persistence with IsEvent flag
   - Version incrementing per stream
   - Optimistic concurrency handling
2. New Message Storage (4 tests)
   - NewOutboxMessage storage and immediate return
   - NewInboxMessage deduplication
   - Partition assignment via consistent hashing
3. IsEvent Serialization (4 tests)
   - Dapper and EFCore serialization of IsEvent flag

**Priority 2 Tests (Ordering + Partitioning):**
4. Stream Ordering (5 tests) - OrderedStreamProcessor
5. Partition Distribution (4 tests) - Consistent hashing
6. Granular Status Tracking (3 tests) - Bitwise flags

**Priority 3 Tests (Strategies):**
7. Strategy implementations (12 tests)
   - Immediate, Scoped, Interval strategies

**Estimated Coverage Progression:**
- After Priority 1: ~75% (+30 branches)
- After Priority 2: ~93% (+65 branches total)
- After Priority 3: **100%** (+93 branches total)

**Success Criteria:**
- [ ] All 33 new tests passing
- [ ] Branch coverage >= 100% for all work coordinator components
- [ ] All error paths tested
- [ ] All edge cases covered

## Open Questions

1. ~~**Event Store Integration:** Should `process_work_batch` automatically insert to event_store for messages implementing IEvent?~~ ✅ **RESOLVED:** Yes, implemented in Phase 7 using `IsEvent` flag and convention-based filtering
2. **Schema Migration:** How to handle existing outbox/inbox records during migration?
3. **Monitoring:** What metrics to expose for partition distribution, lease health, ordering violations?

## References

- Previous work coordinator refactor (pre-strategy pattern)
- Partition-based ordering inspiration: Kafka consumer groups
- Granular status tracking: Saga pattern partial completion
