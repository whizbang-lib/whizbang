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

### Related Files

- `SharedFixtureSource.cs` - Manages shared emulator and ServiceBusClient
- `ServiceBusIntegrationFixture.cs` - Per-test fixture with shared Service Bus
- `EmulatorLauncher.cs` - Handles emulator startup and lifecycle
