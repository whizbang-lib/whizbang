# Policy Authoring Guide

This guide explains how to write **policies** for routing and configuring message processing in Whizbang.

## Overview

Policies drive **everything** in Whizbang:
- **Routing**: Which topic and stream?
- **Execution**: Serial or parallel?
- **Partitioning**: How many partitions? Which router?
- **Sequencing**: Which sequence provider?
- **Concurrency Control**: Bulkheads, timeouts, retries (future)

**No convention-based routing** - policies make all decisions explicit and debuggable.

---

## Policy Anatomy

A policy consists of two parts:

### 1. Predicate (When)

A function that returns `true` if the policy matches:

```csharp
policies.When(ctx => ctx.MatchesAggregate<Order>())
```

### 2. Configuration (Then)

Fluent API that configures routing and execution:

```csharp
.Then(config => config
    .UseTopic("orders")
    .UseStream(ctx => $"order-{ctx.GetAggregateId()}")
    .WithPartitions(16)
    .UseStrategy<SerialExecutor>()
)
```

---

## Complete Example

```csharp
using Whizbang.Core.Policies;

public class OrderPolicies {
    public static void Configure(PolicyBuilder policies) {
        // Policy 1: Order aggregate messages
        policies.When(ctx => ctx.MatchesAggregate<Order>())
                .Then(config => config
                    .UseTopic("orders")
                    .UseStream(ctx => $"order-{ctx.GetAggregateId()}")
                    .WithPartitions(16)
                    .UsePartitionRouter<HashPartitionRouter>()
                    .UseExecutionStrategy<SerialExecutor>()
                    .UseSequenceProvider<InMemorySequenceProvider>()
                );

        // Policy 2: Projection messages (read models)
        policies.When(ctx => ctx.Message is IProjection)
                .Then(config => config
                    .UseTopic("projections")
                    .UseStream("projections-shared")
                    .WithPartitions(8)
                    .UsePartitionRouter<RoundRobinPartitionRouter>()
                    .UseExecutionStrategy<ParallelExecutor>()
                    .WithConcurrency(20)
                );

        // Policy 3: High-priority messages
        policies.When(ctx => ctx.HasTag("priority:high"))
                .Then(config => config
                    .UseTopic("high-priority")
                    .UseStream(ctx => $"priority-{ctx.MessageType.Name}")
                    .WithPartitions(32)
                    .UseExecutionStrategy<ParallelExecutor>()
                    .WithConcurrency(50)
                );

        // Default policy (catches everything else)
        policies.When(ctx => true)
                .Then(config => config
                    .UseTopic("default")
                    .UseStream("default-stream")
                    .WithPartitions(4)
                    .UseExecutionStrategy<SerialExecutor>()
                );
    }
}
```

---

## PolicyContext API

The `PolicyContext` provides access to message information and helper methods.

### Message Information

```csharp
// Access the message
ctx.Message              // IMessage (the actual message instance)
ctx.MessageType          // Type (message type)
ctx.Envelope             // MessageEnvelope (wrapper with metadata)

// Runtime context
ctx.Environment          // string (e.g., "Production", "Staging")
ctx.ExecutionTime        // DateTimeOffset (when policy is evaluated)
ctx.Services             // IServiceProvider (DI container)
```

### Helper Methods

#### MatchesAggregate<T>()

Check if message is for a specific aggregate type:

```csharp
policies.When(ctx => ctx.MatchesAggregate<Order>())
```

**Typical use**: Route all messages for an aggregate to the same topic/stream.

#### GetAggregateId()

Extract aggregate ID from message (if it implements `IAggregateMessage`):

```csharp
.UseStream(ctx => $"order-{ctx.GetAggregateId()}")
```

**Typical use**: Create stream key from aggregate ID.

#### HasTag(string tag)

Check if envelope metadata contains a tag:

```csharp
policies.When(ctx => ctx.HasTag("priority:high"))
```

**Typical use**: Route tagged messages differently.

#### HasFlag(WhizbangFlags flag)

Check if envelope has a specific flag (enum):

```csharp
policies.When(ctx => ctx.HasFlag(WhizbangFlags.Replay))
```

**Typical use**: Special handling for replayed messages.

#### GetMetadata(string key)

Get metadata value from envelope:

```csharp
policies.When(ctx => {
    var region = ctx.GetMetadata("region");
    return region?.ToString() == "us-west";
})
```

**Typical use**: Route based on custom metadata.

#### GetService<T>()

Get service from DI container:

```csharp
.Then(config => {
    var logger = ctx.GetService<ILogger>();
    logger.LogInformation("Routing to orders topic");
    return config.UseTopic("orders");
})
```

**Typical use**: Access services for complex routing logic.

---

## PolicyConfiguration API

The fluent configuration API for specifying routing and execution.

### Routing Configuration

#### UseTopic(string topic)

Set the logical routing destination:

```csharp
.UseTopic("orders")
```

**Maps to**:
- Kafka: Topic name
- Service Bus: Topic name
- RabbitMQ: Exchange name
- Event Store: Category ($category-{name})

#### UseStream(string streamKey)

Set the stream key (ordering boundary):

```csharp
.UseStream("order-12345")
```

**Or use a function**:

```csharp
.UseStream(ctx => $"order-{ctx.GetAggregateId()}")
```

**Maps to**:
- Kafka: Partition key
- Service Bus: SessionId
- RabbitMQ: Routing key
- Event Store: Stream ID

### Partitioning Configuration

#### WithPartitions(int count)

Set number of partitions:

```csharp
.WithPartitions(16)
```

**Typical values**:
- Low volume: 4-8 partitions
- Medium volume: 16-32 partitions
- High volume: 64-128 partitions

#### UsePartitionRouter<TRouter>()

Set the partition router implementation:

```csharp
.UsePartitionRouter<HashPartitionRouter>()    // Consistent hashing (default)
.UsePartitionRouter<RoundRobinPartitionRouter>()  // Load balancing
```

**HashPartitionRouter** (recommended):
- Same stream key always goes to same partition
- Preserves ordering per stream
- Good distribution

**RoundRobinPartitionRouter** (for parallelism):
- Distributes evenly across partitions
- No ordering guarantees
- Maximum throughput

### Execution Configuration

#### UseExecutionStrategy<TStrategy>()

Set the execution strategy:

```csharp
.UseExecutionStrategy<SerialExecutor>()    // FIFO ordering
.UseExecutionStrategy<ParallelExecutor>()  // No ordering, concurrent
```

**SerialExecutor**:
- Strict FIFO ordering
- One message at a time per partition
- Use for: Aggregates, stateful processing

**ParallelExecutor**:
- No ordering guarantees
- Concurrent execution (configurable limit)
- Use for: Projections, read models, idempotent handlers

#### WithConcurrency(int maxConcurrency)

Set max concurrent executions (for ParallelExecutor):

```csharp
.UseExecutionStrategy<ParallelExecutor>()
.WithConcurrency(20)  // Max 20 concurrent handlers
```

**Typical values**:
- CPU-bound work: 2x CPU cores
- I/O-bound work: 20-50 concurrent
- Database writes: 10-20 concurrent

### Sequencing Configuration

#### UseSequenceProvider<TProvider>()

Set the sequence provider implementation:

```csharp
.UseSequenceProvider<InMemorySequenceProvider>()  // v0.2.0
.UseSequenceProvider<SqlSequenceProvider>()       // v0.3.0 (future)
.UseSequenceProvider<RedisSequenceProvider>()     // v0.4.0 (future)
```

**Note**: Sequence numbers are monotonically increasing per stream.

---

## Policy Matching Order

Policies are evaluated **in the order they are defined**. The **first match wins**.

### Example: Order Matters

```csharp
// ✅ CORRECT: Specific policies first
policies.When(ctx => ctx.HasTag("priority:high"))
        .Then(config => config.UseTopic("high-priority"));

policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config.UseTopic("orders"));

policies.When(ctx => true)  // Default (last)
        .Then(config => config.UseTopic("default"));

// ❌ WRONG: Default policy first (would match everything)
policies.When(ctx => true)  // Matches ALL messages - subsequent policies never evaluated!
        .Then(config => config.UseTopic("default"));

policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config.UseTopic("orders"));  // Never reached!
```

**Best Practice**: Order policies from **most specific to least specific**.

---

## Common Policy Patterns

### Pattern 1: Aggregate-Based Routing

Route all messages for an aggregate type to dedicated topic/stream:

```csharp
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config
            .UseTopic("orders")
            .UseStream(ctx => $"order-{ctx.GetAggregateId()}")
            .UseExecutionStrategy<SerialExecutor>()
        );

policies.When(ctx => ctx.MatchesAggregate<Customer>())
        .Then(config => config
            .UseTopic("customers")
            .UseStream(ctx => $"customer-{ctx.GetAggregateId()}")
            .UseExecutionStrategy<SerialExecutor>()
        );
```

**Use when**: Event sourcing, CQRS with aggregates.

### Pattern 2: Message Type Routing

Route by message base type or interface:

```csharp
policies.When(ctx => ctx.Message is ICommand)
        .Then(config => config
            .UseTopic("commands")
            .UseExecutionStrategy<SerialExecutor>()
        );

policies.When(ctx => ctx.Message is IEvent)
        .Then(config => config
            .UseTopic("events")
            .UseExecutionStrategy<ParallelExecutor>()
        );

policies.When(ctx => ctx.Message is IProjection)
        .Then(config => config
            .UseTopic("projections")
            .UseExecutionStrategy<ParallelExecutor>()
            .WithConcurrency(20)
        );
```

**Use when**: Separating command/event/query processing.

### Pattern 3: Tag-Based Routing

Route based on metadata tags:

```csharp
policies.When(ctx => ctx.HasTag("priority:critical"))
        .Then(config => config
            .UseTopic("critical")
            .WithPartitions(64)
            .WithConcurrency(100)
        );

policies.When(ctx => ctx.HasTag("priority:high"))
        .Then(config => config
            .UseTopic("high-priority")
            .WithPartitions(32)
        );

policies.When(ctx => ctx.HasTag("priority:low"))
        .Then(config => config
            .UseTopic("low-priority")
            .WithPartitions(4)
        );
```

**Use when**: Priority-based processing, SLA differentiation.

### Pattern 4: Environment-Based Routing

Route differently per environment:

```csharp
policies.When(ctx => ctx.Environment == "Production")
        .Then(config => config
            .UseTopic("orders-prod")
            .WithPartitions(64)
            .UsePartitionRouter<HashPartitionRouter>()
        );

policies.When(ctx => ctx.Environment == "Staging")
        .Then(config => config
            .UseTopic("orders-staging")
            .WithPartitions(8)
        );

policies.When(ctx => ctx.Environment == "Development")
        .Then(config => config
            .UseTopic("orders-dev")
            .WithPartitions(1)
        );
```

**Use when**: Multi-environment deployments.

### Pattern 5: Tenant-Based Routing

Route based on tenant/customer:

```csharp
policies.When(ctx => {
        var security = ctx.Envelope.GetCurrentSecurityContext();
        return security?.TenantId == "enterprise-tenant-123";
    })
    .Then(config => config
        .UseTopic("enterprise-orders")
        .WithPartitions(128)  // More capacity for enterprise
    );

policies.When(ctx => true)
    .Then(config => config
        .UseTopic("standard-orders")
        .WithPartitions(16)
    );
```

**Use when**: Multi-tenant SaaS applications.

### Pattern 6: Hybrid Routing (Combine Patterns)

Combine multiple predicates for complex routing:

```csharp
policies.When(ctx =>
        ctx.MatchesAggregate<Order>() &&
        ctx.HasTag("priority:high") &&
        ctx.Environment == "Production"
    )
    .Then(config => config
        .UseTopic("production-high-priority-orders")
        .WithPartitions(64)
        .UseExecutionStrategy<SerialExecutor>()
    );

policies.When(ctx => ctx.MatchesAggregate<Order>())
    .Then(config => config
        .UseTopic("orders")
        .WithPartitions(16)
    );
```

**Use when**: Complex business rules for routing.

---

## Testing Policies

### Unit Testing Policies

```csharp
using TUnit.Assertions;

[Test]
public async Task OrderPolicy_ShouldRouteToOrdersTopic() {
    // Arrange
    var policies = new PolicyBuilder();
    OrderPolicies.Configure(policies);

    var engine = new PolicyEngine(policies.Build());

    var message = new CreateOrderCommand { OrderId = "12345" };
    var envelope = CreateTestEnvelope(message);
    var context = new PolicyContext(envelope);

    // Act
    var config = await engine.MatchPolicyAsync(context);

    // Assert
    await Assert.That(config).IsNotNull();
    await Assert.That(config!.Topic).IsEqualTo("orders");
    await Assert.That(config.StreamKey).IsEqualTo("order-12345");
}
```

### Integration Testing Policies

```csharp
[Test]
public async Task HighPriorityOrder_ShouldRouteToHighPriorityTopic() {
    // Arrange
    var policies = new PolicyBuilder();
    OrderPolicies.Configure(policies);

    var engine = new PolicyEngine(policies.Build());

    var message = new CreateOrderCommand { OrderId = "12345" };
    var envelope = CreateTestEnvelope(message);

    // Add priority tag
    envelope.Hops[0] = envelope.Hops[0] with {
        Metadata = new Dictionary<string, object> {
            ["tags"] = new[] { "priority:high" }
        }
    };

    var context = new PolicyContext(envelope);

    // Act
    var config = await engine.MatchPolicyAsync(context);

    // Assert
    await Assert.That(config).IsNotNull();
    await Assert.That(config!.Topic).IsEqualTo("high-priority");
}
```

---

## Policy Debugging

### Decision Trail

Every policy evaluation is recorded in the decision trail:

```csharp
var context = new PolicyContext(envelope);
var config = await engine.MatchPolicyAsync(context);

// Inspect decision trail
var decisions = context.Trail.Decisions;

foreach (var decision in decisions) {
    Console.WriteLine($"Policy: {decision.PolicyName}");
    Console.WriteLine($"  Matched: {decision.Matched}");
    Console.WriteLine($"  Reason: {decision.Reason}");
}
```

**Example Output**:

```
Policy: "High Priority Policy"
  Matched: false
  Reason: "Message does not have 'priority:high' tag"

Policy: "Order Processing Policy"
  Matched: true
  Reason: "Message matches Order aggregate"
```

### Logging

Add logging to policy predicates:

```csharp
policies.When(ctx => {
        var logger = ctx.GetService<ILogger>();
        var matches = ctx.MatchesAggregate<Order>();

        logger.LogDebug("Order policy predicate: {Matches}", matches);

        return matches;
    })
    .Then(config => config.UseTopic("orders"));
```

---

## Best Practices

### 1. Keep Predicates Simple

```csharp
// ✅ GOOD: Simple, readable
policies.When(ctx => ctx.MatchesAggregate<Order>())

// ❌ BAD: Complex logic in predicate
policies.When(ctx => {
    var service = ctx.GetService<IOrderValidator>();
    var isValid = service.Validate(ctx.Message);
    var isPriority = ctx.HasTag("priority:high");
    return isValid && isPriority;
})
```

**Prefer**: Move complex logic to helper methods or services.

### 2. Use Meaningful Policy Names

```csharp
// Policies should have descriptive names (for debugging)
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Named("Order Processing Policy")  // (future feature)
```

### 3. Avoid Side Effects in Predicates

```csharp
// ❌ BAD: Side effects (state changes)
policies.When(ctx => {
    _counter++;  // DON'T DO THIS
    return ctx.MatchesAggregate<Order>();
})

// ✅ GOOD: Pure predicate (no side effects)
policies.When(ctx => ctx.MatchesAggregate<Order>())
```

### 4. Order Policies Carefully

```csharp
// ✅ CORRECT ORDER: Specific → General
1. High priority orders
2. Standard orders
3. All orders (catch-all)
4. Default (everything else)

// ❌ WRONG ORDER: General first blocks specific
1. Default (matches everything - other policies never evaluated!)
```

### 5. Always Have a Default Policy

```csharp
// Last policy: catch-all default
policies.When(ctx => true)
        .Then(config => config
            .UseTopic("default")
            .UseStream("default-stream")
        );
```

**Why**: Prevents exceptions if no policies match.

---

## Performance Considerations

### Policy Evaluation Overhead

From benchmarks (v0.2.0):

```
MatchPolicy_1Policy:      ~10 μs
MatchPolicy_5Policies:    ~45 μs  (evaluates all 5, matches last)
MatchPolicy_20Policies:  ~180 μs  (evaluates all 20, matches last)
```

**Recommendation**: Keep policy count reasonable (< 50 policies).

### Optimization: Early Exit

Policies are evaluated in order and **stop at first match**:

```csharp
// Only evaluates policies until one matches
policies.When(ctx => ctx.HasTag("priority:critical"))  // Checked first
        .Then(...);

policies.When(ctx => ctx.MatchesAggregate<Order>())   // Checked second
        .Then(...);

// If first policy matches, second is never evaluated
```

### Caching (Future Feature)

For complex predicates, consider caching results:

```csharp
// v0.3.0+ (future)
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .WithCaching(TimeSpan.FromMinutes(5))  // Cache for 5 min
        .Then(...);
```

---

## Summary

Policies in Whizbang:

- **Drive all routing and execution decisions**
- **Evaluated in order** (first match wins)
- **Record decision trails** for debugging
- **Support complex predicates** via PolicyContext helpers
- **Fluent configuration API** for routing, partitioning, execution
- **Testable** with unit and integration tests
- **Low overhead** (<10μs for simple policies)

**Key Principles**:
1. Order policies from specific to general
2. Keep predicates pure (no side effects)
3. Always have a default policy
4. Test policies thoroughly
5. Use decision trails for debugging

This approach enables **flexible, explicit, debuggable routing** without hard-coded conventions.
