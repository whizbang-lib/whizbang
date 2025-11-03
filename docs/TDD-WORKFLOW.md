# TDD Workflow Guide

This guide explains Whizbang's **strict Test-Driven Development (TDD) workflow** for all new features.

## Overview

Whizbang follows a **test-first philosophy**:

> **Tests define behavior. Implementation follows.**

Every line of production code in Whizbang is written **only after** a failing test demands it.

---

## The RED → GREEN → REFACTOR Cycle

### Phase 1: RED (Write Failing Tests)

**Write tests that fail** - because the feature doesn't exist yet.

```csharp
// File: tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs

[Test]
public async Task HashPartitionRouter_ShouldRouteToSamePartitionForSameKey() {
    // Arrange
    var router = new HashPartitionRouter();  // ❌ Doesn't exist yet!
    var context = new PolicyContext(...);

    // Act
    var partition1 = router.SelectPartition("order-123", 16, context);
    var partition2 = router.SelectPartition("order-123", 16, context);

    // Assert
    await Assert.That(partition1).IsEqualTo(partition2);
}
```

**Run tests**: `dotnet test` → ❌ **Test fails** (HashPartitionRouter doesn't exist)

This is the **RED phase** - test proves the feature is missing.

### Phase 2: GREEN (Make Tests Pass)

**Write the minimum code** to make the test pass.

```csharp
// File: src/Whizbang.Core/Partitioning/HashPartitionRouter.cs

public class HashPartitionRouter : IPartitionRouter {
    public int SelectPartition(string streamKey, int partitionCount, PolicyContext context) {
        // Simple implementation to make test pass
        var hash = streamKey.GetHashCode();
        return Math.Abs(hash % partitionCount);
    }
}
```

**Run tests**: `dotnet test` → ✅ **Test passes**

This is the **GREEN phase** - test proves the feature works.

### Phase 3: REFACTOR (Clean Up Code)

**Improve the code** without changing behavior.

```csharp
// File: src/Whizbang.Core/Partitioning/HashPartitionRouter.cs

/// <summary>
/// Routes messages to partitions using consistent hashing based on stream key.
/// Uses FNV-1a hash algorithm for fast, well-distributed hashing.
/// </summary>
public class HashPartitionRouter : IPartitionRouter {
    /// <inheritdoc />
    public int SelectPartition(string streamKey, int partitionCount, PolicyContext context) {
        // Handle edge cases
        if (partitionCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(partitionCount), "Must be greater than zero");
        }

        if (partitionCount == 1) {
            return 0; // Only one partition available
        }

        if (string.IsNullOrEmpty(streamKey)) {
            return 0; // Route null/empty to partition 0
        }

        // Use FNV-1a hash for better distribution
        var hash = ComputeFnv1aHash(streamKey);
        var partition = Math.Abs(hash % partitionCount);

        return partition;
    }

    private static int ComputeFnv1aHash(string value) {
        const int FNV_PRIME = 16777619;
        const int FNV_OFFSET_BASIS = unchecked((int)2166136261);

        var hash = FNV_OFFSET_BASIS;
        var bytes = Encoding.UTF8.GetBytes(value);

        foreach (var b in bytes) {
            hash ^= b;
            hash *= FNV_PRIME;
        }

        return hash;
    }
}
```

**Run tests**: `dotnet test` → ✅ **Still passes**

**Format code**: `dotnet format` → Code meets style standards

This is the **REFACTOR phase** - test proves refactoring didn't break anything.

---

## Whizbang TDD Rules

### Rule 1: Tests Before Implementation

**❌ WRONG**:
```
1. Write implementation
2. Write tests to verify
```

**✅ CORRECT**:
```
1. Write failing tests
2. Write implementation to make them pass
```

### Rule 2: Write Minimal Implementation

Don't write features the tests don't demand:

```csharp
// ❌ WRONG: Over-engineering
public class HashPartitionRouter : IPartitionRouter {
    private readonly ILogger _logger;
    private readonly IMetrics _metrics;
    private readonly ConcurrentDictionary<string, int> _cache;

    public HashPartitionRouter(ILogger logger, IMetrics metrics) {
        _logger = logger;
        _metrics = metrics;
        _cache = new ConcurrentDictionary<string, int>();
    }

    public int SelectPartition(string streamKey, int partitionCount, PolicyContext context) {
        _logger.LogDebug("Selecting partition for {StreamKey}", streamKey);
        _metrics.Increment("partition.selections");

        // ... caching logic, etc.
    }
}
```

**Test didn't require** logger, metrics, or caching → Don't add them yet.

```csharp
// ✅ CORRECT: Minimal implementation
public class HashPartitionRouter : IPartitionRouter {
    public int SelectPartition(string streamKey, int partitionCount, PolicyContext context) {
        var hash = streamKey.GetHashCode();
        return Math.Abs(hash % partitionCount);
    }
}
```

**Add features only when tests require them.**

### Rule 3: One Test at a Time

Don't write all tests upfront. Write one test, make it pass, repeat:

```
✅ Write test 1 → Make it pass → Refactor
✅ Write test 2 → Make it pass → Refactor
✅ Write test 3 → Make it pass → Refactor
...
```

**Not**:

```
❌ Write tests 1-20 → Make them all pass
```

### Rule 4: Tests Define the API

Let tests drive API design:

```csharp
// Test reveals clean API
[Test]
public async Task SelectPartition_ShouldReturnValidPartition() {
    var router = new HashPartitionRouter();  // No dependencies needed

    var partition = router.SelectPartition("order-123", 16, context);

    await Assert.That(partition).IsGreaterThanOrEqualTo(0);
    await Assert.That(partition).IsLessThan(16);
}
```

**API emerges from usage** in tests, not from upfront design.

### Rule 5: All Tests Must Pass Always

**Never commit failing tests** (unless marked as `[Ignore]` with reason).

```bash
# Before every commit
dotnet test  # ✅ All tests must pass
dotnet format  # ✅ Code must be formatted
git commit
```

---

## TDD Workflow Example: SerialExecutor

Let's walk through implementing `SerialExecutor` using TDD.

### Step 1: Write First Test (RED)

```csharp
// File: tests/Whizbang.Execution.Tests/SerialExecutorTests.cs

[Test]
public async Task SerialExecutor_ShouldExecuteSingleMessage() {
    // Arrange
    var executor = new SerialExecutor();  // ❌ Doesn't exist yet
    await executor.StartAsync();

    var envelope = CreateTestEnvelope(new TestCommand());
    var context = new PolicyContext(envelope);
    var executed = false;

    // Act
    var result = await executor.ExecuteAsync(
        envelope,
        (env, ctx) => {
            executed = true;
            return Task.FromResult(true);
        },
        context
    );

    // Assert
    await Assert.That(executed).IsTrue();
    await Assert.That(result).IsTrue();
}
```

**Run**: `dotnet test` → ❌ **Fails** (SerialExecutor doesn't exist)

### Step 2: Minimal Implementation (GREEN)

```csharp
// File: src/Whizbang.Core/Execution/SerialExecutor.cs

public class SerialExecutor : IExecutionStrategy {
    public string Name => "Serial";

    public Task StartAsync(CancellationToken ct = default) {
        return Task.CompletedTask;
    }

    public Task<TResult> ExecuteAsync<TResult>(
        IMessageEnvelope envelope,
        Func<IMessageEnvelope, PolicyContext, Task<TResult>> handler,
        PolicyContext context,
        CancellationToken ct = default
    ) {
        // Minimal: just call the handler
        return handler(envelope, context);
    }

    public Task StopAsync(CancellationToken ct = default) {
        return Task.CompletedTask;
    }

    public Task DrainAsync(CancellationToken ct = default) {
        return Task.CompletedTask;
    }
}
```

**Run**: `dotnet test` → ✅ **Passes**

### Step 3: Write Next Test - Ordering (RED)

```csharp
[Test]
public async Task SerialExecutor_ShouldPreserveStrictOrder() {
    // Arrange
    var executor = new SerialExecutor();
    await executor.StartAsync();

    var executionOrder = new List<int>();

    // Act - Execute 3 messages
    var tasks = new List<Task>();

    for (int i = 0; i < 3; i++) {
        var index = i;
        var envelope = CreateTestEnvelope(new TestCommand());
        var context = new PolicyContext(envelope);

        tasks.Add(executor.ExecuteAsync(
            envelope,
            async (env, ctx) => {
                await Task.Delay(50 - (index * 10));  // Reverse delays (2 is fastest)
                executionOrder.Add(index);
                return true;
            },
            context
        ));
    }

    await Task.WhenAll(tasks);

    // Assert - Order should be 0, 1, 2 (not 2, 1, 0)
    await Assert.That(executionOrder).IsEquivalentTo(new[] { 0, 1, 2 });
}
```

**Run**: `dotnet test` → ❌ **Fails** (no ordering guarantee yet)

### Step 4: Add Queueing (GREEN)

```csharp
public class SerialExecutor : IExecutionStrategy {
    private readonly Channel<WorkItem> _channel;
    private Task? _workerTask;

    public SerialExecutor() {
        _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken ct = default) {
        _workerTask = Task.Run(() => ProcessWorkItemsAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        IMessageEnvelope envelope,
        Func<IMessageEnvelope, PolicyContext, Task<TResult>> handler,
        PolicyContext context,
        CancellationToken ct = default
    ) {
        var tcs = new TaskCompletionSource<TResult>();

        var workItem = new WorkItem(
            envelope,
            context,
            async () => {
                try {
                    var result = await handler(envelope, context);
                    tcs.SetResult(result);
                } catch (Exception ex) {
                    tcs.SetException(ex);
                }
            },
            ct
        );

        await _channel.Writer.WriteAsync(workItem, ct);
        return await tcs.Task;
    }

    private async Task ProcessWorkItemsAsync(CancellationToken ct) {
        await foreach (var workItem in _channel.Reader.ReadAllAsync(ct)) {
            if (!workItem.CancellationToken.IsCancellationRequested) {
                try {
                    await workItem.ExecuteAsync();
                } catch {
                    // Exceptions captured in TaskCompletionSource
                }
            }
        }
    }

    private record WorkItem(
        IMessageEnvelope Envelope,
        PolicyContext Context,
        Func<Task> ExecuteAsync,
        CancellationToken CancellationToken
    );
}
```

**Run**: `dotnet test` → ✅ **Passes**

### Step 5: Refactor (REFACTOR)

```csharp
// Add state management
private enum State { NotStarted, Running, Stopped }
private State _state = State.NotStarted;
private readonly object _stateLock = new();

public async Task<TResult> ExecuteAsync<TResult>(...) {
    lock (_stateLock) {
        if (_state != State.Running) {
            throw new InvalidOperationException("SerialExecutor is not running. Call StartAsync first.");
        }
    }

    // ... rest of implementation
}
```

**Run**: `dotnet test` → ✅ **Still passes**

**Format**: `dotnet format` → Code formatted

---

## Contract Tests

Whizbang uses **contract tests** to ensure all implementations of an interface behave correctly.

### Example: IPartitionRouter Contract Tests

```csharp
// File: tests/Whizbang.Partitioning.Tests/PartitionRouterContractTests.cs

public abstract class PartitionRouterContractTests {
    protected abstract IPartitionRouter CreateRouter();

    [Test]
    public async Task SelectPartition_ShouldReturnValidPartition() {
        var router = CreateRouter();
        var partition = router.SelectPartition("test-key", 16, _context);

        await Assert.That(partition).IsGreaterThanOrEqualTo(0);
        await Assert.That(partition).IsLessThan(16);
    }

    [Test]
    public async Task SelectPartition_ShouldBeConsistent() {
        var router = CreateRouter();

        var partition1 = router.SelectPartition("test-key", 16, _context);
        var partition2 = router.SelectPartition("test-key", 16, _context);

        await Assert.That(partition1).IsEqualTo(partition2);
    }

    // ... more contract tests
}
```

### Implementation Tests Inherit Contract Tests

```csharp
// File: tests/Whizbang.Partitioning.Tests/HashPartitionRouterTests.cs

public class HashPartitionRouterTests : PartitionRouterContractTests {
    protected override IPartitionRouter CreateRouter() {
        return new HashPartitionRouter();
    }

    // Implementation-specific tests
    [Test]
    public async Task HashPartitionRouter_ShouldDistributeEvenly() {
        // Test specific to hash router
    }
}
```

**Result**: HashPartitionRouter must pass both contract tests AND implementation-specific tests.

---

## Test Organization

### Test Project Structure

```plaintext
tests/
├── Whizbang.Core.Tests/
│   ├── ValueObjects/IdentityValueObjectTests.cs
│   └── ... other core tests
│
├── Whizbang.Policies.Tests/
│   ├── PolicyContextTests.cs
│   ├── PolicyDecisionTrailTests.cs
│   └── PolicyEngineTests.cs
│
├── Whizbang.Observability.Tests/
│   ├── MessageTracingTests.cs
│   ├── SecurityContextTests.cs
│   └── TraceStore/
│       ├── TraceStoreContractTests.cs
│       └── InMemoryTraceStoreTests.cs
│
├── Whizbang.Sequencing.Tests/
│   ├── SequenceProviderContractTests.cs
│   └── InMemorySequenceProviderTests.cs
│
├── Whizbang.Partitioning.Tests/
│   ├── PartitionRouterContractTests.cs
│   ├── HashPartitionRouterTests.cs
│   └── RoundRobinPartitionRouterTests.cs
│
└── Whizbang.Execution.Tests/
    ├── ExecutionStrategyContractTests.cs
    ├── SerialExecutorTests.cs
    └── ParallelExecutorTests.cs
```

### Test Naming Convention

```csharp
[Test]
public async Task ClassName_MethodOrScenario_ExpectedOutcomeAsync() {
    // Arrange
    // Act
    // Assert
}
```

**Examples**:

```csharp
HashPartitionRouter_SelectPartition_ReturnsValidPartitionAsync()
SerialExecutor_ExecuteAsync_PreservesOrderAsync()
PolicyEngine_MatchPolicy_ReturnsFirstMatchAsync()
TraceStore_GetByCorrelation_ReturnsAllMessagesInWorkflowAsync()
```

---

## Testing Best Practices

### 1. Arrange-Act-Assert

Always structure tests with clear sections:

```csharp
[Test]
public async Task Example_Test() {
    // Arrange - Set up test data
    var router = new HashPartitionRouter();
    var context = new PolicyContext(...);

    // Act - Execute the code under test
    var partition = router.SelectPartition("test-key", 16, context);

    // Assert - Verify the results
    await Assert.That(partition).IsGreaterThanOrEqualTo(0);
    await Assert.That(partition).IsLessThan(16);
}
```

### 2. One Assert Per Test (Generally)

Prefer focused tests with single assertions:

```csharp
// ✅ GOOD: Focused test
[Test]
public async Task SelectPartition_ShouldReturnValidPartition() {
    var partition = router.SelectPartition("key", 16, context);
    await Assert.That(partition).IsGreaterThanOrEqualTo(0);
}

[Test]
public async Task SelectPartition_ShouldBeConsistent() {
    var partition1 = router.SelectPartition("key", 16, context);
    var partition2 = router.SelectPartition("key", 16, context);
    await Assert.That(partition1).IsEqualTo(partition2);
}
```

**Exception**: Related assertions on the same object are fine:

```csharp
// ✅ ACCEPTABLE: Related assertions
[Test]
public async Task MessageEnvelope_ShouldHaveValidIdentity() {
    var envelope = CreateTestEnvelope();

    await Assert.That(envelope.MessageId).IsNotNull();
    await Assert.That(envelope.CorrelationId).IsNotNull();
    await Assert.That(envelope.CausationId).IsNotNull();
}
```

### 3. Test Edge Cases

Always test boundary conditions:

```csharp
[Test]
public async Task SelectPartition_WithOnePartition_ShouldReturnZero() {
    var partition = router.SelectPartition("key", 1, context);
    await Assert.That(partition).IsEqualTo(0);
}

[Test]
public async Task SelectPartition_WithEmptyKey_ShouldNotThrow() {
    var partition = router.SelectPartition("", 16, context);
    await Assert.That(partition).IsGreaterThanOrEqualTo(0);
}

[Test]
public async Task SelectPartition_WithInvalidPartitionCount_ShouldThrow() {
    await Assert.That(async () => {
        router.SelectPartition("key", 0, context);
    }).Throws<ArgumentOutOfRangeException>();
}
```

### 4. Use Helper Methods

Extract repetitive test setup:

```csharp
private static MessageEnvelope<TMessage> CreateTestEnvelope<TMessage>(TMessage message) {
    return new MessageEnvelope<TMessage> {
        MessageId = MessageId.New(),
        CorrelationId = CorrelationId.New(),
        CausationId = CausationId.From(MessageId.Empty),
        Payload = message,
        Hops = [
            new MessageHop {
                Type = HopType.Current,
                ServiceName = "TestService",
                MachineName = "test-machine",
                Timestamp = DateTimeOffset.UtcNow,
                ExecutionStrategy = "Test"
            }
        ]
    };
}
```

### 5. Avoid Test Interdependence

Each test should be **independent** and **self-contained**:

```csharp
// ❌ BAD: Tests depend on execution order
private static ISequenceProvider _sharedProvider;

[Test]
public async Task Test1_SetupProvider() {
    _sharedProvider = new InMemorySequenceProvider();
    await _sharedProvider.GetNextAsync("stream-1");  // 0
}

[Test]
public async Task Test2_UsesProvider() {
    // Fails if Test1 didn't run first!
    var seq = await _sharedProvider.GetNextAsync("stream-1");  // Expects 1
    await Assert.That(seq).IsEqualTo(1);
}
```

```csharp
// ✅ GOOD: Each test is independent
[Test]
public async Task Test1_GetNext_ReturnsZeroForNewStream() {
    var provider = new InMemorySequenceProvider();
    var seq = await provider.GetNextAsync("stream-1");
    await Assert.That(seq).IsEqualTo(0);
}

[Test]
public async Task Test2_GetNext_ReturnsIncrementedValue() {
    var provider = new InMemorySequenceProvider();
    await provider.GetNextAsync("stream-1");  // 0
    var seq = await provider.GetNextAsync("stream-1");  // 1
    await Assert.That(seq).IsEqualTo(1);
}
```

---

## Commands Reference

```bash
# Run all tests
dotnet test

# Run tests for specific project
dotnet test tests/Whizbang.Partitioning.Tests/

# Run specific test
dotnet test --filter "FullyQualifiedName~HashPartitionRouter_ShouldBeConsistent"

# Run tests with no build (faster iteration)
dotnet test --no-build

# Format code (ALWAYS run before commit)
dotnet format

# Full TDD cycle
dotnet clean && dotnet build && dotnet test && dotnet format
```

---

## Summary

Whizbang's TDD workflow:

1. **RED**: Write failing tests first
2. **GREEN**: Write minimal code to pass tests
3. **REFACTOR**: Clean up code, tests still pass
4. **FORMAT**: Run `dotnet format` always

**Key Principles**:
- Tests before implementation
- One test at a time
- Minimal implementation
- Tests define API
- All tests pass always
- Contract tests for interfaces
- Independent, self-contained tests

This approach ensures **high quality, well-designed, thoroughly tested code** throughout the Whizbang codebase.
