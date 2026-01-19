# VSCode Extension Data Guide

This guide documents the observability data that Whizbang captures to enable a future **VSCode extension for message flow visualization and debugging**.

## Overview

Whizbang captures **caller information** at every hop in a message's journey. This enables a VSCode extension to:

- **Visualize message flows** graphically
- **Jump to source code** where messages were sent/received
- **Trace causal chains** across service boundaries
- **Debug distributed systems** like local code

> **Note**: The VSCode extension is **not part of v0.2.0**. We are only **capturing the data** it will need. The extension itself will be built in a future release.

---

## Captured Data

### Caller Information (Automatic)

Every `MessageHop` automatically captures caller information using C# compiler attributes:

```csharp
public record MessageHop {
    // Caller information (automatic via [CallerMemberName], etc.)
    public string? CallerMemberName { get; init; }
    public string? CallerFilePath { get; init; }
    public int? CallerLineNumber { get; init; }

    // ... other fields
}
```

### Example Captured Data

```json
{
  "CallerMemberName": "ExecuteAsync",
  "CallerFilePath": "/Users/phil/whizbang/src/Whizbang.Core/Execution/SerialExecutor.cs",
  "CallerLineNumber": 45
}
```

This enables the VSCode extension to create **clickable links** that jump directly to the source code line where the hop was recorded.

---

## How Caller Information is Captured

Whizbang uses **C# compiler magic attributes** to automatically capture call site information at **compile-time** with **zero runtime overhead**.

### Automatic Capture in MessageTracing

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

            // Automatically filled by compiler:
            CallerMemberName = callerMemberName,
            CallerFilePath = callerFilePath,
            CallerLineNumber = callerLineNumber
        };
    }
}
```

### Usage Example

```csharp
public class SerialExecutor : IExecutionStrategy {
    public async Task<TResult> ExecuteAsync<TResult>(...) {
        // Compiler automatically fills in:
        //   CallerMemberName = "ExecuteAsync"
        //   CallerFilePath = "...src/Whizbang.Core/Execution/SerialExecutor.cs"
        //   CallerLineNumber = 45
        var hop = MessageTracing.RecordHop(
            envelope.GetCurrentTopic() ?? "unknown",
            envelope.GetCurrentStreamKey() ?? "unknown",
            this.Name
        );

        // Add hop to envelope
        envelope.Hops.Add(hop);

        // ... execute handler
    }
}
```

**Zero code changes needed** - the compiler fills in caller info automatically.

---

## Complete Hop Data Structure

Every hop contains a **complete snapshot** of message state at that point:

```csharp
public record MessageHop {
    // Identity
    public required HopType Type { get; init; }  // Current or Causation

    // Service/Machine identity
    public required string ServiceName { get; init; }
    public required string MachineName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    // Routing information
    public string? Topic { get; init; }
    public string? StreamKey { get; init; }
    public int? PartitionIndex { get; init; }
    public long? SequenceNumber { get; init; }
    public required string ExecutionStrategy { get; init; }

    // Security context
    public SecurityContext? SecurityContext { get; init; }

    // Policy decisions (at this hop)
    public PolicyDecisionTrail? Trail { get; init; }

    // Custom metadata (at this hop)
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    // Causation tracking (for Causation hops)
    public MessageId? CausationMessageId { get; init; }
    public string? CausationMessageType { get; init; }

    // Caller information (for VSCode extension - JUMP TO LINE)
    public string? CallerMemberName { get; init; }
    public string? CallerFilePath { get; init; }
    public int? CallerLineNumber { get; init; }

    // Performance
    public TimeSpan? Duration { get; init; }
}
```

---

## VSCode Extension Capabilities (Future)

### 1. Message Flow Visualization

The extension will render message journeys graphically:

```
OrderReceived (API Gateway)
  │ CreateOrderCommand (Orders Service)
  │   ├─ OrderCreatedEvent (Orders Service)
  │   │    ├─ InventoryReservedCommand (Inventory Service)
  │   │    └─ SendEmailCommand (Notifications Service)
  │   └─ AuditLogCommand (Audit Service)
```

**Click any node** → Jumps to source code where that message was created.

### 2. Jump to Source Code

Each hop is **clickable** in the visualization:

```
OrderCreatedEvent (Orders Service)
  Source: OrdersReceptor.cs:127
  [Click to open file]
```

Clicking opens:
- File: `/Users/phil/whizbang/src/Orders.Service/Receptors/OrdersReceptor.cs`
- Line: 127
- Method: `HandleCreateOrderAsync`

### 3. Distributed Tracing View

Show **complete causal chains** across services:

```
[Service: API Gateway]
  ↓ OrderReceived (line 45)

[Service: Orders Service]
  ↓ CreateOrderCommand (line 123)
  ↓ OrderCreatedEvent (line 127)

[Service: Inventory Service]
  ↓ InventoryReservedCommand (line 89)
  ↓ InventoryReservedEvent (line 92)

[Service: Notifications Service]
  ↓ SendEmailCommand (line 210)
```

**Every line is clickable** - jumps across codebases/repos.

### 4. Policy Decision Debugging

Visualize **why** a message was routed a certain way:

```
OrderCreatedEvent
  Policy: "Order Processing Policy"
    ✓ Rule: "ctx.MatchesAggregate<Order>()" → MATCHED
    ✓ Topic: "orders"
    ✓ Stream: "order-12345"
    ✓ Partition: 3 (of 16)
    ✓ Execution: SerialExecutor

  [View source: OrderPolicies.cs:42]
```

### 5. Time-Travel Debugging

**Replay message flows** chronologically:

```
Timeline View:
14:23:45.123 - OrderReceived (API Gateway)
14:23:45.134 - CreateOrderCommand (Orders Service)
14:23:45.245 - OrderCreatedEvent (Orders Service)
14:23:45.267 - InventoryReservedCommand (Inventory Service)
14:23:45.289 - SendEmailCommand (Notifications Service)

[Scrubber: ========|===============]
         You are here ↑
```

Dragging the scrubber shows **system state at any point in time**.

### 6. Live Message Monitoring

Show **messages flowing through the system in real-time**:

```
Live Messages (last 10 seconds):
- OrderCreatedEvent → orders/partition-3 (25ms) ✓
- InventoryReservedCommand → inventory/partition-1 (12ms) ✓
- SendEmailCommand → notifications/partition-0 (45ms) ⚠ SLOW
- OrderCancelledEvent → orders/partition-7 (18ms) ✓

[Click any message to see full trace]
```

---

## Data Storage Requirements

### Trace Store Interface

Whizbang provides `ITraceStore` for storing complete message histories:

```csharp
public interface ITraceStore {
    // Store complete message envelope
    Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default);

    // Query by message ID
    Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default);

    // Query by correlation (all messages in workflow)
    Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default);

    // Query by causation (parent/child chain)
    Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default);

    // Query by time range
    Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
```

### Storage Options

**Development**:
- `InMemoryTraceStore` - Fast, no persistence (v0.2.0)

**Production** (future):
- `ElasticsearchTraceStore` - Full-text search, visualizations
- `SqlTraceStore` - Structured queries, relational analysis
- `MongoDbTraceStore` - Document-based, flexible schema

---

## VSCode Extension Architecture (Future)

### Data Flow

```
1. Application emits messages
   ↓
2. Whizbang captures hops with caller info
   ↓
3. TraceStore persists envelopes
   ↓
4. VSCode extension queries TraceStore
   ↓
5. Extension renders visualizations
   ↓
6. User clicks hop → Extension opens file at line
```

### Extension Components

**Backend (ASP.NET Core)**:
- REST API for querying TraceStore
- WebSocket for live message streaming
- Authentication/authorization

**Frontend (VSCode Extension)**:
- Tree view for message hierarchies
- Graph view for causal chains
- Timeline view for chronological replay
- Code lens decorations (show message flow inline)

**Communication**:
- Extension connects to backend via WebSocket
- Real-time updates as messages flow
- Historical queries via REST API

---

## Example: End-to-End Flow

### 1. Code Emits Message

```csharp
// File: Orders.Service/Receptors/OrdersReceptor.cs
// Line: 127
public async Task HandleAsync(CreateOrderCommand cmd, PolicyContext ctx) {
    // Business logic...
    var evt = new OrderCreatedEvent(...);

    // Whizbang automatically captures:
    // - CallerMemberName: "HandleAsync"
    // - CallerFilePath: ".../Orders.Service/Receptors/OrdersReceptor.cs"
    // - CallerLineNumber: 127
    await _dispatcher.DispatchAsync(evt, ctx);
}
```

### 2. Hop is Recorded

```json
{
  "Type": "Current",
  "ServiceName": "Orders.Service",
  "MachineName": "orders-pod-7f8b9",
  "Timestamp": "2025-11-02T14:23:45.245Z",
  "Topic": "orders",
  "StreamKey": "order-12345",
  "PartitionIndex": 3,
  "SequenceNumber": 789,
  "ExecutionStrategy": "SerialExecutor",
  "CallerMemberName": "HandleAsync",
  "CallerFilePath": "/Users/phil/whizbang-demo/Orders.Service/Receptors/OrdersReceptor.cs",
  "CallerLineNumber": 127
}
```

### 3. TraceStore Persists Envelope

```csharp
await _traceStore.StoreAsync(envelope);
```

### 4. VSCode Extension Queries

```typescript
// Extension queries backend
const envelope = await api.getEnvelope(messageId);

// Render hop
const hop = envelope.hops[2];
const link = createCodeLink(
    hop.callerFilePath,  // "/Users/phil/.../OrdersReceptor.cs"
    hop.callerLineNumber  // 127
);

// User clicks → VSCode opens file at line 127
```

---

## Performance Considerations

### Overhead

- **Caller info capture**: **ZERO** runtime overhead (compile-time)
- **Hop creation**: ~5μs per hop (minimal)
- **Trace storage**: Async, non-blocking

### Benchmarks (from v0.2.0)

```
CreateMessageHop:        4.82 μs   (single hop)
CreateMessageEnvelope:   8.43 μs   (envelope + 1 hop)
TraceStore.StoreAsync:  12.56 μs   (storage overhead)
```

**Total overhead**: ~13μs per message (0.013ms) - negligible for most workloads.

### Scalability

- Store traces **asynchronously** (don't block message processing)
- Use **partitioned trace stores** (ElasticSearch, MongoDB clusters)
- Implement **retention policies** (delete old traces)
- Sample traces in high-throughput scenarios (store 10% of messages)

---

## Development Timeline

### v0.2.0 (Current Release)
- ✅ Caller info capture in MessageHop
- ✅ ITraceStore interface
- ✅ InMemoryTraceStore implementation
- ✅ Complete observability data model

### v0.3.0 (Future)
- Persistent trace stores (SQL, Elasticsearch)
- REST API for trace queries
- WebSocket live streaming

### v0.4.0 (Future)
- VSCode extension (alpha)
- Message flow visualization
- Jump to source code
- Policy decision debugging

### v0.5.0 (Future)
- VSCode extension (beta)
- Time-travel debugging
- Live monitoring dashboard
- Code lens decorations

---

## Summary

- **Whizbang captures caller information automatically** using C# compiler attributes
- **Zero code changes required** - just call `MessageTracing.RecordHop()`
- **Zero runtime overhead** - compiler fills in call site at compile-time
- **Complete data model** for future VSCode extension
- **Enables distributed debugging** like local debugging
- **VSCode extension is future work** - data capture is done in v0.2.0

This design enables **unprecedented visibility** into distributed systems, making message flows as debuggable as local function calls.
