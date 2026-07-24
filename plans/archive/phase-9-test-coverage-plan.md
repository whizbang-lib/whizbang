# Phase 9: Comprehensive Test Coverage Plan

**Goal:** Achieve 100% branch coverage for Work Coordinator components

**Created:** 2025-12-06

## Current Test Coverage Analysis

### ✅ Already Covered (DapperWorkCoordinatorTests.cs)

**Integration Tests:**
1. ✅ Heartbeat updates (`ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync`)
2. ✅ Outbox message completions (`ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync`)
3. ✅ Outbox message failures with error tracking (`ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync`)
4. ✅ Inbox message completions (`ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync`)
5. ✅ Inbox message failures with error tracking (`ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedWithErrorAsync`)
6. ✅ Orphaned outbox recovery - lease expiry (`ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync`)
7. ✅ Orphaned inbox recovery - lease expiry (`ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync`)
8. ✅ Mixed operations - all patterns together (`ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync`)

**Coverage:** ~60% - Core work coordination patterns covered, but Phase 7+ features missing

---

## ❌ Missing Coverage (Phase 7+ Features)

### 1. IsEvent Flag Serialization
**Component:** `DapperWorkCoordinator`, `EFCoreWorkCoordinator`

**Tests Needed:**
- `SerializeNewOutboxMessages_WithIsEventTrue_IncludesIsEventFieldAsync()`
  - Create NewOutboxMessage with IsEvent = true
  - Verify serialized JSON contains `"is_event":true`
- `SerializeNewOutboxMessages_WithIsEventFalse_IncludesIsEventFieldAsync()`
  - Create NewOutboxMessage with IsEvent = false
  - Verify serialized JSON contains `"is_event":false`
- `SerializeNewInboxMessages_WithIsEventTrue_IncludesIsEventFieldAsync()`
  - Create NewInboxMessage with IsEvent = true
  - Verify serialized JSON contains `"is_event":true`
- `SerializeNewInboxMessages_WithIsEventFalse_IncludesIsEventFieldAsync()`
  - Create NewInboxMessage with IsEvent = false
  - Verify serialized JSON contains `"is_event":false`

**Branch Coverage Impact:** +8 branches

---

### 2. Event Store Integration
**Component:** `process_work_batch` SQL function

**Tests Needed:**
- `ProcessWorkBatchAsync_WithEventOutbox_PersistsToEventStoreAsync()`
  - Queue NewOutboxMessage with IsEvent=true, StreamId present, type ends with "Event"
  - Flush via ProcessWorkBatchAsync
  - Verify event persisted to wb_event_store with:
    - Correct stream_id
    - Auto-incremented version (starts at 1)
    - Global sequence number from wb_event_sequence
    - Extracted aggregate_type
- `ProcessWorkBatchAsync_WithEventInbox_PersistsToEventStoreAsync()`
  - Queue NewInboxMessage with IsEvent=true, StreamId present, type ends with "Event"
  - Flush via ProcessWorkBatchAsync
  - Verify event persisted to wb_event_store
- `ProcessWorkBatchAsync_EventVersionConflict_HandlesOptimisticConcurrencyAsync()`
  - Insert event with version 1
  - Try to insert duplicate with version 1
  - Verify ON CONFLICT DO NOTHING works (no error, no duplicate)
- `ProcessWorkBatchAsync_MultipleEventsInStream_IncrementsVersionAsync()`
  - Insert 3 events for same stream
  - Verify versions: 1, 2, 3
- `ProcessWorkBatchAsync_NonEvent_DoesNotPersistToEventStoreAsync()`
  - Queue message with IsEvent=false
  - Verify NOT persisted to wb_event_store

**Branch Coverage Impact:** +10 branches

---

### 3. Stream Ordering (OrderedStreamProcessor)
**Component:** `OrderedStreamProcessor`

**Tests Needed:**
- `ProcessInboxWorkAsync_SingleStream_ProcessesInOrderAsync()`
  - Submit 5 messages from same stream (different SequenceOrder)
  - Verify processed in SequenceOrder ascending order
- `ProcessInboxWorkAsync_MultipleStreams_ProcessesConcurrentlyAsync()`
  - Submit messages from 3 different streams
  - Verify all streams processed independently (not blocked by each other)
- `ProcessInboxWorkAsync_StreamWithError_ContinuesOtherStreamsAsync()`
  - Submit messages from 2 streams
  - Make one stream's processor throw exception
  - Verify other stream continues processing
- `ProcessInboxWorkAsync_PartialFailure_ReportsCorrectStatusAsync()`
  - Process message that completes Stored but fails at next stage
  - Verify CompletedStatus reports partial success
- `ProcessOutboxWorkAsync_SameTests_ForOutboxAsync()`
  - Same tests as above for outbox work

**Branch Coverage Impact:** +15 branches

---

### 4. Partition Distribution
**Component:** `process_work_batch` SQL function

**Tests Needed:**
- `ProcessWorkBatchAsync_ConsistentHashing_SameStreamSamePartitionAsync()`
  - Insert 10 messages with same stream_id
  - Verify all assigned same partition_number
- `ProcessWorkBatchAsync_PartitionAssignment_WithinRangeAsync()`
  - Insert messages with various stream_ids
  - Verify all partition_numbers in range 0-9999
- `ProcessWorkBatchAsync_LoadBalancing_DistributesAcrossInstancesAsync()`
  - Create 3 service instances
  - Insert 300 messages (30 different streams)
  - Verify each instance claims ~100 partitions (max_partitions_per_instance)
  - Verify partitions distributed across instances
- `ProcessWorkBatchAsync_InstanceFailover_RedistributesPartitionsAsync()`
  - Instance A claims partitions
  - Mark Instance A as stale (old heartbeat)
  - Instance B calls ProcessWorkBatchAsync
  - Verify Instance B claims orphaned partitions

**Branch Coverage Impact:** +12 branches

---

### 5. Granular Status Tracking (Bitwise Flags)
**Component:** `process_work_batch` SQL function, `MessageProcessingStatus` enum

**Tests Needed:**
- `ProcessWorkBatchAsync_StatusFlags_AccumulateCorrectlyAsync()`
  - Complete message with Stored status
  - Complete again with Published status
  - Verify status_flags = Stored | Published (bitwise OR)
- `ProcessWorkBatchAsync_PartialCompletion_TracksCorrectlyAsync()`
  - Fail message with CompletedStatus = Stored | EventStored
  - Verify database status_flags reflects partial completion
- `ProcessWorkBatchAsync_WorkBatchFlags_SetCorrectlyAsync()`
  - NewlyStored message has Flags = NewlyStored
  - Orphaned message has Flags = Orphaned
  - Verify flags returned in WorkBatch

**Branch Coverage Impact:** +8 branches

---

### 6. New Message Storage (NewOutboxMessage / NewInboxMessage)
**Component:** `process_work_batch` SQL function

**Tests Needed:**
- `ProcessWorkBatchAsync_NewOutboxMessage_StoresAndReturnsImmediatelyAsync()`
  - Queue NewOutboxMessage
  - Verify message stored in wb_outbox
  - Verify message returned in WorkBatch.OutboxWork (immediate processing pattern)
- `ProcessWorkBatchAsync_NewInboxMessage_StoresWithDeduplicationAsync()`
  - Queue same NewInboxMessage twice
  - Verify first call stores and returns work
  - Verify second call returns empty (duplicate)
- `ProcessWorkBatchAsync_NewInboxMessage_WithStreamId_AssignsPartitionAsync()`
  - Queue NewInboxMessage with StreamId
  - Verify partition_number assigned via consistent hashing
  - Verify sequence_order set from timestamp
- `ProcessWorkBatchAsync_NewOutboxMessage_WithStreamId_AssignsPartitionAsync()`
  - Same as above for outbox

**Branch Coverage Impact:** +12 branches

---

### 7. Stale Instance Cleanup
**Component:** `process_work_batch` SQL function

**Tests Needed:**
- `ProcessWorkBatchAsync_StaleInstances_CleanedUpAsync()`
  - Insert instance with old heartbeat (> staleThresholdSeconds)
  - Call ProcessWorkBatchAsync
  - Verify stale instance deleted
- `ProcessWorkBatchAsync_ActiveInstances_NotCleanedAsync()`
  - Insert instance with recent heartbeat
  - Call ProcessWorkBatchAsync
  - Verify instance still exists

**Branch Coverage Impact:** +4 branches

---

## Unit Tests for Strategy Implementations

### 8. Immediate Strategy
**Component:** `ImmediateWorkCoordinatorStrategy`

**Tests Needed:**
- `FlushAsync_ImmediatelyCallsWorkCoordinatorAsync()`
- `QueueOutboxMessage_FlushesImmediatelyAsync()`
- `QueueInboxMessage_FlushesImmediatelyAsync()`

**Branch Coverage Impact:** +6 branches

---

### 9. Scoped Strategy
**Component:** `ScopedWorkCoordinatorStrategy`

**Tests Needed:**
- `DisposeAsync_FlushesQueuedMessagesAsync()`
- `FlushAsync_BeforeDisposal_FlushesImmediatelyAsync()`
- `MultipleQueues_FlushedTogetherOnDisposalAsync()`

**Branch Coverage Impact:** +8 branches

---

### 10. Interval Strategy
**Component:** `IntervalWorkCoordinatorStrategy`

**Tests Needed:**
- `BackgroundTimer_FlushesEveryIntervalAsync()`
- `QueuedMessages_BatchedUntilTimerAsync()`
- `DisposeAsync_FlushesAndStopsTimerAsync()`
- `ManualFlushAsync_DoesNotWaitForTimerAsync()`

**Branch Coverage Impact:** +10 branches

---

## Test Execution Plan

### Priority 1: Core Phase 7 Features (High Impact)
1. Event Store Integration (5 tests, +10 branches)
2. New Message Storage (4 tests, +12 branches)
3. IsEvent Serialization (4 tests, +8 branches)

**Estimated Time:** 2-3 hours
**Coverage Gain:** +30 branches (~15%)

---

### Priority 2: Stream Ordering & Partitioning (Medium Impact)
4. Stream Ordering (5 tests, +15 branches)
5. Partition Distribution (4 tests, +12 branches)
6. Granular Status Tracking (3 tests, +8 branches)

**Estimated Time:** 3-4 hours
**Coverage Gain:** +35 branches (~18%)

---

### Priority 3: Strategy Patterns & Cleanup (Lower Impact)
7. Stale Instance Cleanup (2 tests, +4 branches)
8. Immediate Strategy (3 tests, +6 branches)
9. Scoped Strategy (3 tests, +8 branches)
10. Interval Strategy (4 tests, +10 branches)

**Estimated Time:** 2-3 hours
**Coverage Gain:** +28 branches (~14%)

---

## Coverage Target Calculation

**Current Estimated Coverage:** ~60% (8 tests covering core patterns)
**Estimated Total Branches:** ~200 branches across all components

**After Priority 1:** ~75% coverage (+30 branches)
**After Priority 2:** ~93% coverage (+65 branches total)
**After Priority 3:** **~100% coverage** (+93 branches total)

---

## Additional Coverage Tools

### Run Coverage Command
```bash
cd tests/Whizbang.Data.Postgres.Tests
dotnet run -- --coverage --coverage-output-format cobertura --coverage-output coverage.xml
```

### View Coverage Results
```bash
# Read coverage summary
grep "line-rate" bin/Debug/net10.0/TestResults/coverage.xml | head -5
```

---

## Success Criteria

- [ ] All 33 new tests passing
- [ ] Branch coverage >= 100% for:
  - `DapperWorkCoordinator`
  - `EFCoreWorkCoordinator`
  - `OrderedStreamProcessor`
  - `ImmediateWorkCoordinatorStrategy`
  - `ScopedWorkCoordinatorStrategy`
  - `IntervalWorkCoordinatorStrategy`
- [ ] Integration tests verify `process_work_batch` SQL function behavior
- [ ] All error paths tested (exceptions, conflicts, timeouts)
- [ ] All edge cases covered (empty arrays, null values, boundary conditions)

---

## Next Steps

1. Implement Priority 1 tests (Event Store + New Messages)
2. Run coverage and verify gain
3. Implement Priority 2 tests (Ordering + Partitioning)
4. Run coverage and verify gain
5. Implement Priority 3 tests (Strategies)
6. Final coverage measurement
7. Fix any gaps until 100% achieved
