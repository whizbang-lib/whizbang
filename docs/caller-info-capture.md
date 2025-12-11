# Caller Information Capture Guide

This guide explains how Whizbang captures **source code location information** (file path, line number, method name) automatically using C# compiler attributes.

## Overview

Whizbang uses **C# magic attributes** to capture call site information at **compile-time** with **zero runtime overhead**.

This enables:
- **Jump to source code** in future VSCode extension
- **Debugging** - see exactly where messages originated
- **Audit trails** - complete traceability of message flows
- **Time-travel debugging** - replay message history with source locations

---

## The Magic: Compiler Attributes

C# provides three compiler attributes that automatically inject call site information:

### [CallerMemberName]
Captures the **method or property name** where the call originated.

### [CallerFilePath]
Captures the **full file path** where the call originated.

### [CallerLineNumber]
Captures the **line number** where the call originated.

**Key Point**: These are **compile-time only** - zero runtime overhead!

---

## How Whizbang Uses Caller Info

### MessageTracing.RecordHop()

Whizbang provides a static helper that automatically captures caller information:

```csharp
// File: src/Whizbang.Core/Observability/MessageTracing.cs

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
            Type = HopType.Current,
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

### Usage in Production Code

```csharp
// File: Orders.Service/Receptors/OrdersReceptor.cs
// Line: 127

public class OrdersReceptor {
    public async Task HandleCreateOrderAsync(CreateOrderCommand cmd, PolicyContext ctx) {
        // Business logic...
        var evt = new OrderCreatedEvent { OrderId = cmd.OrderId };

        // Record hop - compiler automatically captures:
        // - CallerMemberName: "HandleCreateOrderAsync"
        // - CallerFilePath: "/Users/phil/Orders.Service/Receptors/OrdersReceptor.cs"
        // - CallerLineNumber: 127
        var hop = MessageTracing.RecordHop(
            topic: "orders",
            streamKey: $"order-{cmd.OrderId}",
            executionStrategy: "SerialExecutor"
        );

        // Add hop to envelope
        var envelope = new MessageEnvelope<OrderCreatedEvent> {
            MessageId = MessageId.New(),
            CorrelationId = ctx.Envelope.CorrelationId,
            CausationId = CausationId.From(ctx.Envelope.MessageId),
            Payload = evt,
            Hops = [hop]
        };

        await _dispatcher.DispatchAsync(envelope);
    }
}
```

**Compiler output** (what the compiler actually generates):

```csharp
// What you wrote:
var hop = MessageTracing.RecordHop(
    topic: "orders",
    streamKey: $"order-{cmd.OrderId}",
    executionStrategy: "SerialExecutor"
);

// What the compiler generates:
var hop = MessageTracing.RecordHop(
    topic: "orders",
    streamKey: $"order-{cmd.OrderId}",
    executionStrategy: "SerialExecutor",
    callerMemberName: "HandleCreateOrderAsync",
    callerFilePath: "/Users/phil/Orders.Service/Receptors/OrdersReceptor.cs",
    callerLineNumber: 127
);
```

**No runtime cost** - values are inserted at compile-time!

---

## Captured Data Structure

### MessageHop with Caller Info

```csharp
public record MessageHop {
    // ... other fields

    // Caller information (for VSCode extension)
    public string? CallerMemberName { get; init; }
    public string? CallerFilePath { get; init; }
    public int? CallerLineNumber { get; init; }
}
```

### Example Captured Data

```json
{
  "Type": "Current",
  "ServiceName": "Orders.Service",
  "MachineName": "orders-pod-7f8b9",
  "Timestamp": "2025-11-02T14:23:45.245Z",
  "Topic": "orders",
  "StreamKey": "order-12345",
  "ExecutionStrategy": "SerialExecutor",

  "CallerMemberName": "HandleCreateOrderAsync",
  "CallerFilePath": "/Users/phil/Orders.Service/Receptors/OrdersReceptor.cs",
  "CallerLineNumber": 127,

  "Duration": null
}
```

---

## Use Cases

### 1. Debugging Production Issues

When a message fails in production, you can **jump directly to the source code** that created it:

```csharp
var envelope = await traceStore.GetByMessageIdAsync(failedMessageId);
var currentHop = envelope.Hops.LastOrDefault(h => h.Type == HopType.Current);

Console.WriteLine($"Failed at: {currentHop?.CallerFilePath}:{currentHop?.CallerLineNumber}");
Console.WriteLine($"Method: {currentHop?.CallerMemberName}");
```

**Output**:
```
Failed at: /Users/phil/Orders.Service/Receptors/OrdersReceptor.cs:127
Method: HandleCreateOrderAsync
```

→ **Open file and jump to line 127** to investigate.

### 2. VSCode Extension Integration (Future)

The future VSCode extension will use caller info to create **clickable links**:

```typescript
// VSCode extension code (future)
const hop = envelope.hops[2];

// Create clickable link
const link = createCodeLink(
    hop.callerFilePath,    // File path
    hop.callerLineNumber   // Line number
);

// When user clicks → VSCode opens file at that line
vscode.workspace.openTextDocument(hop.callerFilePath).then(doc => {
    vscode.window.showTextDocument(doc).then(editor => {
        const position = new vscode.Position(hop.callerLineNumber - 1, 0);
        editor.selection = new vscode.Selection(position, position);
        editor.revealRange(new vscode.Range(position, position));
    });
});
```

### 3. Audit Trails

Complete traceability of who/where/when messages were created:

```csharp
var workflow = await traceStore.GetByCorrelationAsync(correlationId);

foreach (var envelope in workflow.OrderBy(e => e.GetMessageTimestamp())) {
    var hop = envelope.Hops.LastOrDefault(h => h.Type == HopType.Current);

    Console.WriteLine($"{envelope.GetMessageTimestamp():HH:mm:ss.fff}");
    Console.WriteLine($"  Message: {envelope.Payload.GetType().Name}");
    Console.WriteLine($"  Service: {hop?.ServiceName}");
    Console.WriteLine($"  Source: {hop?.CallerFilePath}:{hop?.CallerLineNumber}");
    Console.WriteLine($"  Method: {hop?.CallerMemberName}");
    Console.WriteLine();
}
```

**Output**:
```
14:23:45.123
  Message: OrderCreatedEvent
  Service: Orders.Service
  Source: /src/Orders.Service/Receptors/OrdersReceptor.cs:127
  Method: HandleCreateOrderAsync

14:23:45.245
  Message: SendEmailCommand
  Service: Orders.Service
  Source: /src/Orders.Service/Sagas/OrderSaga.cs:89
  Method: HandleOrderCreatedAsync

14:23:45.289
  Message: EmailSentEvent
  Service: Notifications.Service
  Source: /src/Notifications.Service/Receptors/EmailReceptor.cs:45
  Method: HandleSendEmailAsync
```

---

## Advanced Patterns

### Pattern 1: Custom Wrapper Methods

You can create your own wrapper methods that preserve caller info:

```csharp
public static class CustomTracing {
    public static MessageHop CreateOrderHop(
        Order order,
        [CallerMemberName] string? callerMemberName = null,
        [CallerFilePath] string? callerFilePath = null,
        [CallerLineNumber] int? callerLineNumber = null
    ) {
        return MessageTracing.RecordHop(
            topic: "orders",
            streamKey: $"order-{order.Id}",
            executionStrategy: "SerialExecutor",
            callerMemberName: callerMemberName,
            callerFilePath: callerFilePath,
            callerLineNumber: callerLineNumber
        );
    }
}

// Usage:
var hop = CustomTracing.CreateOrderHop(order);
// Compiler fills in caller info from the usage site, not from inside CreateOrderHop
```

**Key Point**: Caller attributes capture the **call site**, not the method definition.

### Pattern 2: Extension Methods

Extension methods preserve caller info:

```csharp
public static class EnvelopeExtensions {
    public static void AddHop(
        this MessageEnvelope envelope,
        string topic,
        string streamKey,
        string executionStrategy,
        [CallerMemberName] string? callerMemberName = null,
        [CallerFilePath] string? callerFilePath = null,
        [CallerLineNumber] int? callerLineNumber = null
    ) {
        var hop = MessageTracing.RecordHop(
            topic,
            streamKey,
            executionStrategy,
            callerMemberName,
            callerFilePath,
            callerLineNumber
        );

        envelope.Hops.Add(hop);
    }
}

// Usage:
envelope.AddHop("orders", "order-123", "SerialExecutor");
// Captures caller info from this line
```

### Pattern 3: Conditional Caller Info

Only capture caller info in certain environments:

```csharp
public static MessageHop RecordHop(
    string topic,
    string streamKey,
    string executionStrategy,
    bool captureCallerInfo = true,
    [CallerMemberName] string? callerMemberName = null,
    [CallerFilePath] string? callerFilePath = null,
    [CallerLineNumber] int? callerLineNumber = null
) {
    return new MessageHop {
        // ... other fields

        CallerMemberName = captureCallerInfo ? callerMemberName : null,
        CallerFilePath = captureCallerInfo ? callerFilePath : null,
        CallerLineNumber = captureCallerInfo ? callerLineNumber : null
    };
}

// Disable caller info capture in production (if needed)
var hop = MessageTracing.RecordHop(
    "orders",
    "order-123",
    "SerialExecutor",
    captureCallerInfo: false  // Don't capture for privacy/security
);
```

---

## Performance Considerations

### Zero Runtime Overhead

Caller attributes are **resolved at compile-time**:

```csharp
// What you write:
var hop = MessageTracing.RecordHop("orders", "order-123", "SerialExecutor");

// What the compiler generates (IL):
var hop = MessageTracing.RecordHop(
    "orders",
    "order-123",
    "SerialExecutor",
    "HandleCreateOrderAsync",
    "/Users/phil/Orders.Service/Receptors/OrdersReceptor.cs",
    127
);
```

**No reflection**, **no stack walking**, **no runtime cost**.

### Benchmarks

From Whizbang v0.2.0 benchmarks:

```
CreateMessageHop (with caller info):  4.82 μs
CreateMessageHop (without caller info): 4.79 μs

Difference: ~0.03 μs (negligible)
```

**Conclusion**: Caller info capture is **essentially free**.

---

## Common Mistakes

### Mistake 1: Passing Literal Values

```csharp
// ❌ WRONG: Passing literal values
var hop = MessageTracing.RecordHop(
    "orders",
    "order-123",
    "SerialExecutor",
    callerMemberName: "MyMethod",  // Don't do this!
    callerFilePath: "/some/path",
    callerLineNumber: 123
);
```

**Fix**: Let the compiler fill in the values automatically:

```csharp
// ✅ CORRECT: Omit caller parameters
var hop = MessageTracing.RecordHop(
    "orders",
    "order-123",
    "SerialExecutor"
    // Compiler fills in caller info automatically
);
```

### Mistake 2: Using in Lambda/Delegate

Caller attributes capture the **definition site** of the lambda, not the call site:

```csharp
// ❌ WRONG: Caller info is from lambda definition site
var factory = () => MessageTracing.RecordHop("orders", "order-123", "SerialExecutor");

var hop1 = factory();  // CallerLineNumber points to lambda definition, not here
var hop2 = factory();  // Same CallerLineNumber as hop1!
```

**Fix**: Call `RecordHop` directly at each site:

```csharp
// ✅ CORRECT: Call RecordHop directly
var hop1 = MessageTracing.RecordHop("orders", "order-123", "SerialExecutor");
var hop2 = MessageTracing.RecordHop("orders", "order-456", "SerialExecutor");
// Each captures different CallerLineNumber
```

### Mistake 3: Using with await

Be careful with multi-line awaits:

```csharp
// ❌ Unclear: Which line is captured?
var hop = MessageTracing.RecordHop(
    "orders",
    await GetStreamKeyAsync(),  // Is it this line?
    "SerialExecutor"
);

// ✅ BETTER: Separate concerns
var streamKey = await GetStreamKeyAsync();
var hop = MessageTracing.RecordHop("orders", streamKey, "SerialExecutor");
```

---

## Testing Caller Info

### Testing Caller Info Capture

```csharp
[Test]
public async Task RecordHop_ShouldCaptureCallerInfo() {
    // Act
    var hop = MessageTracing.RecordHop("orders", "order-123", "SerialExecutor");

    // Assert
    await Assert.That(hop.CallerMemberName).IsEqualTo("RecordHop_ShouldCaptureCallerInfo");
    await Assert.That(hop.CallerFilePath).Contains("MessageTracingTests.cs");
    await Assert.That(hop.CallerLineNumber).IsGreaterThan(0);
}
```

**Note**: `CallerLineNumber` can change if you modify the file, so test it's greater than 0 rather than exact value.

---

## Caller Info in Different Contexts

### In Receptors (Handlers)

```csharp
public class OrdersReceptor {
    public async Task HandleAsync(CreateOrderCommand cmd, PolicyContext ctx) {
        var hop = MessageTracing.RecordHop("orders", $"order-{cmd.OrderId}", "SerialExecutor");
        // CallerMemberName: "HandleAsync"
        // CallerFilePath: ".../Receptors/OrdersReceptor.cs"
        // CallerLineNumber: (line where RecordHop is called)
    }
}
```

### In Dispatchers

```csharp
public class OrderDispatcher {
    public async Task DispatchOrderEventAsync(OrderCreatedEvent evt) {
        var hop = MessageTracing.RecordHop("orders", $"order-{evt.OrderId}", "SerialExecutor");
        // CallerMemberName: "DispatchOrderEventAsync"
        // CallerFilePath: ".../Dispatchers/OrderDispatcher.cs"
        // CallerLineNumber: (line where RecordHop is called)
    }
}
```

### In Sagas

```csharp
public class OrderSaga {
    public async Task OnOrderCreatedAsync(OrderCreatedEvent evt, PolicyContext ctx) {
        var hop = MessageTracing.RecordHop("notifications", "notifications-shared", "ParallelExecutor");
        // CallerMemberName: "OnOrderCreatedAsync"
        // CallerFilePath: ".../Sagas/OrderSaga.cs"
        // CallerLineNumber: (line where RecordHop is called)
    }
}
```

---

## Caller Info and Distributed Tracing

### Cross-Service Call Chain

```
Service A (OrdersService)
  └─ OrdersReceptor.HandleAsync:127
      └─ Creates OrderCreatedEvent

Service B (InventoryService)
  └─ InventoryReceptor.HandleAsync:89
      └─ Creates InventoryReservedEvent

Service C (NotificationsService)
  └─ EmailReceptor.HandleAsync:45
      └─ Creates EmailSentEvent
```

Each hop preserves **exact source location** across services:

```json
{
  "Hops": [
    {
      "ServiceName": "OrdersService",
      "CallerFilePath": "/src/Orders/Receptors/OrdersReceptor.cs",
      "CallerLineNumber": 127,
      "CallerMemberName": "HandleAsync"
    },
    {
      "ServiceName": "InventoryService",
      "CallerFilePath": "/src/Inventory/Receptors/InventoryReceptor.cs",
      "CallerLineNumber": 89,
      "CallerMemberName": "HandleAsync"
    },
    {
      "ServiceName": "NotificationsService",
      "CallerFilePath": "/src/Notifications/Receptors/EmailReceptor.cs",
      "CallerLineNumber": 45,
      "CallerMemberName": "HandleAsync"
    }
  ]
}
```

**Result**: Complete source-level traceability across microservices!

---

## Summary

Whizbang uses **C# compiler magic attributes** to capture source code locations:

- **[CallerMemberName]** - Method/property name
- **[CallerFilePath]** - Full file path
- **[CallerLineNumber]** - Line number

**Key Benefits**:
- **Zero runtime overhead** - compile-time only
- **Automatic capture** - no code changes needed
- **Complete traceability** - every hop knows its source
- **VSCode integration** - future extension can jump to code
- **Debugging** - find exact source of messages
- **Audit trails** - who/where/when messages created

**Best Practices**:
- Don't pass literal caller values
- Call `RecordHop` directly (not in lambdas)
- Test caller info is captured (not exact values)
- Use for debugging, not business logic

This enables **unprecedented visibility** into distributed systems, making debugging **as easy as local code**.
