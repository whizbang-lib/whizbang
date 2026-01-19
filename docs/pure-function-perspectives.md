# Pure Function Perspectives

**Status**: Production Ready (v0.1.0+)
**Namespace**: `Whizbang.Core.Perspectives`

## Overview

Pure function perspectives provide a **deterministic, testable, and AOT-compatible** approach to building read models from event streams. They enforce **compile-time purity** through synchronous signatures and runtime analysis via Roslyn analyzers.

## Table of Contents

1. [Pure Function Perspectives Overview](#pure-function-perspectives-overview)
2. [When to Use Pure Functions](#when-to-use-pure-functions)
3. [Getting Started](#getting-started)
4. [API Reference](#api-reference)
5. [Purity Enforcement](#purity-enforcement)
6. [Advanced Patterns](#advanced-patterns)
7. [Testing Pure Perspectives](#testing-pure-perspectives)
8. [Migration Guide](#migration-guide)

## Pure Function Perspectives Overview

Pure function perspectives provide a **deterministic, testable, and AOT-compatible** approach to building read models from event streams.

```csharp{
title: "Pure Function Perspective"
description: "Deterministic event processing with no side effects"
framework: "NET10"
category: "Perspectives"
difficulty: "BEGINNER"
tags: ["Perspectives", "Pure Functions", "Event Sourcing"]
testFile: "IPerspectiveForTests.cs"
testMethod: "Perspective_Apply_UpdatesModelDeterministicallyAsync"
}
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
  public OrderReadModel Apply(OrderReadModel currentData, OrderCreatedEvent @event) {
    // ✅ Pure function: no I/O, no side effects
    return currentData with {
      OrderId = @event.OrderId,
      Status = "Created",
      TotalAmount = @event.TotalAmount,
      UpdatedAt = @event.CreatedAt  // Use event timestamp!
    };
  }
}
```

**Key Characteristics:**
- ✅ **Synchronous** - Enforced by signature, no async/await
- ✅ **Deterministic** - Same inputs always produce same output
- ✅ **Easy to Test** - No mocks needed, simple unit tests
- ✅ **AOT Compatible** - Zero reflection, generated code only
- ✅ **Compile-time Purity** - Roslyn analyzer enforces rules
- ✅ **Event Sourcing** - Perfect for rebuilding state from events
- ✅ **Time Travel** - Replay events to any point in time

## When to Use Pure Functions

### ✅ Ideal Use Cases:

1. **Event Sourcing**: Rebuild state from event streams with guaranteed reproducibility
2. **Audit Trails**: Compliance and regulatory requirements for reproducible history
3. **Time Travel Debugging**: Replay events to any point in time for investigation
4. **Testing**: Simple, fast unit tests without mocks or test doubles
5. **Determinism**: Guarantee same inputs always produce same output
6. **AOT Deployment**: Target Native AOT with zero reflection
7. **Performance**: Minimize allocations and maximize throughput

### 💡 When to Consider Alternatives:

If you need **side effects** (logging, notifications, external API calls), consider:
- Handling side effects in command handlers before/after event dispatch
- Using separate event handlers for notifications (not perspectives)
- Building separate projection systems for real-time updates

**Key principle**: Perspectives are for building queryable state from events. Side effects belong elsewhere in your architecture.

## Getting Started

### 1. Define Your Model

```csharp{
title: "Perspective Model Definition"
description: "Define a read model with StreamKey attribute"
framework: "NET10"
category: "Perspectives"
difficulty: "BEGINNER"
tags: ["Perspectives", "Models", "StreamKey"]
}
using Whizbang.Core;

namespace MyApp.Models;

public record OrderReadModel {
  [StreamKey]  // Mark the partition key
  public required Guid OrderId { get; init; }

  public required string Status { get; init; }
  public required decimal TotalAmount { get; init; }
  public required DateTimeOffset CreatedAt { get; init; }
  public required DateTimeOffset UpdatedAt { get; init; }
}
```

### 2. Implement the Perspective

```csharp{
title: "Multi-Event Perspective Implementation"
description: "Implement IPerspectiveFor for multiple event types"
framework: "NET10"
category: "Perspectives"
difficulty: "BEGINNER"
tags: ["Perspectives", "Multiple Events", "Apply Methods"]
testFile: "IPerspectiveForTests.cs"
testMethod: "Perspective_MultipleEvents_AppliesCorrectlyAsync"
}
using Whizbang.Core.Perspectives;

namespace MyApp.Perspectives;

public class OrderPerspective :
  IPerspectiveFor<OrderReadModel, OrderCreatedEvent>,
  IPerspectiveFor<OrderReadModel, OrderShippedEvent> {

  public OrderReadModel Apply(OrderReadModel current, OrderCreatedEvent @event) {
    // First event - initialize model
    return new OrderReadModel {
      OrderId = @event.OrderId,
      Status = "Created",
      TotalAmount = @event.TotalAmount,
      CreatedAt = @event.CreatedAt,
      UpdatedAt = @event.CreatedAt
    };
  }

  public OrderReadModel Apply(OrderReadModel current, OrderShippedEvent @event) {
    // Subsequent events - update existing model
    return current with {
      Status = "Shipped",
      UpdatedAt = @event.ShippedAt
    };
  }
}
```

### 3. Register with DI

```csharp
services.AddWhizbang(builder => {
  builder.AddPerspective<OrderPerspective>();
  builder.AddPerspectiveStore<OrderReadModel>("orders");
});
```

### 4. The Runner is Generated!

The source generator creates an AOT-compatible runner:

```csharp
// Generated code (you don't write this!)
internal sealed class OrderPerspectiveRunner : IPerspectiveRunner {
  public async Task<PerspectiveCheckpointCompletion> RunAsync(...) {
    // ... load events from store ...

    foreach (var envelope in events) {
      // AOT-compatible switch (no reflection!)
      updatedModel = ApplyEvent(perspective, updatedModel, envelope.Message);
    }

    // Save once at the end (unit of work pattern)
    await _store.UpsertAsync(streamId, updatedModel, ct);
  }

  private OrderReadModel ApplyEvent(
      OrderPerspective perspective,
      OrderReadModel currentModel,
      IEvent @event) {

    // Generated switch statement (zero reflection)
    switch (@event) {
      case OrderCreatedEvent typedEvent:
        return perspective.Apply(currentModel, typedEvent);

      case OrderShippedEvent typedEvent:
        return perspective.Apply(currentModel, typedEvent);

      default:
        throw new InvalidOperationException(...);
    }
  }
}
```

## API Reference

### IPerspectiveFor<TModel, TEvent>

Single-stream perspective with one event type.

```csharp
public interface IPerspectiveFor<TModel, TEvent1>
  where TModel : class
  where TEvent1 : IEvent {

  /// <summary>
  /// Applies an event to the model and returns a new model.
  /// MUST be a pure function: no I/O, no side effects, deterministic.
  /// </summary>
  TModel Apply(TModel currentData, TEvent1 @event);
}
```

**Multiple Event Types:**

```csharp
public interface IPerspectiveFor<TModel, TEvent1, TEvent2>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent {

  TModel Apply(TModel currentData, TEvent1 @event);
  TModel Apply(TModel currentData, TEvent2 @event);
}

// Also available: IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3>
// TODO: Generate 4-50 event variants via source generator
```

### IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent>

Multi-stream perspective with partition key extraction.

```csharp
public interface IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1>
  where TModel : class
  where TPartitionKey : notnull
  where TEvent1 : IEvent {

  /// <summary>
  /// Extracts the partition key from the event.
  /// Events with the same partition key update the same model.
  /// </summary>
  TPartitionKey GetPartitionKey(TEvent1 @event);

  /// <summary>
  /// Applies the event to the model for this partition.
  /// MUST be a pure function.
  /// </summary>
  TModel Apply(TModel currentData, TEvent1 @event);
}
```

**Example: User Activity Across Streams**

```csharp
public class UserActivityPerspective :
  IGlobalPerspectiveFor<UserActivity, Guid, OrderCreatedEvent>,
  IGlobalPerspectiveFor<UserActivity, Guid, ProductViewedEvent> {

  // Extract user ID from order events
  public Guid GetPartitionKey(OrderCreatedEvent @event) => @event.UserId;

  // Extract user ID from product events
  public Guid GetPartitionKey(ProductViewedEvent @event) => @event.UserId;

  public UserActivity Apply(UserActivity current, OrderCreatedEvent @event) {
    return current with {
      OrderCount = current.OrderCount + 1,
      LastActivityAt = @event.CreatedAt
    };
  }

  public UserActivity Apply(UserActivity current, ProductViewedEvent @event) {
    return current with {
      ProductViewCount = current.ProductViewCount + 1,
      LastActivityAt = @event.ViewedAt
    };
  }
}
```

### IPerspectiveStore<TModel>

Partition key methods for multi-stream perspectives:

```csharp
public interface IPerspectiveStore<TModel> where TModel : class {
  // Existing methods
  Task<TModel?> GetByStreamIdAsync(string streamId, CancellationToken ct);
  Task UpsertAsync(string id, TModel model, CancellationToken ct);

  // NEW: Partition key methods
  Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(
    TPartitionKey partitionKey, CancellationToken ct)
    where TPartitionKey : notnull;

  Task UpsertByPartitionKeyAsync<TPartitionKey>(
    TPartitionKey partitionKey, TModel model, CancellationToken ct)
    where TPartitionKey : notnull;
}
```

**Partition Key Conversion:**

- `Guid` → Used as-is
- `string` → Deterministic MD5 hash to Guid
- `int`, `long`, etc. → `ToString()` then MD5 hash to Guid

## Purity Enforcement

### Roslyn Analyzer (WHIZ100-104)

The `PerspectivePurityAnalyzer` enforces purity at compile-time:

#### WHIZ100: Apply method returns Task

```csharp
// ❌ Error WHIZ100
public Task<OrderReadModel> Apply(OrderReadModel current, OrderEvent @event) {
  return Task.FromResult(current);
}

// ✅ Correct
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  return current;
}
```

**Fix**: Change return type from `Task<TModel>` to `TModel`.

#### WHIZ101: Apply method uses await

```csharp
// ❌ Error WHIZ101
public async OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  var data = await _someService.GetDataAsync();  // NO!
  return current with { Data = data };
}

// ✅ Correct
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  // Use data from the event, not from I/O
  return current with { Data = @event.Data };
}
```

**Fix**: Remove all `async`/`await` operations. Use event data only.

#### WHIZ102: Apply method calls database I/O

```csharp
// ❌ Error WHIZ102
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  var existing = _lens.GetByIdAsync(@event.OrderId).Result;  // NO!
  return current with { ... };
}

// ✅ Correct
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  // Use currentData parameter (already loaded by runner)
  return current with { ... };
}
```

**Fix**: Move database queries outside the Apply method. The runner loads the current model before calling Apply.

#### WHIZ103: Apply method calls HTTP operations

```csharp
// ❌ Error WHIZ103
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  var response = _httpClient.GetAsync("api/validate").Result;  // NO!
  return current with { ... };
}

// ✅ Correct
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  // Validation should happen BEFORE creating the event
  return current with { ... };
}
```

**Fix**: HTTP calls must happen outside Apply. Events should contain validated data.

#### WHIZ104: Apply uses DateTime.UtcNow (Warning)

```csharp
// ⚠️ Warning WHIZ104
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  return current with {
    UpdatedAt = DateTime.UtcNow  // Non-deterministic!
  };
}

// ✅ Correct
public OrderReadModel Apply(OrderReadModel current, OrderEvent @event) {
  return current with {
    UpdatedAt = @event.CreatedAt  // Deterministic from event
  };
}
```

**Fix**: Use timestamps from the event, not current time.

### Purity Rules Summary

Pure `Apply` methods MUST:
- ✅ Be synchronous (no `async`/`await`)
- ✅ Be deterministic (same inputs → same output)
- ✅ Have no side effects (no I/O, no mutations)
- ✅ Use event data only (no external dependencies)
- ✅ Use event timestamps (no `DateTime.UtcNow`)

Pure `Apply` methods MUST NOT:
- ❌ Return `Task` or `ValueTask`
- ❌ Use `await` keyword
- ❌ Call database/storage APIs
- ❌ Call HTTP/gRPC services
- ❌ Use `DateTime.UtcNow` or `DateTimeOffset.Now`
- ❌ Mutate shared state
- ❌ Log (logging happens in the runner)
- ❌ Send notifications (use separate event handler)

## Advanced Patterns

### Pattern: Preserving CreatedAt on Updates

```csharp
public OrderReadModel Apply(OrderReadModel current, OrderUpdatedEvent @event) {
  // Preserve CreatedAt from current model
  return current with {
    Status = @event.NewStatus,
    UpdatedAt = @event.UpdatedAt  // From event
    // CreatedAt remains unchanged (from current)
  };
}
```

### Pattern: Soft Deletes

```csharp
public OrderReadModel Apply(OrderReadModel current, OrderDeletedEvent @event) {
  return current with {
    DeletedAt = @event.DeletedAt,  // Set soft delete timestamp
    UpdatedAt = @event.DeletedAt
    // Keep all other data for audit trail
  };
}
```

### Pattern: Event Sequence Validation

```csharp
public OrderReadModel Apply(OrderReadModel current, OrderShippedEvent @event) {
  // Validate state transitions in Apply (throws if invalid)
  if (current.Status != "Paid") {
    throw new InvalidOperationException(
      $"Cannot ship order in status {current.Status}");
  }

  return current with {
    Status = "Shipped",
    ShippedAt = @event.ShippedAt,
    UpdatedAt = @event.ShippedAt
  };
}
```

### Pattern: Aggregate Root from Multiple Streams

```csharp
// Aggregate user activity from Order, Product, and Review streams
public class UserActivityPerspective :
  IGlobalPerspectiveFor<UserActivity, Guid, OrderCreatedEvent>,
  IGlobalPerspectiveFor<UserActivity, Guid, ProductViewedEvent>,
  IGlobalPerspectiveFor<UserActivity, Guid, ReviewPostedEvent> {

  public Guid GetPartitionKey(OrderCreatedEvent e) => e.UserId;
  public Guid GetPartitionKey(ProductViewedEvent e) => e.UserId;
  public Guid GetPartitionKey(ReviewPostedEvent e) => e.UserId;

  public UserActivity Apply(UserActivity current, OrderCreatedEvent e) {
    return current with {
      TotalOrders = current.TotalOrders + 1,
      TotalSpent = current.TotalSpent + e.TotalAmount,
      LastActivityAt = e.CreatedAt
    };
  }

  public UserActivity Apply(UserActivity current, ProductViewedEvent e) {
    return current with {
      ProductViewCount = current.ProductViewCount + 1,
      LastActivityAt = e.ViewedAt
    };
  }

  public UserActivity Apply(UserActivity current, ReviewPostedEvent e) {
    return current with {
      ReviewCount = current.ReviewCount + 1,
      LastActivityAt = e.PostedAt
    };
  }
}
```

## Testing Pure Perspectives

### Unit Testing

Pure functions are **trivial to test** - no mocks required!

```csharp
[Test]
public async Task Apply_OrderCreatedEvent_InitializesModelAsync() {
  // Arrange
  var perspective = new OrderPerspective();
  var emptyModel = new OrderReadModel();  // Start with empty
  var @event = new OrderCreatedEvent {
    OrderId = Guid.NewGuid(),
    TotalAmount = 99.99m,
    CreatedAt = DateTimeOffset.UtcNow
  };

  // Act - Pure function, no mocks needed!
  var result = perspective.Apply(emptyModel, @event);

  // Assert
  await Assert.That(result.OrderId).IsEqualTo(@event.OrderId);
  await Assert.That(result.TotalAmount).IsEqualTo(99.99m);
  await Assert.That(result.Status).IsEqualTo("Created");
}

[Test]
public async Task Apply_IsDeterministicAsync() {
  // Arrange
  var perspective = new OrderPerspective();
  var model = new OrderReadModel { Status = "Created" };
  var @event = new OrderShippedEvent { ShippedAt = DateTimeOffset.UtcNow };

  // Act - Call twice with same inputs
  var result1 = perspective.Apply(model, @event);
  var result2 = perspective.Apply(model, @event);

  // Assert - Pure function returns same result
  await Assert.That(result1).IsEqualTo(result2);
}

[Test]
public async Task Apply_DoesNotMutateInputAsync() {
  // Arrange
  var perspective = new OrderPerspective();
  var originalModel = new OrderReadModel { Status = "Created" };
  var @event = new OrderShippedEvent();

  // Act
  var result = perspective.Apply(originalModel, @event);

  // Assert - Original unchanged (immutability!)
  await Assert.That(originalModel.Status).IsEqualTo("Created");
  await Assert.That(result.Status).IsEqualTo("Shipped");
}
```

### Event Replay Testing

Test that replaying events produces correct final state:

```csharp
[Test]
public async Task EventReplay_ProducesCorrectFinalStateAsync() {
  // Arrange
  var perspective = new OrderPerspective();
  var model = new OrderReadModel();

  var events = new IEvent[] {
    new OrderCreatedEvent { OrderId = orderId, TotalAmount = 100m },
    new PaymentReceivedEvent { Amount = 100m },
    new OrderShippedEvent { ShippedAt = DateTimeOffset.UtcNow }
  };

  // Act - Replay all events
  foreach (var @event in events) {
    model = perspective.Apply(model, @event);
  }

  // Assert - Final state is correct
  await Assert.That(model.Status).IsEqualTo("Shipped");
  await Assert.That(model.TotalAmount).IsEqualTo(100m);
}
```

## Migration Guide

### Best Practices for Pure Function Perspectives

This guide covers common patterns and best practices when implementing pure function perspectives.

### Pattern: Minimal Dependencies

Pure function perspectives require **no injected dependencies**:

```csharp
// ✅ CORRECT - No dependencies
public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
  public OrderReadModel Apply(OrderReadModel current, OrderCreatedEvent @event) {
    return current with {
      OrderId = @event.OrderId,
      Status = "Created",
      TotalAmount = @event.TotalAmount,
      UpdatedAt = @event.CreatedAt
    };
  }
}

// ❌ INCORRECT - Dependencies not allowed
public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
  private readonly IPerspectiveStore<OrderReadModel> _store;  // NO!
  private readonly ILogger _logger;  // NO!

  public OrderReadModel Apply(OrderReadModel current, OrderCreatedEvent @event) {
    // ...
  }
}
```

### Pattern: Using Current State

The runner loads the current model before calling `Apply`. Use the `current` parameter, not database queries:

```csharp
// ✅ CORRECT - Use current parameter
public OrderReadModel Apply(OrderReadModel current, OrderCreatedEvent @event) {
  // The runner already loaded 'current' from the store
  // CreatedAt is automatically preserved from current
  return current with {
    OrderId = @event.OrderId,
    Status = "Created",
    UpdatedAt = @event.CreatedAt
  };
}

// ❌ INCORRECT - Don't query the store
public OrderReadModel Apply(OrderReadModel current, OrderCreatedEvent @event) {
  var existing = _lens.GetByIdAsync(@event.OrderId).Result;  // NO!
  return current with { ... };
}
```

### Pattern: Handling Side Effects

Side effects (logging, notifications, API calls) belong **outside** the perspective:

```csharp
// ✅ CORRECT - Pure function only
public class OrderPerspective : IPerspectiveFor<OrderReadModel, OrderCreatedEvent> {
  public OrderReadModel Apply(OrderReadModel current, OrderCreatedEvent @event) {
    return current with {
      OrderId = @event.OrderId,
      Status = "Created"
    };
  }
}

// Handle notifications in a separate event handler
public class OrderNotificationHandler : IEventHandler<OrderCreatedEvent> {
  private readonly IHubContext<OrderHub> _hubContext;

  public async Task HandleAsync(OrderCreatedEvent @event, CancellationToken ct) {
    await _hubContext.Clients.All.SendAsync("OrderCreated", @event, ct);
  }
}
```

### Implementation Checklist

When implementing a pure function perspective:

- [ ] Use `IPerspectiveFor<TModel, TEvent>` interface
- [ ] Keep `Apply` method synchronous (no `async`/`await`)
- [ ] Return `TModel`, not `Task<TModel>`
- [ ] Remove all I/O operations (database, HTTP, file system)
- [ ] Remove injected dependencies (DI constructor parameters)
- [ ] Use `current` parameter instead of database queries
- [ ] Use event timestamps instead of `DateTime.UtcNow`
- [ ] Move side effects to separate event handlers
- [ ] Add `[StreamKey]` attribute to model partition key property
- [ ] Run analyzer to verify purity (WHIZ100-104 diagnostics)
- [ ] Write simple unit tests (no mocks needed!)

## Best Practices

### ✅ DO:

- **Use immutable records** for models (`record` with `init` properties)
- **Use `with` expressions** for updates
- **Use event timestamps** for all time values
- **Throw exceptions** for invalid state transitions (fail-fast)
- **Keep Apply methods simple** (single responsibility)
- **Test replay scenarios** (multiple events in sequence)

### ❌ DON'T:

- **Don't use classes** for models (use records for immutability)
- **Don't mutate current** (return new instance)
- **Don't depend on external state** (only event + current)
- **Don't log in Apply** (logging happens in runner)
- **Don't catch exceptions** (let them propagate for debugging)
- **Don't call other services** (pure function only!)

## Performance Considerations

### AOT Compilation

Pure function perspectives generate **zero-reflection** code:

```csharp
// Generated switch statement (AOT-friendly)
switch (@event) {
  case OrderCreatedEvent typedEvent:
    return perspective.Apply(currentModel, typedEvent);
  // No GetMethod(), no Invoke() - direct calls only!
}
```

### Memory Efficiency

- Uses `record` with structural equality (efficient caching)
- `with` expressions create minimal copies (only changed fields)
- No Task allocation (synchronous execution)

### Unit of Work Pattern

The generated runner uses a **unit of work** pattern:

1. Load current model from store (one read)
2. Apply ALL events in-memory (fast, pure functions)
3. Save final model to store (one write)

This is much faster than saving after each event!

## Troubleshooting

### "Apply method returns Task" (WHIZ100)

**Problem**: Method signature is async.

**Solution**: Remove `async` and change return type from `Task<TModel>` to `TModel`.

### "Apply method uses await" (WHIZ101)

**Problem**: Using `await` inside Apply.

**Solution**: Remove all async operations. Use event data directly.

### "Apply method calls database I/O" (WHIZ102)

**Problem**: Calling `IPerspectiveStore`, `ILensQuery`, or DbContext.

**Solution**: Use the `currentData` parameter (runner loads it for you).

### "Apply method calls HTTP operations" (WHIZ103)

**Problem**: Using `HttpClient` or HTTP libraries.

**Solution**: Move HTTP calls outside Apply. Validate before creating events.

### "Apply uses DateTime.UtcNow" (WHIZ104)

**Problem**: Using current time instead of event time.

**Solution**: Use `@event.CreatedAt` or `@event.Timestamp` from the event.

### CreatedAt not preserved on updates

**Problem**: Overwriting CreatedAt timestamp.

**Solution**: Use `current.CreatedAt` (from parameter) for updates:

```csharp
return current with {
  UpdatedAt = @event.UpdatedAt,
  // CreatedAt automatically preserved from 'current'
};
```

## See Also

- [EF Core Storage Implementation](./efcore-storage-implementation.md)
- [Testing Guide](./tdd-workflow.md)
- Source code: `src/Whizbang.Core/Perspectives/IPerspectiveFor.cs`
- Tests: `tests/Whizbang.Core.Tests/Perspectives/IPerspectiveForTests.cs`
- Analyzer: `src/Whizbang.Generators/PerspectivePurityAnalyzer.cs`

---

**Last Updated**: 2025-01-19
**Version**: v0.1.0
