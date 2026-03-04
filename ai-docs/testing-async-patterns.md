# Testing Async Patterns

This document provides guidance on writing reliable async tests in Whizbang, including available utilities, best practices, and forbidden patterns.

## Overview

Flaky tests typically stem from **time-based synchronization** instead of **event-driven verification**. This document helps you avoid common pitfalls and write tests that pass consistently.

---

## Available Test Utilities

### Whizbang.Testing.Async

**`AsyncTestHelpers`** - Generic async helpers for polling and negative testing.

```csharp
using Whizbang.Testing.Async;

// Wait for a condition to become true (with timeout)
await AsyncTestHelpers.WaitForConditionAsync(
    () => worker.ProcessedCount > 0,
    TimeSpan.FromSeconds(5),
    pollInterval: TimeSpan.FromMilliseconds(50));

// Assert that a condition remains false (negative test)
await AsyncTestHelpers.AssertNeverAsync(
    () => errorCount > 0,
    TimeSpan.FromMilliseconds(200),
    failureMessage: "Error occurred when it should not have");

// Wait for a value to match a predicate
var count = await AsyncTestHelpers.WaitForValueAsync(
    () => counter.Value,
    value => value >= 5,
    TimeSpan.FromSeconds(5));
```

### Whizbang.Testing.Transport

**`MessageAwaiter<T>`** - Wait for transport messages with filtering and timeout.

```csharp
var awaiter = new MessageAwaiter<OrderPlaced>(
    envelope => envelope.GetPayload<OrderPlaced>(),
    envelope => envelope.MessageType == "OrderPlaced");

// Register with transport subscription
var subscription = await transport.SubscribeAsync("topic", awaiter.Handler);

// Wait for message
var result = await awaiter.WaitAsync(TimeSpan.FromSeconds(10));
```

**`SubscriptionWarmup`** - Warm up transport subscriptions before tests.

```csharp
var (warmupAwaiter, testAwaiter) = SubscriptionWarmup.CreateDiscriminatingAwaiters<OrderPlaced>();
await SubscriptionWarmup.WarmupAsync(transport, topic, warmupAwaiter);
```

**`TransportTestHarness<T>`** - High-level harness for transport integration tests.

### Whizbang.Testing.Lifecycle

**`LifecycleStageAwaiter<T>`** - Wait for specific lifecycle stages to complete.

```csharp
var awaiter = LifecycleAwaiter.ForPerspectiveCompletion<OrderPlaced>(services);
await dispatcher.SendAsync(command);
await awaiter.WaitAsync(TimeSpan.FromSeconds(15));
```

**`PerspectiveCompletionWaiter<T>`** - Wait for perspectives across multiple hosts.

**`MultiHostPerspectiveAwaiter<T>`** - Flexible multi-host perspective waiting.

### Whizbang.Testing.Containers

**`SharedPostgresContainer`** - Shared PostgreSQL container with per-test database isolation.

```csharp
[Before(Test)]
public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDb = $"test_{Guid.NewGuid():N}";
    // Create unique database for this test
}
```

**`SharedRabbitMqContainer`** - Shared RabbitMQ container.

### Whizbang.Testing.Observability

**`InMemorySpanCollector`** - Collect OpenTelemetry spans for assertions.

```csharp
var collector = new InMemorySpanCollector();
// ... run code ...
var spans = collector.WithNamePrefix("Dispatcher.");
await Assert.That(spans).Count().IsGreaterThan(0);
```

---

## Best Practices

### DO: Use TaskCompletionSource with RunContinuationsAsynchronously

```csharp
// Prevents deadlocks from synchronous continuations
private readonly TaskCompletionSource<bool> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
```

### DO: Create fresh ServiceProvider per test

```csharp
[Test]
public async Task MyTest_ShouldWork_Async() {
    // Each test gets isolated DI container
    var services = new ServiceCollection();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    // ...
}
```

### DO: Use per-test database isolation

```csharp
[Before(Test)]
public async Task SetupAsync() {
    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await CreateDatabaseAsync(_testDatabaseName);
}

[After(Test)]
public async Task TeardownAsync() {
    await DropDatabaseAsync(_testDatabaseName);
}
```

### DO: Use WaitForConditionAsync for polling

```csharp
// Wait for condition with explicit timeout
await AsyncTestHelpers.WaitForConditionAsync(
    () => worker.CallCount >= 1,
    TimeSpan.FromSeconds(5));
```

### DO: Use AssertNeverAsync for negative tests

```csharp
// Prove something doesn't happen
await AsyncTestHelpers.AssertNeverAsync(
    () => errorHandler.WasCalled,
    TimeSpan.FromMilliseconds(200),
    failureMessage: "Error handler should not be called");
```

### DO: Register lifecycle receptors BEFORE sending commands

```csharp
// Register FIRST to avoid race condition
var awaiter = LifecycleAwaiter.ForPerspectiveCompletion<OrderPlaced>(services);
// THEN send command
await dispatcher.SendAsync(command);
// THEN wait
await awaiter.WaitAsync(TimeSpan.FromSeconds(15));
```

### DO: Use atomic ConcurrentDictionary operations

```csharp
// Use TryAdd instead of Contains + Add
if (_processedIds.TryAdd(messageId, true)) {
    // First time seeing this message
}
```

---

## Forbidden Patterns

### FORBIDDEN: Task.WhenAny with Task.Delay

```csharp
// WRONG - Race condition: delay can win under load
var received = await Task.WhenAny(signalReceived.Task, Task.Delay(1000)) == signalReceived.Task;
await Assert.That(received).IsTrue();
```

```csharp
// CORRECT - Use WaitAsync which throws on timeout
await signalReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
// If we get here, signal was received. Timeout throws exception.
```

### FORBIDDEN: Bare Task.Delay for synchronization

```csharp
// WRONG - Arbitrary timeout doesn't scale with system load
await Task.Delay(300);
await Assert.That(worker.CallCount).IsGreaterThan(0);
```

```csharp
// CORRECT - Wait for the actual condition
await AsyncTestHelpers.WaitForConditionAsync(
    () => worker.CallCount > 0,
    TimeSpan.FromSeconds(5),
    pollInterval: TimeSpan.FromMilliseconds(50));
```

### FORBIDDEN: Task.Delay for negative testing

```csharp
// WRONG - Cannot reliably prove something didn't happen with a delay
await Task.Delay(100);
await Assert.That(errorCount).IsEqualTo(0);
```

```csharp
// CORRECT - Actively poll to catch if condition becomes true
await AsyncTestHelpers.AssertNeverAsync(
    () => errorCount > 0,
    TimeSpan.FromMilliseconds(200),
    failureMessage: "Error count should remain zero");
```

### FORBIDDEN: Shared state between tests

```csharp
// WRONG - Tests can interfere with each other
private static int _globalCounter;

[Test]
public async Task Test1Async() {
    _globalCounter++;
    // ...
}
```

```csharp
// CORRECT - Use instance fields or per-test setup
private int _testCounter;

[Before(Test)]
public void Setup() {
    _testCounter = 0;
}
```

### FORBIDDEN: Contains + Add on ConcurrentDictionary

```csharp
// WRONG - Race condition between Contains and Add
if (!_dict.ContainsKey(key)) {
    _dict.Add(key, value);  // Another thread might have added it
}
```

```csharp
// CORRECT - Use atomic TryAdd
_dict.TryAdd(key, value);
```

---

## Test Project Classification

Test projects are classified by the `<WhizbangTestType>` property in their `.csproj`:

- **Unit** - Fast tests with no external infrastructure
- **Integration** - Tests requiring external resources (databases, message queues, containers)

Run tests by type:

```bash
# Run only unit tests (fast)
pwsh scripts/Run-Tests.ps1 -Mode AiUnit

# Run only integration tests
pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations

# Run all tests
pwsh scripts/Run-Tests.ps1 -Mode Ai
```

---

## Summary Table

| Scenario | Use This | Not This |
|----------|----------|----------|
| Wait for condition | `WaitForConditionAsync()` | `Task.Delay()` |
| Wait for signal | `tcs.Task.WaitAsync(timeout)` | `Task.WhenAny(tcs, Task.Delay)` |
| Prove nothing happens | `AssertNeverAsync()` | `Task.Delay() + Assert` |
| Transport messages | `MessageAwaiter<T>` | Manual polling |
| Lifecycle stages | `LifecycleStageAwaiter<T>` | Task.Delay between steps |
| Database tests | Per-test database name | Shared test database |
| Concurrent maps | `TryAdd()` | `ContainsKey() + Add()` |
| Completion signals | `TaskCreationOptions.RunContinuationsAsynchronously` | Default TaskCompletionSource |
