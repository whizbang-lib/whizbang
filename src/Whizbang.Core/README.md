# Whizbang.Core

Core interfaces, types, and abstractions for the Whizbang library.

## What's In This Package

### Core Interfaces
- **`IReceptor<TMessage, TResponse>`** - Message handler interface
- **`IDispatcher`** - Message routing and orchestration
- **`IMessageContext`** - Message metadata and tracing

### Value Objects (Vogen-generated)
- **`MessageId`** - Unique message identifier
- **`CorrelationId`** - Workflow correlation
- **`CausationId`** - Causal chain tracking

### Exception Types
- **`HandlerNotFoundException`** - No handler found for message type

### Attributes
- **`WhizbangHandlerAttribute`** - Marks handlers for source generator discovery

## Design Principles

### Zero Reflection
All handler discovery and routing happens at compile-time via source generators. No reflection at runtime.

### Type Safety
Generic interfaces provide compile-time type checking:
```csharp
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async Task<OrderCreated> Receive(CreateOrder message) {
        // Type-safe: must return OrderCreated
    }
}
```

### AOT Compatible
Everything works with Native AOT compilation. No dynamic code generation.

### Flexible Response Types
Receptors support various response patterns:
- Single response: `Task<OrderCreated>`
- Multiple responses: `Task<(OrderCreated, AuditEvent)>`
- Dynamic responses: `Task<NotificationEvent[]>`
- Error handling: `Task<Result<OrderCreated>>`

## Usage

```csharp
using Whizbang.Core;
using Whizbang.Core.Attributes;

// Define your message
public record CreateOrder(Guid CustomerId, OrderItem[] Items);
public record OrderCreated(Guid OrderId, Guid CustomerId);

// Create a receptor
[WhizbangHandler]  // Discovered by source generator
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async Task<OrderCreated> Receive(CreateOrder message) {
        // Validation
        if (message.Items.Length == 0) {
            throw new InvalidOperationException("Order must have items");
        }

        // Business logic
        var orderId = Guid.NewGuid();

        // Return event
        return new OrderCreated(orderId, message.CustomerId);
    }
}
```

## Dependencies

- **Microsoft.Extensions.DependencyInjection.Abstractions** - DI abstractions
- **Vogen** - Source-generated value objects

## Version

**0.1.0** - Foundation release with stateless receptors and basic dispatcher
