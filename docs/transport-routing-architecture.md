# Transport Routing Architecture

**Status**: Design Document
**Version**: v0.4.0 (Owned-Domain Routing)
**Last Updated**: 2026-03-29
**Owner**: Phil Carbone

---

## Executive Summary

This document defines the **policy-based transport routing architecture** for Whizbang. It describes how services:
- **Publish** messages to remote transports (Kafka, Service Bus, RabbitMQ)
- **Subscribe** to remote transports based on local receptors
- Handle **consumer groups** (Kafka) and **topic subscriptions** (Service Bus)
- Enable **auto-discovery** with zero manual subscription management

**Key Principle**: Policies define routing. Services declare what they publish and subscribe to. Whizbang handles the rest.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Policy-Based Routing](#policy-based-routing)
3. [Owned-Domain Routing](#owned-domain-routing)
4. [Transport Models](#transport-models)
4. [Auto-Discovery](#auto-discovery)
5. [Complete Examples](#complete-examples)
6. [Sequence Diagrams](#sequence-diagrams)
7. [API Reference](#api-reference)

---

## Architecture Overview

### The Key Insight: Local vs Remote Policies

Each service has **its own local policies** that define:
- **Publishing (Outbound)**: Where messages are published when created locally
- **Subscribing (Inbound)**: Where to listen for messages this service can handle

```
┌─────────────────┐                    ┌─────────────────┐
│  Order Service  │                    │ Inventory Svc   │
├─────────────────┤                    ├─────────────────┤
│ Policy:         │                    │ Policy:         │
│ OrderCreated    │                    │ OrderCreated    │
│  → Publish to   │   Kafka Topic:     │  ← Subscribe    │
│     "orders"    │   "orders-topic"   │     from        │
│                 │                    │     "orders"    │
└─────────────────┘                    └─────────────────┘
```

### Three-Layer Architecture

```
┌──────────────────────────────────────────────┐
│           Application Layer                  │
│  (Receptors, Commands, Events, Queries)      │
└──────────────────┬───────────────────────────┘
                   │
┌──────────────────▼───────────────────────────┐
│         Policy Engine Layer                  │
│  (Routing decisions, where to pub/sub)       │
└──────────────────┬───────────────────────────┘
                   │
┌──────────────────▼───────────────────────────┐
│      Transport Bridge Layer                  │
│  (DispatcherTransportBridge, serialization)  │
└──────────────────┬───────────────────────────┘
                   │
┌──────────────────▼───────────────────────────┐
│         Transport Layer                      │
│  (Kafka, ServiceBus, RabbitMQ, EventStore)   │
└──────────────────────────────────────────────┘
```

---

## Policy-Based Routing

### Publishing Configuration

Define **where messages are published** when created locally:

```csharp
// Order Service - Publisher
builder.Services.AddWhizbang(options => {
    options.Transports.AddKafka("localhost:9092");

    options.Policies.When(ctx => ctx.Message is OrderCreated)
        .Then(config => config
            .UseTopic("orders") // Logical topic
            .PublishToKafka("orders-topic") // Kafka topic
            .PublishToServiceBus("orders-topic") // ServiceBus topic (fan-out)
        );
});
```

**Multiple Transports**: Messages can be published to multiple transports simultaneously (fan-out).

### Subscription Configuration

Define **where to subscribe** for messages this service can handle:

```csharp
// Inventory Service - Subscriber
builder.Services.AddWhizbang(options => {
    options.Transports.AddKafka("localhost:9092");

    options.Policies.When(ctx => ctx.Message is OrderCreated)
        .Then(config => config
            .SubscribeFromKafka(
                topic: "orders-topic",
                consumerGroup: "inventory-service" // Load balancing
            )
        );

    // Auto-subscribe: discovers OrderCreatedReceptor, uses policy above
    options.Transports.AutoSubscribe(discovery => {
        discovery.DiscoverReceptors();
    });
});

// Local receptor handles incoming messages
public class OrderCreatedReceptor : IReceptor<OrderCreated> {
    public async Task ReceiveAsync(OrderCreated message) {
        await _inventory.ReserveAsync(message.Items);
    }
}
```

### Policy Resolution Flow

```
Message Created → Policy Matches → PublishTargets → Transport Bridge → Kafka/ServiceBus/etc.
                                                                     ↓
Message Arrives ← Dispatcher ← Bridge ← Transport ← Kafka/ServiceBus/etc.
                       ↓
                    Receptor
```

---

## Owned-Domain Routing

When a service declares domain ownership via `RoutingOptions.OwnDomains()`, Whizbang enforces **asymmetric routing** — events and commands in owned namespaces are handled locally and never published to the transport.

### How It Works

Events and commands flow through the outbox for event store persistence and perspective processing. The **destination** field on the outbox message controls whether the transport publishes it:

| Scenario | Destination | Outbox Table | Event Store | Transport |
|----------|-------------|:------------:|:-----------:|:---------:|
| Non-owned event | `"orders"` | Yes | Yes | Yes |
| Owned event | `null` | Yes | Yes | **No** |
| Non-owned command (no local receptor) | `"inbox"` | Yes | — | Yes |
| Owned command (no local receptor) | — | **No** | — | **No** |

When `_resolveEventTopic()` detects an event in an owned namespace, it returns `null`. The existing `TransportPublishStrategy` already skips transport for null destinations (event-store-only path).

### Namespace Matching

Ownership uses **hierarchical matching** — the same logic as `EventSubscriptionDiscovery`:

```csharp
// Exact match
routing.OwnDomains("JDX.Contracts.Chat");
// → "JDX.Contracts.Chat" events are owned

// Child namespaces are also owned
// → "JDX.Contracts.Chat.Common" is owned too
```

### Configuration

```csharp
services.AddWhizbang()
    .WithRouting(routing => {
        routing
            .OwnDomains("MyApp.Orders", "MyApp.Users")  // events/commands in these namespaces stay local
            .SubscribeTo("MyApp.Payments.Events");       // subscribe to events from other services
    });
```

### Opting Out: Explicit Cross-Service Broadcast

If an owned event genuinely needs cross-service delivery, use `Route.Outbox()` or `Route.Both()` explicitly in the receptor return:

```csharp
// Receptor in OrderService (owns "MyApp.Orders")
public class PlaceOrderHandler : IReceptor<PlaceOrderCommand, Routed<OrderCreatedEvent>> {
    public ValueTask<Routed<OrderCreatedEvent>> HandleAsync(PlaceOrderCommand message, CancellationToken ct) {
        var @event = new OrderCreatedEvent { OrderId = message.OrderId };
        // Explicitly broadcast to other services despite being in an owned namespace
        return Route.Both(@event).AsValueTask();
    }
}
```

### Why This Matters

Without owned-domain routing, every event produced by a service's own receptors goes to the transport — even though no other service subscribes to it. This causes:

1. **Outbox flooding** — thousands of unnecessary outbox messages with valid destinations
2. **Queue backpressure** — downstream queues (e.g., BFF) fill up with events they don't need
3. **Self-consumption loops** — if subscription filtering has gaps, the service processes its own events again, creating cascading event storms

### Transport Echo Suppression

When a service publishes an event, the transport (RabbitMQ, Service Bus, etc.) broadcasts it to **all** subscribers — including the originating service itself. The `TransportConsumerWorker` suppresses these echo messages at the transport consumer layer, before they reach the inbox.

**Events and commands are suppressed differently:**

| Message Type | Owned Namespace? | Echo Detection | Discard? |
|--------------|:----------------:|----------------|:--------:|
| Event | Yes | **Unconditional** — owned events only exist because this service published them | Always |
| Event | No | None — from another service | Never |
| Command | Yes | **Hop-based** — check if last hop's service name matches this service | Only if self-echo |
| Command | No | None — routed to this service intentionally | Never |

**Why the asymmetry?**

- **Events** in an owned namespace can *only* have been published by this service (the namespace owner). When they arrive from the transport, they are always echo — the service already processed them locally via the fast path (`LocalImmediateDetached`). No hop inspection is needed.

- **Commands** in an owned namespace may arrive from other services via cross-service dispatch (e.g., BffService sends a command to ChatService). The transport consumer checks the last hop's service name: if it matches this service, it's self-echo; if it's a different service, the command is legitimate and must be processed.

```
Event published by ChatService
  ├── Local fast path: ChatService processes immediately (LocalImmediateDetached)
  └── Transport broadcast: arrives at ChatService inbox → DISCARDED (owned event echo)
                           arrives at BffService inbox → PROCESSED (cross-service delivery)

Command sent by BffService to ChatService's namespace
  └── Transport delivery: arrives at ChatService inbox → PROCESSED (hop says "BffService")

Command sent by ChatService to its own namespace
  └── Transport delivery: arrives at ChatService inbox → DISCARDED (hop says "ChatService")
```

### Related

- **Source code**: [`src/Whizbang.Core/Dispatcher.cs` — `_isOwnedNamespace()`, `_resolveEventTopic()`](../src/Whizbang.Core/Dispatcher.cs)
- **Source code**: [`src/Whizbang.Core/Workers/TransportConsumerWorker.cs` — echo suppression in batch and single-message handlers](../src/Whizbang.Core/Workers/TransportConsumerWorker.cs)
- **Tests**: [`tests/Whizbang.Core.Tests/Dispatcher/DispatcherOwnedDomainTests.cs`](../tests/Whizbang.Core.Tests/Dispatcher/DispatcherOwnedDomainTests.cs)
- **Tests**: [`tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerOwnedEventDiscardTests.cs`](../tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerOwnedEventDiscardTests.cs)
- **Subscription filtering**: [`src/Whizbang.Core/Routing/EventSubscriptionDiscovery.cs`](../src/Whizbang.Core/Routing/EventSubscriptionDiscovery.cs)

---

## Transport Models

Different transports have different subscription models. Whizbang abstracts these differences while exposing transport-specific configuration.

### Kafka: Topic + Consumer Group

**Model**: Topics have partitions. Consumer groups share partition load.

```csharp
.SubscribeFromKafka(
    topic: "orders-topic",
    consumerGroup: "inventory-service",
    partition: null // Auto-assigned by Kafka
)
```

**Behavior**:
- **Same consumer group** = Load balancing (one instance processes message)
- **Different consumer groups** = Fan-out (all groups get message)

**Scaling Example**:

```
Orders Topic (3 partitions)
├── Partition 0
├── Partition 1
└── Partition 2

Consumer Group: inventory-service
├── Instance 1 → Partitions [0, 1]
└── Instance 2 → Partition [2]

Consumer Group: shipping-service
├── Instance 1 → Partition [0]
├── Instance 2 → Partition [1]
└── Instance 3 → Partition [2]
```

Both `inventory-service` and `shipping-service` get **all messages** (different consumer groups). Within each group, instances **share the load** (partition assignment).

### Azure Service Bus: Topic + Subscription

**Model**: Topics have subscriptions. Each subscription gets a copy of the message.

```csharp
.SubscribeFromServiceBus(
    topic: "orders-topic",
    subscriptionName: "inventory-sub",
    sqlFilter: null // Optional SQL filter
)
```

**Behavior**:
- **Different subscriptions** = Fan-out (each subscription gets message)
- **Same subscription** = Load balancing (instances compete for message)

**Scaling Example**:

```
Orders Topic
├── Subscription: inventory-sub
│   ├── Instance 1 (competes)
│   └── Instance 2 (competes)
└── Subscription: shipping-sub
    ├── Instance 1 (competes)
    └── Instance 2 (competes)
```

Both `inventory-sub` and `shipping-sub` get **all messages** (different subscriptions). Within each subscription, instances **compete** (PeekLock ensures at-most-once processing).

**SQL Filters** (Optional):

```csharp
.SubscribeFromServiceBus(
    topic: "orders-topic",
    subscriptionName: "high-priority-orders",
    sqlFilter: "Priority > 5"
)
```

### RabbitMQ: Exchange + Queue + Routing Key

**Model**: Exchanges route to queues based on routing keys.

```csharp
.SubscribeFromRabbitMQ(
    exchange: "orders",
    queueName: "inventory-queue",
    routingKey: "order.created"
)
```

**Behavior**:
- **Different queues** = Fan-out (each queue gets message if routing key matches)
- **Same queue** = Load balancing (instances compete for message)

**Scaling Example**:

```
Exchange: orders (type: topic)
├── Queue: inventory-queue (routing: "order.created")
│   ├── Instance 1 (competes)
│   └── Instance 2 (competes)
└── Queue: shipping-queue (routing: "order.created")
    ├── Instance 1 (competes)
    └── Instance 2 (competes)
```

Both queues get messages matching `order.created` (different queues). Within each queue, instances **compete**.

### Infrastructure Mapping

| Whizbang Concept | Kafka | Service Bus | RabbitMQ |
|------------------|-------|-------------|----------|
| **Topic** | Topic | Topic | Exchange |
| **Subscription** | Consumer Group | Subscription | Queue |
| **Load Balancing** | Partition assignment | PeekLock competition | Message consumption |
| **Fan-Out** | Multiple consumer groups | Multiple subscriptions | Multiple queues |
| **Filtering** | Client-side | SQL filters | Routing keys |

---

## Auto-Discovery

### Discovery Modes

**1. Discover All Receptors** (Recommended)

```csharp
options.Transports.AutoSubscribe(discovery => {
    discovery.DiscoverReceptors(); // Scans for IReceptor<T>, subscribes based on policies
});
```

**2. Namespace Patterns**

```csharp
options.Transports.AutoSubscribe(discovery => {
    discovery.SubscribeToNamespace("MyApp.Orders.*");
    discovery.SubscribeToNamespace("MyApp.Payments.*");
    discovery.SubscribeToNamespace("*.Events");
});
```

**3. Explicit Types**

```csharp
options.Transports.AutoSubscribe(discovery => {
    discovery.Subscribe<OrderCreated>();
    discovery.Subscribe<PaymentProcessed>();
});
```

### Auto-Discovery Flow

```mermaid
sequenceDiagram
    participant App as Application Startup
    participant AD as AutoDiscovery
    participant SG as Source Generator
    participant PE as PolicyEngine
    participant TM as TransportManager
    participant Bridge as DispatcherTransportBridge
    participant Kafka as Kafka Transport

    App->>AD: AutoSubscribe(DiscoverReceptors)
    AD->>SG: Get all IReceptor implementations
    SG-->>AD: [OrderCreatedReceptor, PaymentReceptor]

    AD->>AD: Extract message types
    Note over AD: [OrderCreated, PaymentProcessed]

    loop For each message type
        AD->>PE: GetPolicy(messageType)
        PE-->>AD: PolicyConfig with SubscriptionTargets

        loop For each subscription target
            AD->>TM: GetTransport(transportType)
            TM-->>AD: Transport instance

            AD->>Bridge: SubscribeFromTransportAsync(messageType, destination)
            Bridge->>Kafka: SubscribeAsync(topic, consumerGroup, handler)
            Kafka-->>Bridge: ISubscription
        end
    end

    Note over App,Kafka: Service now subscribed to all topics<br/>for messages it can handle
```

### Namespace Pattern Matching

```csharp
// Pattern: "MyApp.Orders.*"
// Matches:
//   MyApp.Orders.OrderCreated ✓
//   MyApp.Orders.OrderUpdated ✓
//   MyApp.Orders.Commands.CreateOrder ✓
//   MyApp.Payments.PaymentProcessed ✗

// Pattern: "*.Events"
// Matches:
//   MyApp.Orders.Events.OrderCreated ✓
//   MyApp.Payments.Events.PaymentProcessed ✓
//   MyApp.Orders.OrderCreated ✗ (no ".Events" suffix)

// Pattern: "MyApp.*.*"
// Matches:
//   MyApp.Orders.OrderCreated ✓
//   MyApp.Payments.PaymentProcessed ✓
//   MyApp.OrderCreated ✗ (only one dot after MyApp)
```

---

## Complete Examples

### Example 1: Simple Kafka Fan-Out

**Order Service** (Publisher):

```csharp
builder.Services.AddWhizbang(options => {
    options.Transports.AddKafka("localhost:9092");

    options.Policies.When(ctx => ctx.Message is OrderCreated)
        .Then(config => config
            .PublishToKafka("orders-topic")
        );
});

// Somewhere in a receptor or command handler
await _dispatcher.PublishAsync(new OrderCreated { OrderId = "123", Items = [...] });
```

**Inventory Service** (Subscriber 1):

```csharp
builder.Services.AddWhizbang(options => {
    options.Transports.AddKafka("localhost:9092");

    options.Policies.When(ctx => ctx.Message is OrderCreated)
        .Then(config => config
            .SubscribeFromKafka("orders-topic", "inventory-service")
        );

    options.Transports.AutoSubscribe(discovery => {
        discovery.DiscoverReceptors();
    });
});

public class OrderCreatedReceptor : IReceptor<OrderCreated> {
    public async Task ReceiveAsync(OrderCreated message) {
        await _inventory.ReserveAsync(message.Items);
    }
}
```

**Shipping Service** (Subscriber 2):

```csharp
builder.Services.AddWhizbang(options => {
    options.Transports.AddKafka("localhost:9092");

    options.Policies.When(ctx => ctx.Message is OrderCreated)
        .Then(config => config
            .SubscribeFromKafka("orders-topic", "shipping-service") // Different group
        );

    options.Transports.AutoSubscribe(discovery => {
        discovery.DiscoverReceptors();
    });
});

public class OrderCreatedReceptor : IReceptor<OrderCreated> {
    public async Task ReceiveAsync(OrderCreated message) {
        await _shipping.PrepareShipmentAsync(message);
    }
}
```

**Result**: OrderCreated message delivered to **both** Inventory and Shipping services (different consumer groups = fan-out).

### Example 2: Multi-Transport Fan-Out

**Order Service** publishes to **both Kafka and Service Bus**:

```csharp
options.Policies.When(ctx => ctx.Message is OrderCreated)
    .Then(config => config
        .PublishToKafka("orders-topic")
        .PublishToServiceBus("orders-topic")
    );
```

**Inventory Service** subscribes from **Kafka**:

```csharp
options.Policies.When(ctx => ctx.Message is OrderCreated)
    .Then(config => config
        .SubscribeFromKafka("orders-topic", "inventory-service")
    );
```

**Analytics Service** subscribes from **Service Bus**:

```csharp
options.Policies.When(ctx => ctx.Message is OrderCreated)
    .Then(config => config
        .SubscribeFromServiceBus("orders-topic", "analytics-sub")
    );
```

**Result**: OrderCreated message delivered via **two independent transports** (Kafka → Inventory, ServiceBus → Analytics).

### Example 3: Service Bus with SQL Filters

**Order Service** publishes with metadata:

```csharp
var orderCreated = new OrderCreated {
    OrderId = "123",
    Priority = 10 // High priority
};

await _dispatcher.PublishAsync(orderCreated);
```

**Standard Processing**:

```csharp
options.Policies.When(ctx => ctx.Message is OrderCreated)
    .Then(config => config
        .SubscribeFromServiceBus("orders-topic", "standard-processing")
    );
```

**High-Priority Processing**:

```csharp
options.Policies.When(ctx => ctx.Message is OrderCreated)
    .Then(config => config
        .SubscribeFromServiceBus(
            topic: "orders-topic",
            subscriptionName: "high-priority-processing",
            sqlFilter: "Priority > 5" // SQL filter expression
        )
    );
```

**Result**: Standard processing gets **all orders**. High-priority processing gets **only orders with Priority > 5**.

### Example 4: Namespace-Based Auto-Discovery

**Multiple Message Types**:

```csharp
namespace MyApp.Orders.Events {
    public record OrderCreated;
    public record OrderShipped;
    public record OrderCancelled;
}

namespace MyApp.Payments.Events {
    public record PaymentReceived;
    public record RefundIssued;
}
```

**Service Configuration**:

```csharp
// Policies for all order events
options.Policies.When(ctx => ctx.MessageType.Namespace == "MyApp.Orders.Events")
    .Then(config => config
        .SubscribeFromKafka("orders-topic", "my-service")
    );

// Policies for all payment events
options.Policies.When(ctx => ctx.MessageType.Namespace == "MyApp.Payments.Events")
    .Then(config => config
        .SubscribeFromKafka("payments-topic", "my-service")
    );

// Auto-discover by namespace
options.Transports.AutoSubscribe(discovery => {
    discovery.SubscribeToNamespace("MyApp.Orders.Events.*");
    discovery.SubscribeToNamespace("MyApp.Payments.Events.*");
});
```

**Result**: Service automatically subscribes to **all event types** in those namespaces, even as new event types are added.

---

## Sequence Diagrams

### Publishing Flow (Kafka)

```mermaid
sequenceDiagram
    participant Receptor as OrderReceptor
    participant Dispatcher as IDispatcher
    participant PE as PolicyEngine
    participant Bridge as DispatcherTransportBridge
    participant Kafka as Kafka Transport

    Receptor->>Dispatcher: PublishAsync(OrderCreated)
    Dispatcher->>PE: GetPolicy(OrderCreated)
    PE-->>Dispatcher: PolicyConfig { PublishTo: [Kafka("orders-topic")] }

    loop For each PublishTarget
        Dispatcher->>Bridge: PublishToTransportAsync(message, KafkaTransport, "orders-topic")
        Bridge->>Bridge: CreateEnvelope(message, context)
        Note over Bridge: MessageId, CorrelationId,<br/>Hops, Serialization
        Bridge->>Kafka: PublishAsync(envelope, "orders-topic")
        Kafka-->>Bridge: Acknowledgment
    end

    Bridge-->>Dispatcher: Published
    Dispatcher-->>Receptor: Completed
```

### Subscribing Flow (Kafka with Consumer Group)

```mermaid
sequenceDiagram
    participant App as Inventory Service
    participant AD as AutoDiscovery
    participant PE as PolicyEngine
    participant TM as TransportManager
    participant Kafka as Kafka Transport
    participant Bridge as DispatcherTransportBridge
    participant Dispatcher as IDispatcher
    participant Receptor as OrderCreatedReceptor

    App->>AD: AutoSubscribe(DiscoverReceptors)
    AD->>AD: Scan for IReceptor implementations
    Note over AD: Found: OrderCreatedReceptor

    AD->>PE: GetPolicy(typeof(OrderCreated))
    PE-->>AD: PolicyConfig { SubscribeFrom: [Kafka("orders-topic", "inventory-service")] }

    AD->>TM: GetTransport(TransportType.Kafka)
    TM-->>AD: KafkaTransport instance

    AD->>Bridge: SubscribeFromTransportAsync<OrderCreated>("orders-topic")
    Note over Bridge: Passes consumerGroup = "inventory-service"

    Bridge->>Kafka: SubscribeAsync(destination, handler)
    Note over Kafka: Creates Kafka consumer<br/>group.id = "inventory-service"<br/>subscribes to "orders-topic"

    Kafka-->>Bridge: ISubscription
    Bridge-->>AD: Subscription created

    Note over App,Receptor: Service now listening...

    Kafka->>Bridge: Message received (envelope)
    Bridge->>Bridge: Deserialize envelope
    Bridge->>Dispatcher: SendAsync(OrderCreated)
    Dispatcher->>Receptor: ReceiveAsync(OrderCreated)
    Receptor-->>Dispatcher: Completed
    Dispatcher-->>Bridge: Completed
    Bridge->>Kafka: Acknowledge/Commit offset
```

### Multi-Transport Fan-Out

```mermaid
sequenceDiagram
    participant Receptor as OrderReceptor
    participant Dispatcher as IDispatcher
    participant PE as PolicyEngine
    participant Bridge1 as Bridge (Kafka)
    participant Bridge2 as Bridge (ServiceBus)
    participant Kafka as Kafka
    participant SB as Service Bus

    Receptor->>Dispatcher: PublishAsync(OrderCreated)
    Dispatcher->>PE: GetPolicy(OrderCreated)
    PE-->>Dispatcher: PolicyConfig {<br/>PublishTo: [Kafka, ServiceBus]<br/>}

    par Publish to Kafka
        Dispatcher->>Bridge1: PublishToTransportAsync(Kafka)
        Bridge1->>Kafka: PublishAsync("orders-topic")
    and Publish to Service Bus
        Dispatcher->>Bridge2: PublishToTransportAsync(ServiceBus)
        Bridge2->>SB: PublishAsync("orders-topic")
    end

    Note over Kafka,SB: Message available on both transports
```

---

## API Reference

### PolicyConfiguration - Publishing

```csharp
public class PolicyConfiguration {
    // Publish to Kafka topic
    public PolicyConfiguration PublishToKafka(string topic);

    // Publish to Service Bus topic
    public PolicyConfiguration PublishToServiceBus(string topic);

    // Publish to RabbitMQ exchange with routing key
    public PolicyConfiguration PublishToRabbitMQ(string exchange, string routingKey);

    // Dynamic destination based on context
    public PolicyConfiguration PublishToKafka(Func<PolicyContext, string> topicSelector);
}
```

### PolicyConfiguration - Subscribing

```csharp
public class PolicyConfiguration {
    // Subscribe from Kafka topic with consumer group
    public PolicyConfiguration SubscribeFromKafka(
        string topic,
        string consumerGroup,
        int? partition = null // Optional: specific partition
    );

    // Subscribe from Service Bus topic with subscription name
    public PolicyConfiguration SubscribeFromServiceBus(
        string topic,
        string subscriptionName,
        string? sqlFilter = null // Optional: SQL filter expression
    );

    // Subscribe from RabbitMQ exchange with queue and routing key
    public PolicyConfiguration SubscribeFromRabbitMQ(
        string exchange,
        string queueName,
        string? routingKey = null
    );
}
```

### TransportAutoDiscovery

```csharp
public class TransportAutoDiscovery {
    // Discover all IReceptor implementations and subscribe
    public void DiscoverReceptors();

    // Subscribe based on namespace pattern
    // Patterns: "MyApp.Orders.*", "*.Events", "MyApp.*.*"
    public void SubscribeToNamespace(string pattern);

    // Explicitly subscribe to specific message type
    public void Subscribe<TMessage>();
}
```

### WhizbangOptions - Transport Setup

```csharp
public class WhizbangOptions {
    // Add Kafka transport
    public void AddKafka(string bootstrapServers);

    // Add Azure Service Bus transport
    public void AddServiceBus(string connectionString);

    // Add RabbitMQ transport
    public void AddRabbitMQ(string hostname, int port = 5672);

    // Configure auto-discovery
    public void AutoSubscribe(Action<TransportAutoDiscovery> configure);
}
```

### PublishTarget and SubscriptionTarget

```csharp
public record PublishTarget {
    public TransportType TransportType { get; init; }
    public string Destination { get; init; } // Topic/Queue/Exchange
    public string? RoutingKey { get; init; } // RabbitMQ routing key
}

public record SubscriptionTarget {
    public TransportType TransportType { get; init; }
    public string Topic { get; init; } // Kafka topic, ServiceBus topic, RabbitMQ exchange
    public string? ConsumerGroup { get; init; } // Kafka consumer group
    public string? SubscriptionName { get; init; } // ServiceBus subscription
    public string? QueueName { get; init; } // RabbitMQ queue
    public string? RoutingKey { get; init; } // RabbitMQ routing key
    public string? SqlFilter { get; init; } // ServiceBus SQL filter
    public int? Partition { get; init; } // Kafka partition (optional)
}

public enum TransportType {
    Kafka,
    ServiceBus,
    RabbitMQ,
    EventStore,
    InProcess
}
```

---

## Design Decisions

### Why Policy-Based Routing?

**Alternative 1: Convention-Based Routing**
```csharp
// Messages go to topic named after message type
OrderCreated → "OrderCreated" topic
```
❌ **Problem**: Inflexible, can't route to different topics per environment, can't fan-out to multiple transports.

**Alternative 2: Attribute-Based Routing**
```csharp
[PublishTo("orders-topic")]
public record OrderCreated;
```
❌ **Problem**: Couples message to transport, can't change routing at runtime, can't route differently per service.

**Alternative 3: Policy-Based Routing** (✅ Chosen)
```csharp
options.Policies.When(ctx => ctx.Message is OrderCreated)
    .Then(config => config.PublishToKafka("orders-topic"));
```
✅ **Benefits**:
- Decoupled: Messages don't know about transports
- Flexible: Different routing per service, environment, context
- Testable: Override policies in tests
- Multi-transport: Publish to multiple transports simultaneously
- Dynamic: Route based on message content, context, metadata

### Why Auto-Discovery?

**Without Auto-Discovery**:
```csharp
// Manual subscription management (error-prone)
await bridge.SubscribeFromTransportAsync<OrderCreated>(destination);
await bridge.SubscribeFromTransportAsync<OrderUpdated>(destination);
await bridge.SubscribeFromTransportAsync<OrderCancelled>(destination);
// Easy to forget one, hard to maintain
```

**With Auto-Discovery**:
```csharp
// Just write receptors, subscriptions happen automatically
options.Transports.AutoSubscribe(discovery => {
    discovery.DiscoverReceptors(); // Done!
});
```

✅ **Benefits**:
- Zero boilerplate
- Can't forget to subscribe
- Add receptor → subscription happens automatically
- Source generator provides compile-time safety
- Namespace patterns scale to dozens/hundreds of message types

### Why Consumer Groups / Subscriptions?

Different use cases require different delivery guarantees:

**Load Balancing** (Same consumer group/subscription):
- Multiple instances of same service
- Share partition/message load
- Horizontal scaling

**Fan-Out** (Different consumer groups/subscriptions):
- Multiple different services
- Each gets every message
- Event-driven architecture

Whizbang supports both via explicit configuration in policies.

---

## Future Enhancements

### v0.4.0: Dynamic Configuration

```csharp
// Reload policies at runtime
await _policyManager.ReloadPoliciesAsync();

// Add subscription dynamically
await _transportManager.AddSubscriptionAsync<NewEvent>(destination);
```

### v0.5.0: Transport Adapters

```csharp
// Custom transport implementations
options.Transports.AddCustom<MyKinesisTransport>(config => {
    config.StreamName = "my-stream";
});
```

### v0.6.0: Dead Letter Queues

```csharp
options.Policies.When(ctx => ctx.Message is OrderCreated)
    .Then(config => config
        .SubscribeFromKafka("orders-topic", "inventory-service")
        .WithDeadLetterQueue("orders-dlq")
        .WithRetryPolicy(maxRetries: 3, backoff: exponential)
    );
```

---

## Related Documentation

- [Transport Interfaces](./TRANSPORT-INTERFACES.md) - ITransport, ISubscription, IMessageSerializer
- [Dispatcher Transport Bridge](./DISPATCHER-TRANSPORT-BRIDGE.md) - Bridge implementation
- [Policy Engine](./v0.2.0-streams-policies-observability.md) - Policy evaluation
- [Infrastructure Mapping](./INFRASTRUCTURE-MAPPING.md) - Kafka, ServiceBus, RabbitMQ mapping

---

## References

- **Kafka Consumer Groups**: https://kafka.apache.org/documentation/#consumerconfigs_group.id
- **Service Bus Subscriptions**: https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-queues-topics-subscriptions#topics-and-subscriptions
- **RabbitMQ Exchanges**: https://www.rabbitmq.com/tutorials/amqp-concepts.html#exchanges
- **Policy Engine Design**: ../plans/v0.2.0-streams-policies-observability.md
