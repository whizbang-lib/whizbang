# Integration Testing Setup

## Azure Service Bus Emulator Configuration

### Critical Constraints

The Azure Service Bus Emulator has several limitations that dictate our test architecture:

1. **Shared Emulator Instance**
   - Single emulator shared across all tests
   - Takes 45-60 seconds to start on first test
   - Cannot be restarted between tests due to startup cost

2. **Connection Limits**
   - Limited number of concurrent connections
   - Tests CANNOT run in parallel
   - Must use `[NotInParallel]` attribute on all integration test classes

3. **Topic Limits**
   - Shared topic/subscription namespace
   - Topics are created once and reused
   - Topics are **cleared/flushed** between tests (not recreated)

4. **Shared Service Architecture**
   - Same `ServiceBusClient` instance shared across tests
   - Connections and topics are shared resources
   - Each test gets fresh PostgreSQL database but reuses Service Bus infrastructure

### Rationale for Design

**Why Shared Emulator?**
- 45-60 second startup time makes per-test instantiation impractical
- Connection limits prevent parallel test execution anyway
- Minimizes Docker container overhead

**Why Clear Topics Instead of Recreate?**
- Topic creation/deletion is slow
- Clearing is faster and sufficient for test isolation
- Reduces emulator load and improves test reliability

**Why Sequential Test Execution?**
- Emulator connection limits prevent parallelism
- Shared topics require exclusive access during test
- Ensures deterministic test behavior

### Test Execution Requirements

**MUST DO**:
- Run tests sequentially (NOT in parallel)
- Use `[NotInParallel]` on all integration test classes
- Wait for emulator to be ready before first test
- Clean up topics between tests (handled by fixture)

**DO NOT**:
- Run integration tests in parallel (will fail with connection errors)
- Attempt to restart emulator between tests (too slow)
- Create new ServiceBusClient per test (connection limit)
- Skip topic cleanup (will cause test pollution)

### Typical Test Run Characteristics

- **Total Tests**: ~40-50 lifecycle integration tests
- **Total Duration**: 8-10 minutes (sequential execution)
- **Per-Test Duration**: 10-20 seconds average
- **First Test**: +45-60 seconds for emulator startup
- **Failure Mode**: Timeouts if emulator not ready or connection limit reached

### Troubleshooting

**Tests Timeout on First Run**:
- Emulator may not be fully started
- Wait 60 seconds and retry
- Check emulator logs: `docker logs <emulator-container-id>`

**Connection Errors**:
- Too many concurrent connections
- Ensure tests are NOT running in parallel
- Verify `[NotInParallel]` attribute present

**Test Pollution**:
- Topics not cleaned between tests
- Check `ServiceBusIntegrationFixture.DrainAllSubscriptionsAsync()`
- Verify fixture cleanup in `[After(Test)]` hook

## Cross-Service Event Distribution Architecture

### Critical Design Requirement

**Both InventoryWorker and BFF MUST have ServiceBusConsumerWorker registered** to receive events from ServiceBus topics.

### Event Flow

```
Publisher (InventoryWorker):
  Receptor → PublishAsync → Outbox → WorkCoordinatorPublisherWorker → ServiceBus Topics

Subscribers (InventoryWorker + BFF):
  ServiceBus Topics → ServiceBusConsumerWorker → Inbox → process_work_batch → Event Store → Perspectives
```

### Why Both Services Need ServiceBusConsumerWorker

1. **InventoryWorker**: Publishes events but MUST also subscribe to receive them back
   - Events published by receptors go to outbox → ServiceBus
   - Without subscriber, events never return to `inventory.wh_event_store`
   - Perspectives timeout waiting for events that never arrive

2. **BFF**: Cross-service event consumption
   - Subscribes to InventoryWorker's events
   - Receives via ServiceBus → stores to `bff.wh_event_store`
   - Perspectives materialize from received events

### Test Fixture Configuration

**InventoryWorker** (Lines 458-477):
- Subscribes to `sub-00-b` and `sub-01-b` on topics `topic-00` and `topic-01`
- Receives its own published events from ServiceBus
- Stores events to `inventory.wh_event_store` via `process_work_batch` Phase 4.5B

**BFF** (Lines 599-618):
- Subscribes to `sub-00-a` and `sub-01-a` on the same topics
- Receives cross-service events from InventoryWorker
- Stores events to `bff.wh_event_store`

### Common Pitfall

**WRONG**: Assuming InventoryWorker doesn't need ServiceBusConsumerWorker because it has local receptors
**RIGHT**: InventoryWorker MUST subscribe to receive its own events from ServiceBus for perspectives to work

Without ServiceBusConsumerWorker on InventoryWorker:
- ✅ Events are published to ServiceBus
- ✅ BFF receives and processes events
- ❌ Events never stored to `inventory.wh_event_store`
- ❌ InventoryWorker perspectives timeout waiting for events

### Related Files

- `SharedFixtureSource.cs` - Manages shared emulator and ServiceBusClient
- `ServiceBusIntegrationFixture.cs` - Per-test fixture with shared Service Bus
- `EmulatorLauncher.cs` - Handles emulator startup and lifecycle
- `Config-Named.json` - Emulator topic and subscription configuration
