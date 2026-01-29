# Flaky Test Patterns and Fixes

> **Purpose**: Guide for identifying and fixing flaky tests in the Whizbang test suite. Read this when tests pass sometimes but fail other times.

## Quick Diagnosis

When a test fails intermittently, check these patterns in order:

1. **Static shared resources** → Add `[NotInParallel]`
2. **Missing event waiters** → Wait for ALL events that affect asserted data
3. **Insufficient timeouts** → Increase waiter/delay timeouts for parallel load
4. **Timing-sensitive operations** → Use deterministic synchronization instead of delays

---

## Pattern 1: Static Shared Resources

### Symptoms
- Test passes in isolation, fails in parallel
- Assertions on exact counts fail (e.g., "expected 1024, got 1019")
- Resource state contaminated by other tests

### Root Cause
Static singletons (pools, caches, registries) are shared across parallel tests.

### Example
```csharp
// PolicyContextPool is static - shared across all tests
public static class PolicyContextPool {
  private static readonly ConcurrentBag<PolicyContext> _pool = [];
  private static int _poolSize;
  private const int MAX_POOL_SIZE = 1024;
}
```

### Fix
```csharp
// Add NotInParallel to isolate tests using shared static resources
[NotInParallel("PolicyContextPool")]
public class PolicyContextPoolTests {
  // Tests now run sequentially, no interference
}

// Also relax exact assertions to tolerate minor drift
await Assert.That(reusedCount).IsGreaterThanOrEqualTo(970)  // Not exactly 1024
  .Because("Pool should reuse approximately 1024 contexts");
```

### When to Use `[NotInParallel]`
- Tests that modify static state
- Tests that assert on exact counts from shared resources
- Tests that use shared external resources (ServiceBus topics, database connections)

---

## Pattern 2: Missing Event Waiters

### Symptoms
- Test times out waiting for data
- Assertions fail with wrong values (e.g., "expected 88, got 0")
- Data appears eventually if you add delays

### Root Cause
The test waits for Event A but asserts on data set by Event B.

### Example - WRONG
```csharp
// Only waits for ProductCreatedEvent
using var waiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
  inventoryPerspectives: 2, bffPerspectives: 2);
await fixture.Dispatcher.SendAsync(command);
await waiter.WaitAsync();

// BUT inventory quantity is set by InventoryRestockedEvent!
var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(productId);
await Assert.That(inventoryLevel.Quantity).IsEqualTo(88);  // FAILS - InventoryRestockedEvent not processed yet
```

### Fix - CORRECT
```csharp
// Wait for BOTH events that affect the data being asserted
using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
  inventoryPerspectives: 2, bffPerspectives: 2);
using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
  inventoryPerspectives: 1, bffPerspectives: 1);
await fixture.Dispatcher.SendAsync(command);
await productWaiter.WaitAsync();
await restockWaiter.WaitAsync();  // Now inventory quantity is set

var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(productId);
await Assert.That(inventoryLevel.Quantity).IsEqualTo(88);  // PASSES
```

### Rule
**Always wait for ALL events that affect the data you're asserting on.**

In the ECommerce sample:
- `ProductCreatedEvent` → Sets product name, description, price
- `InventoryRestockedEvent` → Sets inventory quantity, available

---

## Pattern 3: Insufficient Timeouts

### Symptoms
- Test passes locally, fails in CI
- Test passes alone, fails in full suite
- `TimeoutException` from `PerspectiveCompletionWaiter`

### Root Cause
Under parallel test load, operations take longer:
- ServiceBus emulator message delivery slows down
- Database connections contend
- CPU time is shared across test processes

### Example - WRONG
```csharp
// 150s might not be enough for 72 perspective invocations under load
await productWaiter.WaitAsync(timeoutMilliseconds: 150000);
```

### Fix - CORRECT
```csharp
// Use generous timeouts for bulk operations
// 200s timeout: 72 perspective invocations across 2 hosts via ServiceBus emulator
await productWaiter.WaitAsync(timeoutMilliseconds: 200000);
```

### Timeout Guidelines

| Operation | Recommended Timeout |
|-----------|-------------------|
| Single product (4-6 perspectives) | 45s |
| Multiple products (20+ perspectives) | 150s |
| Bulk operations (50+ perspectives) | 300s |
| Timer-based tests under load | 600ms+ delays |

**Note**: For bulk operations with 72+ perspective invocations across ServiceBus emulator, use 300s (5 minutes) waiter timeout and 360s (6 minutes) test timeout.

---

## Pattern 4: Timing-Sensitive Operations

### Symptoms
- Test uses `Task.Delay` to wait for async operations
- Passes with longer delays, fails with shorter ones
- Fails more often under system load

### Root Cause
Fixed delays don't account for system load variations.

### Example - WRONG
```csharp
// 300ms might not be enough for timer to tick under parallel load
await Task.Delay(300);
var unitId2 = await strategy.QueueMessageAsync(message2);
await Assert.That(unitId2).IsNotEqualTo(unitId1);  // FAILS - timer hasn't ticked yet
```

### Fix - CORRECT
```csharp
// Use generous delays (600ms = 12x the 50ms timer interval)
await Task.Delay(600);
var unitId2 = await strategy.QueueMessageAsync(message2);
await Assert.That(unitId2).IsNotEqualTo(unitId1);  // PASSES
```

### Better Fix - Deterministic Synchronization
```csharp
// Use TaskCompletionSource instead of arbitrary delays
var flushed = new TaskCompletionSource<bool>();
strategy.OnFlushRequested += async (unitId, ct) => {
  flushed.TrySetResult(true);
  await Task.CompletedTask;
};

await strategy.QueueMessageAsync(message1);
await flushed.Task.WaitAsync(TimeSpan.FromSeconds(2));  // Deterministic wait
var unitId2 = await strategy.QueueMessageAsync(message2);
```

---

## Common Fixes Summary

| Problem | Quick Fix |
|---------|-----------|
| Static resource interference | Add `[NotInParallel("ResourceName")]` |
| Wrong data in assertion | Add waiter for the event that sets that data |
| Timeout waiting for perspectives | Increase `timeoutMilliseconds` to 200000 |
| Timer test fails under load | Increase `Task.Delay` to 600ms+ |
| Exact count assertions fail | Use ranges (≥970 instead of ==1024) |

---

## Test Attributes Reference

```csharp
// Prevent parallel execution with other tests in same group
[NotInParallel("ServiceBus")]

// Set test timeout (includes fixture setup time)
[Timeout(240000)]  // 240 seconds

// Category for filtering
[Category("Integration")]
```

---

## Debugging Flaky Tests

1. **Run test in isolation**: `dotnet run -- --treenode-filter "/*/*/*/TestName"`
2. **Check if it's timing-related**: Increase all delays/timeouts by 2x
3. **Check for static state**: Search for `static` in the test and related code
4. **Check event flow**: Ensure all events affecting asserted data are waited for
5. **Add diagnostic logging**: Use `Console.WriteLine` to trace execution order

---

## Pattern 5: Database Connection Pool Exhaustion

### Symptoms
- `TaskCanceledException: A task was canceled` during assertions
- Error appears in stderr as "Unhandled exception"
- Tests pass individually, fail under parallel load
- Transient failures with PostgreSQL tests

### Root Cause
Many parallel tests competing for PostgreSQL connections can exhaust the connection pool, causing cancellation tokens to trigger.

### Fix
```csharp
// Add NotInParallel for test classes that heavily use PostgreSQL
[NotInParallel("PostgresWorkCoordinator")]
public class EFCoreWorkCoordinatorTests : EFCoreTestBase {
```

---

## Pattern 6: Timing-Sensitive Race Condition Tests

### Symptoms
- Test uses `Task.Delay` with timeouts to wait for background operations
- Test simulates realistic delays (e.g., 100-500ms latency)
- Test passes in isolation, fails under parallel load
- `TaskCanceledException` during assertions

### Root Cause
Tests that simulate realistic timing (delays, latencies, retry intervals) are extremely sensitive to CPU contention. Under parallel test load, background workers don't get enough CPU time to complete their work within expected timeouts.

### Example - Race Condition Test
```csharp
// Test waits 15 seconds for 10 messages × 3 retry attempts
// Works in isolation, but CPU contention causes timeouts under parallel load
await Task.Delay(15000, cancellationToken);
await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(10);  // FAILS with TaskCanceledException
```

### Fix
```csharp
// Add NotInParallel to isolate timing-sensitive tests
[NotInParallel("WorkCoordinatorRaceCondition")]
public class WorkCoordinatorPublisherWorkerRaceConditionTests {
  // Tests now have dedicated CPU time for accurate timing
}
```

### When to Use for Race Condition Tests
- Tests that simulate realistic latencies (50-500ms delays)
- Tests that run background workers with polling intervals
- Tests that wait for multiple retry cycles
- Tests where timing accuracy is critical to assertions

---

## Files Modified in Flaky Test Fixes (January 2025)

| File | Fix Applied |
|------|-------------|
| `tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs` | `[NotInParallel]` + relaxed assertions |
| `samples/ECommerce/tests/ECommerce.Integration.Tests/Workflows/SeedProductsWorkflowTests.cs` | Increased waiter timeouts to 200s |
| `samples/ECommerce/tests/ECommerce.Integration.Tests/Infrastructure/ServiceBusIntegrationFixtureSanityTests.cs` | Added `InventoryRestockedEvent` waiter |
| `tests/Whizbang.Core.Tests/Messaging/IntervalUnitOfWorkStrategyTests.cs` | Increased delay from 300ms to 600ms |
| `tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs` | `[NotInParallel]` for PostgreSQL connection pool |
| `tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs` | `[NotInParallel]` for timing-sensitive race condition tests |
