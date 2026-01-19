# Infrastructure Mapping Guide

This guide explains how Whizbang's abstract concepts map to concrete infrastructure providers.

## Overview

Whizbang provides a **provider-agnostic abstraction layer** for event-driven architectures. This allows you to write your domain logic once and deploy to different messaging infrastructures without code changes.

## Core Concepts Hierarchy

Whizbang uses a three-layer hierarchy:

```plaintext
TOPIC (Policy-Driven Routing)
  ↓ Policies determine which topic based on message context
STREAM (Primary Abstraction - Ordering + Execution)
  ↓ Stream key determines which stream (e.g., aggregate ID)
PARTITION (Implementation Detail - Physical Parallelism)
  ↓ Internal sharding for scale (e.g., stream has 3 partitions)
```

### Topic
- **Purpose**: Logical routing destination determined by policies
- **Analogy**: Like HTTP route paths or message types
- **Example**: `orders`, `inventory`, `notifications`

### Stream
- **Purpose**: Ordering boundary - messages in same stream are processed in order
- **Analogy**: Like a queue for a specific entity or workflow
- **Example**: `order-12345` (all messages for order 12345)

### Partition
- **Purpose**: Physical parallelism - multiple consumers can process different partitions concurrently
- **Analogy**: Like database shards
- **Example**: Stream `order-12345` might be in partition 2 of 16

---

## Infrastructure Mapping Table

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

## Provider-Specific Details

### Kafka / Azure Event Hubs

**Best match for Whizbang's model** - concepts align naturally.

#### Mapping

- **Topic** → Kafka Topic
- **Stream** → Partition Key (same key always goes to same partition)
- **Partition** → Partition (0..N-1)
- **Sequence** → Offset (per partition)
- **Ordering** → Guaranteed per partition, not cross-partition

#### Example Configuration

```csharp
// Policy determines topic and stream key
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config
            .UseTopic("orders")                           // → Kafka topic "orders"
            .UseStream(ctx => $"order-{ctx.GetAggregateId()}")  // → Partition key
            .WithPartitions(16)                           // → 16 partitions (0-15)
            .UsePartitionRouter<HashPartitionRouter>()    // → Hash key to partition
        );
```

#### Physical Layout

```plaintext
Kafka Topic: "orders"
├── Partition 0: [order-101, order-205, ...]  (Hash("order-101") % 16 == 0)
├── Partition 1: [order-333, order-789, ...]  (Hash("order-333") % 16 == 1)
...
└── Partition 15: [order-456, order-999, ...] (Hash("order-456") % 16 == 15)
```

#### Notes

- Kafka provides **exactly the same ordering guarantees** as Whizbang
- Native partition assignment works out-of-the-box
- Offsets map directly to sequence numbers
- Headers support full observability metadata

---

### RabbitMQ

**Requires routing setup** - uses exchanges and queues.

#### Mapping

- **Topic** → Exchange (topic or direct exchange)
- **Stream** → Routing key (determines which queue)
- **Partition** → Multiple queues (one per "partition")
- **Sequence** → Delivery tag (per queue)
- **Ordering** → Guaranteed per queue, not cross-queue

#### Example Configuration

```csharp
// Policy determines exchange and routing key
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config
            .UseTopic("orders.exchange")                  // → Exchange name
            .UseStream(ctx => $"order-{ctx.GetAggregateId()}")  // → Routing key
            .WithPartitions(4)                            // → 4 queues
            .UsePartitionRouter<HashPartitionRouter>()    // → Hash routing key to queue
        );
```

#### Physical Layout

```plaintext
Exchange: "orders.exchange" (type: topic)
├── Queue: "orders.0" (binding: partition-0.#)
├── Queue: "orders.1" (binding: partition-1.#)
├── Queue: "orders.2" (binding: partition-2.#)
└── Queue: "orders.3" (binding: partition-3.#)

Whizbang sends to exchange with routing key: "partition-2.order-12345"
→ Routed to queue "orders.2"
```

#### Notes

- Requires pre-created queues and bindings
- Ordering guaranteed within each queue
- Message properties carry observability metadata
- Delivery tags are sequential per queue

---

### Azure Service Bus

**Session-based model** - aligns well with streams.

#### Mapping

- **Topic** → Service Bus Topic
- **Stream** → SessionId (ensures ordering)
- **Partition** → Multiple subscriptions (for parallelism)
- **Sequence** → SequenceNumber (per message)
- **Ordering** → Guaranteed per session

#### Example Configuration

```csharp
// Policy determines topic and session ID
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config
            .UseTopic("orders")                           // → Service Bus topic
            .UseStream(ctx => $"order-{ctx.GetAggregateId()}")  // → SessionId
            .WithPartitions(8)                            // → 8 subscriptions
            .UsePartitionRouter<HashPartitionRouter>()    // → Hash SessionId to subscription
        );
```

#### Physical Layout

```plaintext
Service Bus Topic: "orders"
├── Subscription: "partition-0" (processes sessions 0, 8, 16, ...)
├── Subscription: "partition-1" (processes sessions 1, 9, 17, ...)
...
└── Subscription: "partition-7" (processes sessions 7, 15, 23, ...)

Message with SessionId "order-12345" → Hash % 8 → Subscription partition-3
```

#### Notes

- **Session-enabled topics required** for ordering guarantees
- SessionId is mandatory for stream-based processing
- User properties carry observability metadata
- SequenceNumber is globally unique (not per partition)

---

### Event Store (EventStoreDB)

**Stream-native** - perfect alignment with event sourcing.

#### Mapping

- **Topic** → Category ($category-{name})
- **Stream** → Stream ID (the fundamental concept)
- **Partition** → Subscription groups (persistent subscriptions)
- **Sequence** → EventNumber (per stream, starts at 0)
- **Ordering** → Guaranteed per stream

#### Example Configuration

```csharp
// Policy determines category and stream
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config
            .UseTopic("order")                            // → Category: $category-order
            .UseStream(ctx => $"order-{ctx.GetAggregateId()}")  // → Stream: order-12345
            .WithPartitions(4)                            // → 4 persistent subscription groups
            .UsePartitionRouter<HashPartitionRouter>()    // → Hash stream ID to group
        );
```

#### Physical Layout

```plaintext
Category: $category-order
├── Stream: "order-12345" → EventNumber 0, 1, 2, ...
├── Stream: "order-67890" → EventNumber 0, 1, 2, ...
└── Stream: "order-99999" → EventNumber 0, 1, 2, ...

Persistent Subscriptions (consume by category):
├── Group: "partition-0" (processes subset of streams)
├── Group: "partition-1" (processes subset of streams)
├── Group: "partition-2" (processes subset of streams)
└── Group: "partition-3" (processes subset of streams)
```

#### Notes

- Stream ID is the primary concept (matches Whizbang perfectly)
- EventNumber is **per stream** (resets for each stream)
- Categories enable topic-like subscriptions
- Event metadata carries observability data
- Persistent subscriptions provide parallelism

---

## Choosing an Infrastructure

### Use Kafka/Event Hubs When:
- ✅ High throughput required (millions of messages/sec)
- ✅ Horizontal scaling is critical
- ✅ You need replay capability
- ✅ Messages are relatively small
- ✅ Ordering per partition is sufficient

### Use RabbitMQ When:
- ✅ Flexible routing patterns needed
- ✅ Lower message volumes
- ✅ You need multiple consumers with different patterns
- ✅ Request/reply patterns common
- ✅ Dead-letter queues required

### Use Service Bus When:
- ✅ Azure-native applications
- ✅ Session-based ordering important
- ✅ You need scheduled messages
- ✅ Enterprise integration patterns required
- ✅ Strong delivery guarantees needed

### Use Event Store When:
- ✅ Event sourcing is your architecture
- ✅ You need temporal queries
- ✅ Complete audit trail required
- ✅ Stream-centric model fits naturally
- ✅ Projections and subscriptions needed

---

## Observability Mapping

All providers can carry Whizbang observability metadata:

| Whizbang Field | Kafka | RabbitMQ | Service Bus | Event Store |
|----------------|-------|----------|-------------|-------------|
| MessageId | Header | Property | UserProperty | Metadata |
| CorrelationId | Header | CorrelationId | CorrelationId | Metadata |
| CausationId | Header | Property | UserProperty | Metadata |
| Hops | Header (JSON) | Property (JSON) | UserProperty (JSON) | Metadata (JSON) |
| SecurityContext | Header | Property | UserProperty | Metadata |
| PolicyTrail | Header (JSON) | Property (JSON) | UserProperty (JSON) | Metadata (JSON) |
| CallerInfo | Header | Property | UserProperty | Metadata |

**Note**: All providers support the full Whizbang observability model - it's just serialized differently.

---

## Migration Between Providers

Because Whizbang abstracts infrastructure, **you can migrate between providers** by:

1. **Keep your domain logic unchanged** - it only knows about topics and streams
2. **Change the provider configuration** - switch Kafka to Service Bus, etc.
3. **Update deployment** - point to new infrastructure

```csharp
// Your code (provider-agnostic):
policies.When(ctx => ctx.MatchesAggregate<Order>())
        .Then(config => config
            .UseTopic("orders")
            .UseStream(ctx => $"order-{ctx.GetAggregateId()}")
            .WithPartitions(16)
        );

// Provider selection (deployment-time config):
services.AddWhizbang()
        .UseKafka(options => { /* kafka config */ });
        // OR
        .UseServiceBus(options => { /* service bus config */ });
        // OR
        .UseEventStore(options => { /* event store config */ });
```

---

## Summary

- **Whizbang abstracts infrastructure** - write once, deploy anywhere
- **Topic** → Logical routing (policy-driven)
- **Stream** → Ordering boundary (entity-centric)
- **Partition** → Physical parallelism (performance optimization)
- **All providers support full observability** - just different serialization
- **Choose provider based on operational requirements** - not code constraints

This abstraction enables **polyglot messaging** - use the right tool for each workload without rewriting your domain logic.
