# Policy Engine v0.2.0: Stream-Based Execution with Signals & Semaphores

## Status
- **Phase**: Planning Complete → Implementation Starting
- **Started**: 2025-11-02
- **Target Release**: v0.2.0
- **Owner**: Phil Carbone

## Progress Tracking

### Phase 1: Plan Documentation ✅
- [x] Plan approved
- [x] Plan document created
- [ ] Infrastructure mapping table documented

### Phase 2: Core Abstractions (TDD) 📋
- [ ] PolicyContext interface and tests
- [ ] PolicyDecisionTrail implementation and tests
- [ ] MessageEnvelope with caller info
- [ ] MessageHop with caller attributes
- [ ] MessageTracing helpers

### Phase 3: Sequence Provider (TDD) 📋
- [ ] ISequenceProvider interface
- [ ] Contract tests for all providers
- [ ] InMemorySequenceProvider implementation
- [ ] Monotonicity and thread-safety tests

### Phase 4: Partition Router (TDD) 📋
- [ ] IPartitionRouter interface
- [ ] Contract tests for all routers
- [ ] HashPartitionRouter implementation
- [ ] RoundRobinPartitionRouter implementation

### Phase 5: Execution Strategies (TDD) 📋
- [ ] IExecutionStrategy interface
- [ ] SerialExecutor with ordering tests
- [ ] ParallelExecutor with concurrency tests
- [ ] Lifecycle tests (Start/Stop/Drain)
- [ ] CancellationToken propagation tests

### Phase 6: Policy Engine (TDD) 📋
- [ ] PolicyEngine implementation
- [ ] PolicyBuilder fluent API
- [ ] Policy matching tests
- [ ] Context propagation tests
- [ ] Decision trail recording tests

### Phase 7: Trace Store (TDD) 📋
- [ ] ITraceStore interface
- [ ] InMemoryTraceStore implementation
- [ ] Trace storage and retrieval tests
- [ ] Correlation query tests
- [ ] Causal chain query tests

### Phase 8: Benchmarks 📋
- [ ] Benchmark project setup
- [ ] Executor benchmarks
- [ ] Sequence provider benchmarks
- [ ] Partition router benchmarks
- [ ] Policy evaluation benchmarks
- [ ] Trace capture overhead benchmarks

### Phase 9: Documentation 📋
- [ ] Infrastructure mapping guide
- [ ] VSCode extension data guide
- [ ] Time-travel debugging guide
- [ ] Policy authoring guide
- [ ] TDD workflow guide
- [ ] Caller info capture guide

---

## Executive Summary

Implement Policy Engine with **stream-based message routing** using **strict TDD for all components**. Includes pluggable partition routing, sequence providers, comprehensive observability with caller-info tracking, and complete test coverage for every interface and implementation.

---

## Core Principle: TDD for Everything

**RED → GREEN → REFACTOR** for every component:
- Policies: Tests first, then implementation
- Execution strategies: Tests first, then implementation
- Trace store: Tests first, then implementation
- Decision trails: Tests first, then implementation
- Sequence providers: Tests first, then implementation
- Partition routers: Tests first, then implementation

**No code without tests first.**

---

## Architecture Overview

### Three-Layer Hierarchy

```
TOPIC (Policy-Driven Routing)
  ↓ Policies determine which topic based on message context
STREAM (Primary Abstraction - Ordering + Execution)
  ↓ Stream key determines which stream (e.g., aggregate ID)
PARTITION (Implementation Detail - Physical Parallelism)
  ↓ Internal sharding for scale (e.g., stream has 3 partitions)
```

### Infrastructure Mapping Table

| Whizbang Concept | Kafka/EventHub | RabbitMQ | Service Bus | Event Store |
|------------------|----------------|----------|-------------|-------------|
| **Topic** | Topic | Exchange | Topic | $category-{name} |
| **Stream** | Partition key | Routing key | SessionId | Stream ID |
| **Partition** | Partition (0..N) | Queue (1 per route) | Subscription | Subscription |
| **Sequence** | Offset | Delivery tag | SequenceNumber | EventNumber |
| **Ordering** | Per partition | Per queue | Per session | Per stream |
| **Policy Context** | Message headers | Message properties | User properties | Event metadata |
| **Trace** | W3C headers | Headers exchange | Custom properties | Metadata |
| **Caller Info** | Custom header | Custom property | Custom property | Metadata field |

---

## Message Envelope with Caller Information

```csharp
public class MessageEnvelope<TMessage> {
    // Identity & Causality
    public MessageId MessageId { get; init; }
    public CorrelationId CorrelationId { get; init; }
    public CausationId CausationId { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    // Actual message payload
    public TMessage Payload { get; init; }

    // Routing metadata
    public string Topic { get; init; }
    public string StreamKey { get; init; }
    public int? PartitionIndex { get; init; }
    public long? SequenceNumber { get; init; }

    // Policy decision trail (for debugging/time-travel)
    public PolicyDecisionTrail PolicyTrail { get; init; }

    // User context
    public string? UserId { get; init; }
    public string? TenantId { get; init; }

    // Custom metadata
    public IReadOnlyDictionary<string, object> Metadata { get; init; }
}
```

### MessageHop with Caller Information

```csharp
public record MessageHop {
    // Service/Machine identity
    public string ServiceName { get; init; }
    public string MachineName { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    // Routing information
    public string Topic { get; init; }
    public string StreamKey { get; init; }
    public int? PartitionIndex { get; init; }
    public long? SequenceNumber { get; init; }
    public string ExecutionStrategy { get; init; }

    // Caller information (for VSCode extension - jump to line)
    public string? CallerMemberName { get; init; }
    public string? CallerFilePath { get; init; }
    public int? CallerLineNumber { get; init; }

    // Performance
    public TimeSpan Duration { get; init; }
}
```

### Caller Information Capture

Use C# magic attributes to automatically capture call site:

```csharp
public static class MessageTracing {
    public static MessageHop RecordHop(
        string topic,
        string streamKey,
        string executionStrategy,
        [CallerMemberName] string? callerMemberName = null,
        [CallerFilePath] string? callerFilePath = null,
        [CallerLineNumber] int? callerLineNumber = null
    ) {
        return new MessageHop {
            ServiceName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown",
            MachineName = Environment.MachineName,
            Timestamp = DateTimeOffset.UtcNow,
            Topic = topic,
            StreamKey = streamKey,
            ExecutionStrategy = executionStrategy,
            CallerMemberName = callerMemberName,
            CallerFilePath = callerFilePath,
            CallerLineNumber = callerLineNumber
        };
    }
}

// Usage (automatic call site capture):
public class SerialExecutor : IExecutionStrategy {
    public async Task<TResult> ExecuteAsync<TResult>(...) {
        // Automatically captures: method="ExecuteAsync", file="SerialExecutor.cs", line=45
        var hop = MessageTracing.RecordHop(
            envelope.Topic,
            envelope.StreamKey,
            this.Name
        );

        envelope.PolicyTrail.AddHop(hop);

        // ... execute handler
    }
}
```

---

## Policy Context (Universal Context)

Flows through entire execution pipeline, accessible to internal code and user code:

```csharp
public class PolicyContext {
    // Message information
    public IMessage Message { get; init; }
    public Type MessageType { get; init; }
    public MessageEnvelope Envelope { get; init; }

    // Runtime context
    public string Environment { get; init; }
    public DateTimeOffset ExecutionTime { get; init; }
    public IServiceProvider Services { get; init; }

    // Policy evaluation state
    public PolicyDecisionTrail Trail { get; }  // Mutable - records decisions

    // User helpers (for policy expressions)
    public bool MatchesAggregate<T>() { }
    public bool HasTag(string tag) { }
    public bool HasFlag(WhizbangFlags flag) { }

    // Context enrichment (for user receptors)
    public T GetService<T>() { }
    public object? GetMetadata(string key) { }
}
```

---

## Policy Decision Trail (Debugging/Time-Travel)

Records every policy decision for observability:

```csharp
public class PolicyDecisionTrail {
    public List<PolicyDecision> Decisions { get; } = new();

    public void RecordDecision(
        string policyName,
        string rule,
        bool matched,
        object? configuration,
        string reason
    ) {
        Decisions.Add(new PolicyDecision {
            PolicyName = policyName,
            Rule = rule,
            Matched = matched,
            Configuration = configuration,
            Reason = reason,
            Timestamp = DateTimeOffset.UtcNow
        });
    }
}

public record PolicyDecision {
    public string PolicyName { get; init; }
    public string Rule { get; init; }
    public bool Matched { get; init; }
    public object? Configuration { get; init; }
    public string Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

---

## Pluggable Abstractions

### 1. Partition Router Interface

```csharp
public interface IPartitionRouter {
    /// <summary>
    /// Determines which partition (0 to partitionCount-1) for a given stream key
    /// </summary>
    int SelectPartition(string streamKey, int partitionCount, PolicyContext context);
}
```

**Implementations:**
- `HashPartitionRouter` - Consistent hashing (default)
- `RoundRobinPartitionRouter` - Load balancing
- `RangePartitionRouter` - Key ranges (future)
- `CustomPartitionRouter` - User-defined logic (future)

### 2. Sequence Provider Interface

```csharp
public interface ISequenceProvider {
    /// <summary>
    /// Gets next sequence number for a stream
    /// Must be monotonically increasing and thread-safe
    /// </summary>
    Task<long> GetNextAsync(string streamKey, CancellationToken ct = default);

    /// <summary>
    /// Gets current sequence for a stream (without incrementing)
    /// </summary>
    Task<long> GetCurrentAsync(string streamKey, CancellationToken ct = default);

    /// <summary>
    /// Resets sequence for a stream (dangerous - for testing/admin only)
    /// </summary>
    Task ResetAsync(string streamKey, long newValue = 0, CancellationToken ct = default);
}
```

**Implementations:**
- `InMemorySequenceProvider` - ConcurrentDictionary + Interlocked (v0.2.0)
- `SqlSequenceProvider` - Database sequence/identity (v0.3.0)
- `RedisSequenceProvider` - INCR command (v0.4.0)
- `HybridLogicalClockProvider` - Physical + logical time (v0.4.0)

### 3. Execution Strategy Interface

```csharp
public interface IExecutionStrategy {
    string Name { get; }

    Task<TResult> ExecuteAsync<TResult>(
        MessageEnvelope<IMessage> envelope,
        Func<MessageEnvelope<IMessage>, PolicyContext, Task<TResult>> handler,
        PolicyContext context,
        CancellationToken ct
    );

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task DrainAsync(CancellationToken ct);

    IObservable<ExecutionMetrics> Metrics { get; }
}
```

**Implementations:**
- `SerialExecutor` - Strict FIFO ordering (v0.2.0)
- `ParallelExecutor` - No ordering, concurrent execution (v0.2.0)
- `SequencedParallelExecutor` - Parallel execution with ordered commits (v0.3.0)

### 4. Trace Store Interface

```csharp
public interface ITraceStore {
    // Store complete message trace
    Task StoreAsync(MessageTrace trace, CancellationToken ct = default);

    // Query by message ID
    Task<MessageTrace?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default);

    // Query by correlation (get all messages in workflow)
    Task<List<MessageTrace>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default);

    // Query by causation (get parent/child chain)
    Task<List<MessageTrace>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default);

    // Query by time range
    Task<List<MessageTrace>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    // Get policy decisions for a message
    Task<List<PolicyDecisionTrail>> GetPolicyTrailAsync(MessageId messageId, CancellationToken ct = default);
}
```

**Implementations:**
- `InMemoryTraceStore` - v0.2.0 (testing)
- `SqlTraceStore` - v0.3.0 (persistent)
- `ElasticsearchTraceStore` - v0.4.0 (production observability)

---

## Policy-Driven Configuration

Policies drive **everything** - routing, execution, sequencing, partitioning:

```csharp
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config
            // Topic & Stream
            .UseTopic("orders")
            .UseStream(ctx => $"order-{ctx.GetAggregateId()}")

            // Execution
            .UseStrategy<SerialExecutor>()
            .WithConcurrency(1)

            // Partitioning
            .WithPartitions(4)
            .UsePartitionRouter<HashPartitionRouter>()

            // Sequencing
            .UseSequenceProvider<InMemorySequenceProvider>()

            // Concurrency Control
            .UseBulkhead("order-processing", maxConcurrency: 10, maxQueue: 50)

            // Timeouts
            .UseTimeout(TimeSpan.FromSeconds(30))
        );

policies.When(ctx => ctx.Message is IProjection)
        .Then(config => config
            .UseTopic("projections")
            .UseStream("projections-shared")
            .UseStrategy<ParallelExecutor>()
            .WithConcurrency(20)
            .WithPartitions(8)
            .UsePartitionRouter<RoundRobinPartitionRouter>()
        );
```

---

## TDD Implementation Phases

### Phase 1: Core Abstractions (Tests First)
1. Write PolicyContext tests → Implement PolicyContext
2. Write PolicyDecisionTrail tests → Implement PolicyDecisionTrail
3. Write MessageEnvelope tests → Implement MessageEnvelope
4. Write MessageTracing tests → Implement MessageTracing

### Phase 2: Sequence Provider (Tests First)
1. Write ISequenceProvider contract tests
2. Write InMemorySequenceProvider tests → Implement
3. Write monotonicity tests → Verify implementation

### Phase 3: Partition Router (Tests First)
1. Write IPartitionRouter contract tests
2. Write HashPartitionRouter tests → Implement
3. Write RoundRobinPartitionRouter tests → Implement

### Phase 4: Execution Strategies (Tests First)
1. Write SerialExecutor ordering tests → Implement
2. Write ParallelExecutor concurrency tests → Implement
3. Write lifecycle tests → Implement Start/Stop/Drain

### Phase 5: Policy Engine (Tests First)
1. Write PolicyEngine tests → Implement
2. Write PolicyBuilder tests → Implement
3. Write end-to-end integration tests

### Phase 6: Trace Store (Tests First)
1. Write ITraceStore tests
2. Write InMemoryTraceStore tests → Implement
3. Write query tests → Verify all query methods

### Phase 7: Benchmarks
1. Setup benchmark project
2. Create executor benchmarks
3. Create sequence provider benchmarks
4. Create partition router benchmarks
5. Create policy evaluation benchmarks
6. Create trace capture overhead benchmarks

### Phase 8: Documentation
1. Infrastructure mapping guide
2. VSCode extension data guide
3. Time-travel debugging guide
4. Policy authoring guide
5. TDD workflow guide

---

## File Structure

### Tests (ALL written before implementation)

```
whizbang/tests/
├── Whizbang.Policies.Tests/
│   ├── PolicyContextTests.cs
│   ├── PolicyDecisionTrailTests.cs
│   ├── PolicyEngineTests.cs
│   └── PolicyBuilderTests.cs
│
├── Whizbang.Observability.Tests/
│   ├── MessageTraceTests.cs
│   ├── TraceStoreTests.cs
│   ├── EnvelopeSerializationTests.cs
│   └── CallerInfoCaptureTests.cs
│
├── Whizbang.Sequencing.Tests/
│   ├── SequenceProviderContractTests.cs
│   ├── InMemorySequenceProviderTests.cs
│   └── MonotonicityTests.cs
│
├── Whizbang.Partitioning.Tests/
│   ├── PartitionRouterContractTests.cs
│   ├── HashPartitionRouterTests.cs
│   └── RoundRobinPartitionRouterTests.cs
│
├── Whizbang.Execution.Tests/
│   ├── SerialExecutorOrderingTests.cs
│   ├── ParallelExecutorTests.cs
│   ├── LifecycleTests.cs
│   └── CancellationTests.cs
│
├── Whizbang.Streams.Tests/
│   ├── StreamRegistryTests.cs
│   ├── StreamRoutingTests.cs
│   └── StreamLifecycleTests.cs
│
└── Whizbang.Benchmarks/
    ├── ExecutorBenchmarks.cs
    ├── SequenceProviderBenchmarks.cs
    ├── PartitionRouterBenchmarks.cs
    ├── PolicyEvaluationBenchmarks.cs
    └── TraceCaptureOverheadBenchmarks.cs
```

### Implementation (ALL written after tests)

```
whizbang/src/Whizbang.Core/
├── Policies/
│   ├── PolicyContext.cs
│   ├── PolicyDecisionTrail.cs
│   ├── PolicyEngine.cs
│   └── PolicyBuilder.cs
│
├── Observability/
│   ├── MessageEnvelope.cs
│   ├── MessageTrace.cs
│   ├── MessageHop.cs
│   ├── MessageTracing.cs
│   ├── ITraceStore.cs
│   └── InMemoryTraceStore.cs
│
├── Sequencing/
│   ├── ISequenceProvider.cs
│   └── InMemorySequenceProvider.cs
│
├── Partitioning/
│   ├── IPartitionRouter.cs
│   ├── HashPartitionRouter.cs
│   └── RoundRobinPartitionRouter.cs
│
├── Execution/
│   ├── IExecutionStrategy.cs
│   ├── SerialExecutor.cs
│   └── ParallelExecutor.cs
│
└── Streaming/
    ├── IStream.cs
    ├── InMemoryStream.cs
    └── StreamRegistry.cs
```

---

## Success Criteria

### Test Coverage
- ✅ 100% coverage for all core abstractions
- ✅ Contract tests for all interfaces
- ✅ Integration tests for end-to-end flows
- ✅ All tests pass before ANY implementation is considered complete

### Functional (Verified by Tests)
- ✅ Sequence providers guarantee monotonicity under concurrency
- ✅ Serial executor preserves exact FIFO order (verified by sequence numbers)
- ✅ Policy context flows through entire pipeline
- ✅ Decision trails capture all policy decisions
- ✅ Caller information captured correctly (method, file, line)
- ✅ Message traces include complete hop chain with caller info

### Performance (Benchmarks)
- ✅ Caller info capture: <5μs overhead
- ✅ Partition routing: <5μs per message
- ✅ Sequence generation: <10μs (in-memory)
- ✅ Policy evaluation: <10μs per message
- ✅ Trace capture: <20μs overhead

### Documentation
- ✅ Plan document in /plans folder
- ✅ Infrastructure mapping table
- ✅ VSCode extension data guide
- ✅ TDD workflow guide
- ✅ Caller info capture guide

---

## Design Decisions Log

### 2025-11-02: Initial Architecture Decisions

1. **Stream as Primary Abstraction** - Partition is implementation detail
   - **Rationale**: Aligns with Kafka/EventHub and Service Bus models
   - **Impact**: Simplifies API, users think in streams not partitions

2. **Policy-Driven Topic Routing** - No convention-based routing
   - **Rationale**: Maximum flexibility, supports complex scenarios
   - **Impact**: More configuration but explicit and debuggable

3. **Caller Info via Magic Attributes** - [CallerMemberName], [CallerFilePath], [CallerLineNumber]
   - **Rationale**: Zero overhead, automatic capture, enables VSCode extension
   - **Impact**: Every hop records exact call site for debugging

4. **TDD for All Components** - No exceptions
   - **Rationale**: Quality, documentation, design-by-contract
   - **Impact**: More upfront work but higher confidence

5. **Semaphores + Signals** - Not just semaphores alone
   - **Rationale**: Semaphores for "how many", signals for "when"
   - **Impact**: More complex but more powerful coordination

---

## Open Questions

None currently - all architectural questions resolved in planning phase.

---

## References

### Related Documentation
- Concurrency Control Proposal (whizbang-lib.github.io)
- Sequence Leasing Architecture (whizbang-lib.github.io)
- Policy Pattern Guide (whizbang-lib.github.io)

### External References
- Kafka Partitioning: https://kafka.apache.org/documentation/#intro_topics
- Azure Service Bus Sessions: https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions
- W3C Trace Context: https://www.w3.org/TR/trace-context/
- Caller Info Attributes: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/attributes/caller-information

---

## Notes

- This plan will be updated as implementation progresses
- Decision log will track any architectural changes
- All test results and benchmark numbers will be added to this document
- VSCode extension is NOT part of v0.2.0 - we're just capturing the data it will need
