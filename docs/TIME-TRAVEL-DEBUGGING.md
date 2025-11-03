# Time-Travel Debugging Guide

This guide explains how to use Whizbang's observability features to **debug distributed systems** by replaying message flows and understanding "what happened" and "why".

## Overview

Traditional debugging shows **where you are now**. Time-travel debugging shows **how you got here** - the complete history of events that led to the current state.

Whizbang enables time-travel debugging through:

- **Complete hop chains** - Every message carries its full journey
- **Causation tracking** - Parent message hops preserved in child messages
- **Policy decision trails** - Records why routing decisions were made
- **Caller information** - Exact source code locations for each hop
- **Correlation tracking** - All messages in a workflow linked together

---

## Core Concepts

### MessageEnvelope: Complete Journey

Every message is wrapped in a `MessageEnvelope` that contains:

```csharp
public class MessageEnvelope<TMessage> {
    // Identity & Causality
    public MessageId MessageId { get; init; }
    public CorrelationId CorrelationId { get; init; }
    public CausationId CausationId { get; init; }

    // The actual message
    public TMessage Payload { get; init; }

    // Complete journey (one or more hops)
    public required List<MessageHop> Hops { get; init; }
}
```

### MessageHop: Complete Snapshot

Each hop is a **complete snapshot** of message state at that point:

```csharp
public record MessageHop {
    // Type: Current (this message) or Causation (parent message)
    public required HopType Type { get; init; }

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

    // Policy decisions made at this hop
    public PolicyDecisionTrail? Trail { get; init; }

    // Custom metadata
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    // Causation tracking (for parent messages)
    public MessageId? CausationMessageId { get; init; }
    public string? CausationMessageType { get; init; }

    // Caller information (jump to source code)
    public string? CallerMemberName { get; init; }
    public string? CallerFilePath { get; init; }
    public int? CallerLineNumber { get; init; }

    // Performance
    public TimeSpan? Duration { get; init; }
}
```

---

## Time-Travel Debugging Scenarios

### Scenario 1: "Why did this message go to the wrong topic?"

**Problem**: Message was routed to `inventory` instead of expected `orders`.

**Solution**: Examine policy decision trail on relevant hops.

```csharp
// Get the envelope from TraceStore
var envelope = await traceStore.GetByMessageIdAsync(messageId);

// Get all policy decisions across all hops
var decisions = envelope.GetAllPolicyDecisions();

foreach (var decision in decisions) {
    Console.WriteLine($"Policy: {decision.PolicyName}");
    Console.WriteLine($"  Rule: {decision.Rule}");
    Console.WriteLine($"  Matched: {decision.Matched}");
    Console.WriteLine($"  Reason: {decision.Reason}");

    if (decision.Configuration is PolicyConfiguration config) {
        Console.WriteLine($"  Topic: {config.Topic}");
        Console.WriteLine($"  Stream: {config.StreamKey}");
    }
}
```

**Example Output**:

```
Policy: "Order Processing Policy"
  Rule: "ctx.MatchesAggregate<Order>()"
  Matched: false
  Reason: "Message type is 'InventoryReserveCommand', not 'Order'"

Policy: "Inventory Policy"
  Rule: "ctx.MatchesAggregate<InventoryItem>()"
  Matched: true
  Reason: "Message type matches InventoryItem aggregate"
  Topic: inventory
  Stream: inventory-item-456
```

**Root Cause**: Message was an `InventoryReserveCommand`, not an `Order` command. Policy correctly routed to `inventory` topic.

---

### Scenario 2: "What caused this error?"

**Problem**: `NullReferenceException` in notification service.

**Solution**: Walk backwards through causal chain to find the source.

```csharp
// Get the failed envelope
var envelope = await traceStore.GetByMessageIdAsync(failedMessageId);

// Get the complete causal chain (parents + this message + children)
var chain = await traceStore.GetCausalChainAsync(failedMessageId);

// Print chronological causal chain
foreach (var msg in chain.OrderBy(e => e.GetMessageTimestamp())) {
    var currentHop = msg.Hops.LastOrDefault(h => h.Type == HopType.Current);

    Console.WriteLine($"{msg.GetMessageTimestamp():HH:mm:ss.fff}");
    Console.WriteLine($"  Message: {msg.Payload.GetType().Name}");
    Console.WriteLine($"  Service: {currentHop?.ServiceName}");
    Console.WriteLine($"  Caller: {currentHop?.CallerFilePath}:{currentHop?.CallerLineNumber}");
    Console.WriteLine();
}
```

**Example Output**:

```
14:23:45.123
  Message: OrderCreatedEvent
  Service: Orders.Service
  Caller: /src/Orders.Service/Receptors/OrdersReceptor.cs:127

14:23:45.245
  Message: SendEmailCommand
  Service: Orders.Service
  Caller: /src/Orders.Service/Sagas/OrderSaga.cs:89

14:23:45.289
  Message: EmailSentEvent
  Service: Notifications.Service
  Caller: /src/Notifications.Service/Receptors/EmailReceptor.cs:45  ← ERROR HERE
```

**Root Cause**: Check `EmailReceptor.cs:45` - likely accessing null property on `SendEmailCommand`.

---

### Scenario 3: "How did user context change?"

**Problem**: Email sent with wrong user/tenant context.

**Solution**: Trace security context changes across hops.

```csharp
var envelope = await traceStore.GetByMessageIdAsync(messageId);

// Walk through all Current hops (ignoring Causation hops)
foreach (var hop in envelope.Hops.Where(h => h.Type == HopType.Current)) {
    var security = hop.SecurityContext;

    Console.WriteLine($"{hop.Timestamp:HH:mm:ss.fff} - {hop.ServiceName}");
    Console.WriteLine($"  UserId: {security?.UserId ?? "NOT SET"}");
    Console.WriteLine($"  TenantId: {security?.TenantId ?? "NOT SET"}");
    Console.WriteLine($"  Caller: {hop.CallerFilePath}:{hop.CallerLineNumber}");
    Console.WriteLine();
}
```

**Example Output**:

```
14:23:45.123 - API.Gateway
  UserId: user-123
  TenantId: tenant-abc
  Caller: /src/API.Gateway/Controllers/OrdersController.cs:67

14:23:45.134 - Orders.Service
  UserId: user-123
  TenantId: tenant-abc
  Caller: /src/Orders.Service/Receptors/OrdersReceptor.cs:127

14:23:45.245 - Notifications.Service
  UserId: NOT SET  ← PROBLEM: Context lost here
  TenantId: NOT SET
  Caller: /src/Notifications.Service/Receptors/EmailReceptor.cs:45
```

**Root Cause**: `EmailReceptor.cs:45` didn't propagate security context to new message.

---

### Scenario 4: "Show me all messages in this workflow"

**Problem**: Need to see complete workflow for order 12345.

**Solution**: Query by CorrelationId.

```csharp
// All messages in the same workflow share the same CorrelationId
var workflow = await traceStore.GetByCorrelationAsync(correlationId);

// Print chronological timeline
foreach (var envelope in workflow.OrderBy(e => e.GetMessageTimestamp())) {
    var hop = envelope.Hops.LastOrDefault(h => h.Type == HopType.Current);

    Console.WriteLine($"{envelope.GetMessageTimestamp():HH:mm:ss.fff} - {envelope.Payload.GetType().Name}");
    Console.WriteLine($"  Service: {hop?.ServiceName}");
    Console.WriteLine($"  Topic: {hop?.Topic}");
    Console.WriteLine($"  Stream: {hop?.StreamKey}");
    Console.WriteLine();
}
```

**Example Output**:

```
14:23:45.123 - OrderCreatedEvent
  Service: Orders.Service
  Topic: orders
  Stream: order-12345

14:23:45.245 - InventoryReservedCommand
  Service: Orders.Service
  Topic: inventory
  Stream: inventory-item-456

14:23:45.267 - InventoryReservedEvent
  Service: Inventory.Service
  Topic: inventory
  Stream: inventory-item-456

14:23:45.289 - SendEmailCommand
  Service: Orders.Service
  Topic: notifications
  Stream: notifications-shared

14:23:45.312 - EmailSentEvent
  Service: Notifications.Service
  Topic: notifications
  Stream: notifications-shared
```

**Result**: Complete workflow timeline showing message flow across services.

---

### Scenario 5: "What happened between 2:00 PM and 2:05 PM?"

**Problem**: System had issues during specific time window.

**Solution**: Query by time range.

```csharp
var from = new DateTimeOffset(2025, 11, 2, 14, 0, 0, TimeSpan.Zero);
var to = new DateTimeOffset(2025, 11, 2, 14, 5, 0, TimeSpan.Zero);

var messages = await traceStore.GetByTimeRangeAsync(from, to);

Console.WriteLine($"Found {messages.Count} messages between {from:HH:mm} and {to:HH:mm}");

foreach (var envelope in messages) {
    var hop = envelope.Hops.LastOrDefault(h => h.Type == HopType.Current);

    Console.WriteLine($"{envelope.GetMessageTimestamp():HH:mm:ss.fff}");
    Console.WriteLine($"  {envelope.Payload.GetType().Name}");
    Console.WriteLine($"  {hop?.ServiceName} → {hop?.Topic}");
}
```

**Example Output**:

```
Found 1,247 messages between 14:00 and 14:05

14:00:03.456
  OrderCreatedEvent
  Orders.Service → orders

14:00:03.478
  InventoryReservedCommand
  Orders.Service → inventory

... (1,245 more messages)
```

---

## Causation Hop Tracking

### Parent Message Context

When a message spawns child messages, the **parent's Current hops** become **Causation hops** in the child:

```csharp
// Parent message
var parentEnvelope = new MessageEnvelope<OrderCreatedEvent> {
    MessageId = MessageId.New(),
    CorrelationId = correlationId,
    CausationId = CausationId.From(MessageId.Empty),
    Payload = orderCreatedEvent,
    Hops = [
        new MessageHop {
            Type = HopType.Current,
            ServiceName = "Orders.Service",
            Topic = "orders",
            StreamKey = "order-12345",
            Timestamp = DateTimeOffset.UtcNow
        }
    ]
};

// Child message inherits parent's Current hops as Causation hops
var childEnvelope = new MessageEnvelope<SendEmailCommand> {
    MessageId = MessageId.New(),
    CorrelationId = correlationId,  // Same correlation
    CausationId = CausationId.From(parentEnvelope.MessageId),  // Parent is causation
    Payload = sendEmailCommand,
    Hops = [
        // Causation hops (from parent's Current hops)
        new MessageHop {
            Type = HopType.Causation,  // ← Marked as Causation
            CausationMessageId = parentEnvelope.MessageId,
            CausationMessageType = nameof(OrderCreatedEvent),
            ServiceName = "Orders.Service",  // Where parent was processed
            Topic = "orders",
            StreamKey = "order-12345",
            Timestamp = parentEnvelope.GetMessageTimestamp()
        },

        // Current hop (this message)
        new MessageHop {
            Type = HopType.Current,  // ← Marked as Current
            ServiceName = "Notifications.Service",
            Topic = "notifications",
            StreamKey = "notifications-shared",
            Timestamp = DateTimeOffset.UtcNow
        }
    ]
};
```

**Key Points**:
- Parent's `Current` hops → Child's `Causation` hops
- Child always has at least 1 `Current` hop (origin of child message)
- Complete causal chain preserved (can see what led to this message)

---

## Policy Decision Trail Debugging

### Understand "Why This Routing?"

Policy decisions are recorded **per hop** in `MessageHop.Trail`:

```csharp
var envelope = await traceStore.GetByMessageIdAsync(messageId);

// Get all policy decisions from all hops
var allDecisions = envelope.GetAllPolicyDecisions();

foreach (var decision in allDecisions) {
    Console.WriteLine($"Policy: {decision.PolicyName}");
    Console.WriteLine($"  Rule: {decision.Rule}");
    Console.WriteLine($"  Matched: {decision.Matched}");

    if (decision.Matched && decision.Configuration is PolicyConfiguration config) {
        Console.WriteLine($"  ↳ Topic: {config.Topic}");
        Console.WriteLine($"  ↳ Stream: {config.StreamKey}");
        Console.WriteLine($"  ↳ Execution: {config.ExecutionStrategyType?.Name}");
        Console.WriteLine($"  ↳ Partitions: {config.PartitionCount}");
    } else {
        Console.WriteLine($"  ↳ Reason: {decision.Reason}");
    }

    Console.WriteLine();
}
```

**Example Output**:

```
Policy: "Default Policy"
  Rule: "ctx => true"
  Matched: false
  ↳ Reason: "Not first match (Order Processing Policy matched first)"

Policy: "Order Processing Policy"
  Rule: "ctx => ctx.MatchesAggregate<Order>()"
  Matched: true
  ↳ Topic: orders
  ↳ Stream: order-12345
  ↳ Execution: SerialExecutor
  ↳ Partitions: 16
```

---

## Metadata Stitching Debugging

### Track Metadata Changes

Metadata can change from hop to hop (enrichment pattern):

```csharp
var envelope = await traceStore.GetByMessageIdAsync(messageId);

// Get all metadata (stitched across all hops)
var allMetadata = envelope.GetAllMetadata();

Console.WriteLine("Final metadata:");
foreach (var (key, value) in allMetadata) {
    Console.WriteLine($"  {key}: {value}");
}

Console.WriteLine("\nMetadata changes per hop:");
foreach (var hop in envelope.Hops.Where(h => h.Type == HopType.Current)) {
    if (hop.Metadata is not null && hop.Metadata.Count > 0) {
        Console.WriteLine($"{hop.Timestamp:HH:mm:ss.fff} - {hop.ServiceName}");
        foreach (var (key, value) in hop.Metadata) {
            Console.WriteLine($"  {key}: {value}");
        }
    }
}
```

**Example Output**:

```
Final metadata:
  priority: high
  source: api-gateway
  enriched: true
  validated: true

Metadata changes per hop:
14:23:45.123 - API.Gateway
  priority: low
  source: api-gateway

14:23:45.134 - Orders.Service
  priority: high  ← Changed from 'low' to 'high'
  enriched: true  ← Added

14:23:45.245 - Inventory.Service
  validated: true  ← Added
```

**Key Points**:
- Later hops override earlier hops for same keys
- `GetAllMetadata()` returns stitched result
- Each hop's metadata shows what changed at that point

---

## Best Practices

### 1. Always Use CorrelationId

```csharp
// Start of workflow: create new CorrelationId
var correlationId = CorrelationId.New();

// All messages in workflow share same CorrelationId
var envelope1 = new MessageEnvelope<OrderCreatedEvent> {
    CorrelationId = correlationId,  // ← Same ID
    // ...
};

var envelope2 = new MessageEnvelope<SendEmailCommand> {
    CorrelationId = correlationId,  // ← Same ID
    // ...
};
```

**Why**: Enables querying all messages in a workflow.

### 2. Preserve Causation

```csharp
// Child message should reference parent
var childEnvelope = new MessageEnvelope<SendEmailCommand> {
    CausationId = CausationId.From(parentEnvelope.MessageId),
    // ...
};
```

**Why**: Enables causal chain queries (parent/child relationships).

### 3. Record Policy Decisions

```csharp
// In your policy engine
var trail = new PolicyDecisionTrail();

foreach (var policy in policies) {
    var matched = policy.Predicate(context);

    trail.RecordDecision(
        policyName: policy.Name,
        rule: policy.Rule,
        matched: matched,
        configuration: matched ? policy.Configuration : null,
        reason: matched ? "Matched" : "Did not match predicate"
    );

    if (matched) break;
}

// Add trail to current hop
currentHop.Trail = trail;
```

**Why**: Enables debugging "why this routing?"

### 4. Enrich Metadata Incrementally

```csharp
// Hop 1: API Gateway
var hop1 = new MessageHop {
    Metadata = new Dictionary<string, object> {
        ["source"] = "api-gateway",
        ["priority"] = "low"
    }
};

// Hop 2: Orders Service (enrichment)
var hop2 = new MessageHop {
    Metadata = new Dictionary<string, object> {
        ["priority"] = "high",  // Override
        ["enriched"] = true     // Add new
    }
    // Note: 'source' is inherited from hop1 (null coalescing)
};
```

**Why**: Shows how message was enriched through the pipeline.

### 5. Store Traces Asynchronously

```csharp
// Don't block message processing
_ = Task.Run(async () => {
    await traceStore.StoreAsync(envelope);
});
```

**Why**: Minimize observability overhead on critical path.

---

## TraceStore Query Examples

### Find All Failed Messages

```csharp
var allMessages = await traceStore.GetByTimeRangeAsync(from, to);

var failed = allMessages.Where(envelope => {
    var metadata = envelope.GetAllMetadata();
    return metadata.TryGetValue("error", out var error) && error != null;
});

foreach (var envelope in failed) {
    Console.WriteLine($"Failed: {envelope.MessageId}");
    Console.WriteLine($"  Error: {envelope.GetAllMetadata()["error"]}");
}
```

### Find Slowest Messages

```csharp
var allMessages = await traceStore.GetByTimeRangeAsync(from, to);

var slow = allMessages
    .Select(envelope => new {
        Envelope = envelope,
        Duration = envelope.Hops
            .Where(h => h.Type == HopType.Current)
            .Sum(h => h.Duration?.TotalMilliseconds ?? 0)
    })
    .OrderByDescending(x => x.Duration)
    .Take(10);

foreach (var item in slow) {
    Console.WriteLine($"{item.Envelope.MessageId}: {item.Duration:F2}ms");
}
```

### Find Messages by User

```csharp
var allMessages = await traceStore.GetByTimeRangeAsync(from, to);

var userMessages = allMessages.Where(envelope => {
    var security = envelope.GetCurrentSecurityContext();
    return security?.UserId == "user-123";
});

foreach (var envelope in userMessages) {
    Console.WriteLine($"{envelope.Payload.GetType().Name} at {envelope.GetMessageTimestamp()}");
}
```

---

## Summary

Time-travel debugging with Whizbang enables:

- **Complete audit trails** - Every hop recorded with full context
- **Causal chain walking** - Understand parent/child relationships
- **Policy decision replay** - See why routing decisions were made
- **Metadata enrichment tracking** - See how messages were enriched
- **Security context tracking** - Trace user/tenant changes
- **Caller information** - Jump to exact source code lines
- **Workflow correlation** - All messages in workflow linked together
- **Time-based queries** - Find messages in specific time windows

**Key Benefits**:
- Debug distributed systems like local code
- Understand "what happened" and "why"
- Reproduce production issues in development
- Audit message flows for compliance
- Optimize performance bottlenecks

This turns distributed systems from **black boxes** into **transparent, debuggable systems**.
