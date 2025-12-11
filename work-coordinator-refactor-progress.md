# Work Coordinator Refactor - Progress Summary

**Started:** 2025-12-06
**Status:** Core Infrastructure Complete (Phases 1-5) - Integration Work Remaining (Phases 6-8)

---

## ✅ Completed Work

### Phase 1: SQL Schema & Partitioning (COMPLETE)

**Files Modified:**
- `src/Whizbang.Data.Dapper.Postgres/whizbang-schema.sql`

**Changes:**
1. ✅ Added `stream_id UUID` and `partition_number INTEGER` columns to `wb_inbox` and `wb_outbox`
2. ✅ Changed `status` from `VARCHAR(50)` to `INTEGER` for bitwise flag support
3. ✅ Added `processed_at TIMESTAMPTZ` to `wb_outbox`
4. ✅ Created `wb_partition_assignments` table for partition ownership tracking
5. ✅ Created `compute_partition(stream_id, partition_count)` function for consistent hashing
6. ✅ Added partition-based indexes:
   - `idx_inbox_partition_status`
   - `idx_inbox_partition_stream_order`
   - `idx_outbox_partition_status`
   - `idx_outbox_partition_stream_order`

**Key Features:**
- 10,000 partitions (configurable) for fine-grained load distribution
- Consistent hashing ensures same stream always goes to same instance
- Partition assignments tracked with heartbeat for automatic failover

---

### Phase 2: C# Enums & Data Structures (COMPLETE)

**Files Created:**
- `src/Whizbang.Core/Messaging/WorkCoordinatorEnums.cs`

**Files Modified:**
- `src/Whizbang.Core/Messaging/IWorkCoordinator.cs`

**New Types:**

1. ✅ **WorkBatchFlags** enum (extensible via bit flags):
   - `NewlyStored` - Just stored in this call
   - `Orphaned` - Claimed from failed instance
   - `DebugMode` - Keep completed records
   - `FromEventStore` - Also in event store
   - `HighPriority`, `RetryAfterFailure` - Future use
   - Bits 6-31 reserved

2. ✅ **MessageProcessingStatus** enum (tracks pipeline stages):
   - `Stored` (1) - Persisted to inbox/outbox
   - `EventStored` (2) - Written to event store
   - `Published` (4) - Sent to transport
   - `ReceptorProcessed` (8) - Handler completed
   - `PerspectiveProcessed` (16) - Projections updated
   - `Failed` (32768 / bit 15) - Processing failed
   - `FullyCompleted` (24) = ReceptorProcessed | PerspectiveProcessed

3. ✅ **MessageCompletion** record:
   - `MessageId` + `Status` (which stages completed)

4. ✅ **MessageFailure** record:
   - `MessageId` + `CompletedStatus` (what succeeded before failure) + `Error`

5. ✅ **Updated OutboxWork/InboxWork** records:
   - Added `StreamId`, `PartitionNumber`, `Status`, `Flags`, `SequenceOrder`

---

### Phase 3: PostgreSQL Function Rewrite (COMPLETE)

**Files Modified:**
- `src/Whizbang.Data.Dapper.Postgres/whizbang-schema.sql`

**New Signature:**
```sql
process_work_batch(
  -- Instance ID, service info, metadata
  p_instance_id, p_service_name, p_host_name, p_process_id, p_metadata,

  -- NEW: Completion/failure tracking with status pairing
  p_outbox_completions JSONB DEFAULT '[]',  -- [{"message_id": "...", "status": 12}, ...]
  p_outbox_failures JSONB DEFAULT '[]',     -- [{"message_id": "...", "status": 8, "error": "..."}, ...]
  p_inbox_completions JSONB DEFAULT '[]',
  p_inbox_failures JSONB DEFAULT '[]',

  -- NEW: Immediate processing support
  p_new_outbox_messages JSONB DEFAULT '[]',  -- Store & claim in one call
  p_new_inbox_messages JSONB DEFAULT '[]',

  -- Configuration
  p_lease_seconds DEFAULT 300,
  p_stale_threshold_seconds DEFAULT 600,
  p_flags INTEGER DEFAULT 0,  -- WorkBatchFlags
  p_partition_count INTEGER DEFAULT 10000,
  p_max_partitions_per_instance INTEGER DEFAULT 100
)
RETURNS TABLE(
  source, msg_id, destination, event_type, event_data, metadata, scope,
  stream_id, partition_number, attempts, status, flags, sequence_order
)
```

**New Logic (350+ lines):**

1. ✅ **Partition Assignment** (lines 230-256):
   - Consistent hashing via `abs(hashtext(instance_id || partition_num))`
   - Claims up to `max_partitions_per_instance` partitions
   - Automatic cleanup when instances go stale

2. ✅ **Granular Completion Tracking** (lines 258-321):
   - Bitwise OR to add newly completed flags: `status = status | completion.status`
   - Debug mode preserves records, production deletes when `(status & 24) = 24`
   - Separate logic for inbox and outbox

3. ✅ **Partial Failure Tracking** (lines 323-358):
   - Sets Failed flag (bit 15) while preserving completed stages
   - `status = (status | failure.status | 32768)`

4. ✅ **Immediate Processing** (lines 360-440):
   - Stores new messages with partition assignment
   - Computes partition via `compute_partition(stream_id)`
   - Sets initial status: `1 | CASE WHEN is_event THEN 2 ELSE 0 END`
   - Inbox uses `ON CONFLICT DO NOTHING` for atomic deduplication

5. ✅ **Stream-Ordered Work Retrieval** (lines 442-503):
   - Filters by owned partitions only
   - **CRITICAL:** `ORDER BY stream_id, created_at/received_at`
   - Returns sequence_order as epoch milliseconds

---

### Phase 4: Ordered Stream Processor (COMPLETE)

**Files Created:**
- `src/Whizbang.Core/Messaging/OrderedStreamProcessor.cs`

**Functionality:**
- ✅ Groups work items by `stream_id`
- ✅ Processes each stream **sequentially** (maintains ordering)
- ✅ Optionally processes **different streams in parallel**
- ✅ Stops stream on failure (preserves ordering guarantee)
- ✅ Separate methods for inbox and outbox processing
- ✅ Configurable via `ParallelizeStreams` option

**Key Algorithm:**
```csharp
var streamGroups = inboxWork
  .GroupBy(w => w.StreamId ?? Guid.Empty)
  .Select(g => new StreamBatch {
    StreamId = g.Key,
    Messages = g.OrderBy(m => m.SequenceOrder).ToList()  // Ensures ordering
  });

foreach (var streamBatch in streamGroups) {
  foreach (var message in streamBatch.Messages) {  // Sequential!
    try {
      var completedStatus = await processor(message);
      completionHandler(messageId, completedStatus);
    } catch {
      // STOP this stream, remaining messages retry later
      break;
    }
  }
}
```

---

### Phase 5: Strategy Pattern Foundation (COMPLETE)

**Files Created:**
- `src/Whizbang.Core/Messaging/IWorkCoordinatorStrategy.cs`
- `src/Whizbang.Core/Messaging/ImmediateWorkCoordinatorStrategy.cs` (stub)

**Interface Defined:**
```csharp
public interface IWorkCoordinatorStrategy {
  void QueueOutboxMessage(IMessageEnvelope envelope, string destination, Guid? streamId);
  void QueueInboxMessage(IMessageEnvelope envelope, string handlerName, Guid? streamId);
  void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus);
  void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus);
  void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error);
  void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error);
  Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default);
}
```

**Strategy Types:**
- `Immediate` - Call process_work_batch for each operation (lowest latency)
- `Scoped` - Batch within scope, flush on disposal (balanced)
- `Interval` - Batch and flush on timer (highest throughput)

**Configuration:**
```csharp
public class WorkCoordinatorOptions {
  public int PartitionCount { get; set; } = 10_000;
  public int MaxPartitionsPerInstance { get; set; } = 100;
  public bool ParallelizeStreams { get; set; } = false;
  public WorkCoordinatorStrategy Strategy { get; set; } = Scoped;
  public int IntervalMilliseconds { get; set; } = 100;
  public bool DebugMode { get; set; } = false;
  public int LeaseSeconds { get; set; } = 300;
  public int StaleThresholdSeconds { get; set; } = 600;
}
```

---

## 🚧 Remaining Work

### Phase 6: Integration (NEXT PRIORITY)

**Need to Update:**

1. **EFCoreWorkCoordinator** (`src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs`):
   - Update to use new JSONB parameters (`p_outbox_completions`, etc.)
   - Add support for `p_new_outbox_messages` / `p_new_inbox_messages`
   - Parse new return columns (`stream_id`, `partition_number`, `status`, `flags`, `sequence_order`)
   - Map INTEGER status to `MessageProcessingStatus` enum
   - Map INTEGER flags to `WorkBatchFlags` enum

2. **Complete Strategy Implementations**:
   - `ScopedWorkCoordinatorStrategy` - Batch operations, flush on disposal
   - `IntervalWorkCoordinatorStrategy` - Batch operations, flush on timer
   - Wire up strategies in DI container

3. **EFCoreOutbox** (`src/Whizbang.Data.EFCore.Postgres/EFCoreOutbox.cs`):
   - Inject `IWorkCoordinatorStrategy`
   - Replace direct INSERT with strategy.QueueOutboxMessage()
   - Extract stream_id from envelope
   - Determine is_event from payload type

4. **EFCoreInbox** (`src/Whizbang.Data.EFCore.Postgres/EFCoreInbox.cs`):
   - Inject `IWorkCoordinatorStrategy`
   - Replace direct queries with strategy methods
   - `HasProcessedAsync()` + `StoreAsync()` → `strategy.QueueInboxMessage()` + `FlushAsync()`
   - `MarkProcessedAsync()` → `strategy.QueueInboxCompletion()`

5. **Dispatcher** (`src/Whizbang.Core/Dispatcher.cs`):
   - Update `SendToOutboxViaScopeAsync()` to use strategy
   - Track granular status (Stored, EventStored, Published, etc.)
   - Report completions/failures with appropriate flags

6. **ServiceBusConsumerWorker** (`src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs`):
   - Inject `OrderedStreamProcessor` and `IWorkCoordinatorStrategy`
   - Use strategy for inbox operations
   - Use `OrderedStreamProcessor.ProcessInboxWorkAsync()` for ordered processing
   - Track status through pipeline (Stored → EventStored → ReceptorProcessed → PerspectiveProcessed)

---

### Phase 7: Event Store Integration

**File to Update:**
- `src/Whizbang.Data.Dapper.Postgres/whizbang-schema.sql`

**Logic to Add:**
In `process_work_batch` function, around lines 360-440:

```sql
-- 6. Store new outbox messages (with event store integration)
IF jsonb_array_length(p_new_outbox_messages) > 0 THEN
  FOR v_new_msg IN ... LOOP
    v_partition := compute_partition(v_new_msg.stream_id, p_partition_count);

    -- If event, store in event store FIRST (atomic!)
    IF v_new_msg.is_event THEN
      INSERT INTO wb_event_store (
        event_id, stream_id, aggregate_id, event_type, event_data, metadata, scope, version, created_at
      ) VALUES (
        v_new_msg.message_id,
        v_new_msg.stream_id,
        v_new_msg.stream_id,  -- Use stream_id as aggregate_id
        v_new_msg.message_type,
        v_new_msg.message_data::JSONB,
        v_new_msg.metadata::JSONB,
        v_new_msg.scope::JSONB,
        (SELECT COALESCE(MAX(version), 0) + 1 FROM wb_event_store WHERE stream_id = v_new_msg.stream_id),
        v_now
      )
      ON CONFLICT (event_id) DO NOTHING;  -- Idempotent!
    END IF;

    -- Then store in outbox...
  END LOOP;
END IF;

-- Similar for inbox (lines 401-440)
```

**Benefits:**
- Events persisted forever (immutable)
- Perspectives can be rebuilt from event store
- Atomic transaction: event store + inbox/outbox together
- Idempotent via `ON CONFLICT (event_id) DO NOTHING`

---

### Phase 8: EF Core Entity Updates & Migrations

**Files to Update:**

1. **OutboxRecord** (`src/Whizbang.Data.EFCore.Postgres/Entities/OutboxRecord.cs`):
   ```csharp
   public int Status { get; set; }  // Change from string to int
   public Guid? StreamId { get; set; }  // Add
   public int? PartitionNumber { get; set; }  // Add
   public DateTimeOffset? ProcessedAt { get; set; }  // Add
   ```

2. **InboxRecord** (`src/Whizbang.Data.EFCore.Postgres/Entities/InboxRecord.cs`):
   ```csharp
   public int Status { get; set; }  // Change from string to int
   public Guid? StreamId { get; set; }  // Add
   public int? PartitionNumber { get; set; }  // Add
   ```

3. **Migration Script**:
   ```sql
   -- Migrate existing records
   ALTER TABLE wb_outbox
     ADD COLUMN stream_id UUID,
     ADD COLUMN partition_number INTEGER,
     ADD COLUMN processed_at TIMESTAMPTZ,
     ALTER COLUMN status TYPE INTEGER USING CASE
       WHEN status = 'Pending' THEN 1
       WHEN status = 'Publishing' THEN 1
       WHEN status = 'Published' THEN 7  -- Stored | EventStored | Published
       WHEN status = 'Failed' THEN 32769  -- Stored | Failed
       ELSE 1
     END;

   -- Similar for wb_inbox
   ```

---

## Architecture Summary

### Database Call Reduction

**Before:**
- Outbox: INSERT → wait 1s → SELECT → publish → UPDATE
- Inbox: SELECT (dedup) → INSERT → process → UPDATE
- **Total: 5 queries + 1 second latency per message**

**After:**
- Outbox: `process_work_batch(new_outbox_messages)` → publish → `process_work_batch(completions)` (piggybacked)
- Inbox: `process_work_batch(new_inbox_messages)` → process → completion (piggybacked)
- **Total: 1 function call per message, 0ms latency**

**Performance Gain:** 80% fewer DB calls, 1000ms latency eliminated

---

### Stream Ordering Guarantee

**Flow:**
1. Message arrives → `compute_partition(stream_id)` → partition 42
2. Instance claims partition 42 via consistent hashing
3. `process_work_batch` returns work **ordered by stream_id, then timestamp**
4. `OrderedStreamProcessor` groups by stream, processes each stream **sequentially**
5. Failure stops stream processing → remaining events retry in next batch
6. **Result:** Events from same stream always processed in order, by single instance at a time

---

### Status Tracking Example

**Outbox Flow:**
```
Initial: Stored (1)
After event store write: Stored | EventStored (3)
After publish: Stored | EventStored | Published (7)
After processing: ... | ReceptorProcessed (15)
After perspectives: ... | PerspectiveProcessed (31 = FullyCompleted)
→ Delete from outbox (or keep in debug mode)
```

**Inbox Flow with Failure:**
```
Initial: Stored (1)
After event store: Stored | EventStored (3)
After receptor: ... | ReceptorProcessed (11)
Perspective fails: 11 | Failed (32779)
→ Retry later, only perspective step
```

---

## Next Steps

1. **Run schema migration** on development database
2. **Update EFCoreWorkCoordinator** to parse new columns
3. **Implement ScopedWorkCoordinatorStrategy** (most common use case)
4. **Update Dispatcher and ServiceBusConsumerWorker** for strategy integration
5. **Add event store integration** to process_work_batch
6. **Write integration tests** for stream ordering
7. **Performance benchmark** old vs new system
8. **Create ARCHITECTURE.md** with mermaid diagrams from plan

---

## Testing Strategy

**Unit Tests:**
- `compute_partition()` consistency
- `MessageProcessingStatus` flag operations
- `OrderedStreamProcessor` stream grouping

**Integration Tests:**
- 3 instances, 500 streams, verify partition distribution
- Same stream events arrive out of order → verify ordered processing
- Instance failure during stream processing → verify failover
- Partial completion (receptor succeeds, perspective fails) → verify retry

**Performance Tests:**
- 10,000 messages, old system vs new
- Measure: DB calls, latency, throughput
- Expected: 80% fewer calls, 5-10x throughput increase

---

## Key Design Decisions

1. **10,000 partitions** - Sweet spot for distribution without excessive overhead
2. **Consistent hashing** - Minimizes reassignment when instances change
3. **INTEGER status flags** - Extensible, efficient bitwise operations
4. **Separate stream processor** - Clean separation of ordering logic
5. **Strategy pattern** - Flexible batching strategies for different scenarios
6. **Event store in process_work_batch** - Atomic transactions, single round-trip
7. **ON CONFLICT DO NOTHING** - Idempotent deduplication built into SQL

---

## Migration Checklist

- [x] SQL schema changes (partitions, status flags)
- [x] process_work_batch rewrite (partitioning, granular status)
- [x] C# enums (WorkBatchFlags, MessageProcessingStatus)
- [x] Record types (MessageCompletion, MessageFailure)
- [x] OrderedStreamProcessor class
- [x] IWorkCoordinatorStrategy interface
- [ ] Complete strategy implementations
- [ ] Update EF Core entities
- [ ] Update EFCoreWorkCoordinator
- [ ] Update Dispatcher
- [ ] Update ServiceBusConsumerWorker
- [ ] Event store integration in SQL
- [ ] Migration scripts
- [ ] Integration tests
- [ ] Performance benchmarks
- [ ] Documentation

---

**Estimated Remaining Work:** 8-12 hours for integration + testing + documentation

