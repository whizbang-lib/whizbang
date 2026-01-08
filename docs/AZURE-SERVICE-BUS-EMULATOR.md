# Azure Service Bus Emulator Compatibility

## Overview

The Azure Service Bus Emulator has multiple limitations that require workarounds for local development:

1. **No ServiceBusProcessor support** - Background listeners don't receive messages
2. **Limited topic filter support** - Complex subscription rules and filters don't work reliably

This document explains the **two-part compatibility strategy**:
1. **Polling Mode** - Use explicit `ReceiveMessageAsync()` instead of ServiceBusProcessor
2. **Generic Topic Routing** - Route all events to generic topics instead of per-aggregate topics

## The Problem

### Production Azure Service Bus (Works ✅)
- Supports `ServiceBusProcessor` with background listeners (push-based)
- Supports complex subscription rules and topic filters
- Per-aggregate topics (e.g., "products-topic", "inventory-topic")
- Optimal performance and resource usage

### Azure Service Bus Emulator (Limited ⚠️)
- Does **NOT** support `ServiceBusProcessor` background listeners
- Processors start successfully but **never receive messages**
- Complex subscription rules/filters don't work reliably
- Only supports explicit `ReceiveMessageAsync()` polling (pull-based)
- Requires simplified topic/subscription model

## The Solution: Polling Mode

Whizbang's `ServiceBusConsumerWorker` supports two modes:

### 1. Processor Mode (Default - Production)

```csharp
var options = new ServiceBusConsumerOptions {
  Mode = SubscriptionMode.Processor,  // Default
  Subscriptions = {
    new TopicSubscription("products-topic", "my-subscription")
  }
};
```

**When to use**: Production Azure Service Bus, Aspire environments with real Azure resources

**How it works**: Creates `ServiceBusProcessor` instances that run as background listeners

### 2. Polling Mode (Emulator Compatible)

```csharp
var options = new ServiceBusConsumerOptions {
  Mode = SubscriptionMode.Polling,  // Required for emulator
  PollingInterval = TimeSpan.FromMilliseconds(500),  // Optional, default 500ms
  Subscriptions = {
    new TopicSubscription("products-topic", "my-subscription")
  }
};
```

**When to use**: Azure Service Bus Emulator, local development, integration testing

**How it works**: Actively polls each subscription using `ReceiveMessageAsync()` in a continuous loop

## The Solution: Generic Topic Routing

The emulator's limited support for topic filters and subscription rules requires a simplified routing strategy.

### 1. Per-Aggregate Topics (Default - Production)

```csharp
// Production: Each aggregate type publishes to its own topic
services.AddSingleton<ITopicRoutingStrategy>(
  new DefaultTopicRoutingStrategy()  // products → "products-topic", orders → "orders-topic"
);
```

**When to use**: Production Azure Service Bus with full topic filter support

**How it works**: Each aggregate type has its own topic, subscribers use topic filters to select events

### 2. Generic Topic Routing (Emulator Compatible)

```csharp
// Emulator: All events route to generic topics using hash-based distribution
services.AddSingleton<ITopicRoutingStrategy>(
  new GenericTopicRoutingStrategy(topicCount: 2)  // topic-00, topic-01
);
```

**When to use**: Azure Service Bus Emulator, local development, integration testing

**How it works**: Events are distributed across generic topics (topic-00, topic-01, etc.) using message ID hash. Each service subscribes to all generic topics with simple subscriptions (no filters needed).

**Benefits**:
- No complex subscription rules or filters required
- Predictable, deterministic routing
- Works reliably with emulator

## Configuration Examples

### Example 1: Complete Emulator Configuration (Both Strategies)

```csharp
// Detect emulator from connection string
var isEmulator = connectionString.Contains("localhost") ||
                 connectionString.Contains("127.0.0.1");

// STRATEGY 1: Generic Topic Routing
services.AddSingleton<ITopicRoutingStrategy>(
  isEmulator
    ? new GenericTopicRoutingStrategy(topicCount: 2)  // topic-00, topic-01 for emulator
    : new DefaultTopicRoutingStrategy()                // Named topics for production
);

// STRATEGY 2: Polling Mode
var consumerOptions = new ServiceBusConsumerOptions {
  Mode = isEmulator ? SubscriptionMode.Polling : SubscriptionMode.Processor,
  PollingInterval = TimeSpan.FromMilliseconds(100),  // Fast polling for tests
};

// Subscribe to generic topics (emulator) or named topics (production)
if (isEmulator) {
  consumerOptions.Subscriptions.Add(new TopicSubscription("topic-00", "sub-00-a"));
  consumerOptions.Subscriptions.Add(new TopicSubscription("topic-01", "sub-01-a"));
} else {
  consumerOptions.Subscriptions.Add(new TopicSubscription("products-topic", "my-service-products"));
  consumerOptions.Subscriptions.Add(new TopicSubscription("inventory-topic", "my-service-inventory"));
}

services.AddSingleton(consumerOptions);
services.AddHostedService<ServiceBusConsumerWorker>();
```

### Example 2: Aspire Integration Tests (Real Implementation)

```csharp
// In AspireIntegrationFixture - shows actual working configuration

// STRATEGY 1: Generic Topic Routing (for publishers)
builder.Services.AddSingleton<ITopicRoutingStrategy>(
  new GenericTopicRoutingStrategy(topicCount: 2)  // Hash-based distribution to topic-00, topic-01
);

// STRATEGY 2: Polling Mode (for consumers)
var consumerOptions = new ServiceBusConsumerOptions {
  Mode = SubscriptionMode.Polling,  // Required for Azure Service Bus Emulator
  PollingInterval = TimeSpan.FromMilliseconds(100)  // Fast polling for tests (100ms)
};

// Subscribe to ALL generic topics since we can't filter by event type
consumerOptions.Subscriptions.Add(new TopicSubscription("topic-00", "sub-00-a"));
consumerOptions.Subscriptions.Add(new TopicSubscription("topic-01", "sub-01-a"));

builder.Services.AddSingleton(consumerOptions);
builder.Services.AddHostedService<ServiceBusConsumerWorker>();
```

**Key Points**:
- Publishers use `GenericTopicRoutingStrategy` to route all events to generic topics
- Consumers subscribe to ALL generic topics (no filtering needed)
- Each service gets its own subscription name (e.g., "sub-00-a" for BFF, "sub-00-b" for InventoryWorker)
- Fast polling interval (100ms) for responsive integration tests

### Example 3: Environment-Based Configuration

```csharp
var mode = builder.Configuration.GetValue<string>("ServiceBus:Mode") == "Polling"
  ? SubscriptionMode.Polling
  : SubscriptionMode.Processor;

var options = new ServiceBusConsumerOptions {
  Mode = mode,
  PollingInterval = TimeSpan.FromMilliseconds(
    builder.Configuration.GetValue<int>("ServiceBus:PollingIntervalMs", 500)
  ),
  Subscriptions = {
    new TopicSubscription("products-topic", "my-subscription")
  }
};
```

**appsettings.Development.json**:
```json
{
  "ServiceBus": {
    "Mode": "Polling",
    "PollingIntervalMs": 100
  }
}
```

**appsettings.Production.json**:
```json
{
  "ServiceBus": {
    "Mode": "Processor",
    "PollingIntervalMs": 500
  }
}
```

## How the Two Strategies Work Together

### The Complete Emulator Compatibility Pattern

Both strategies must be used together for full emulator compatibility:

```
┌─────────────┐                     ┌─────────────┐
│  Publisher  │                     │  Consumer   │
│  (Service)  │                     │  (Service)  │
└──────┬──────┘                     └──────▲──────┘
       │                                   │
       │ 1. GenericTopicRoutingStrategy    │ 3. ServiceBusConsumerWorker
       │    routes event to topic-00       │    polls topic-00/sub-00-a
       │    based on MessageId hash        │    using ReceiveMessageAsync()
       ▼                                   │
┌────────────────────────────────────────────────┐
│  Azure Service Bus Emulator                    │
│                                                │
│  topic-00/sub-00-a  ◄─── 2. Message stored    │
│  topic-01/sub-01-a                             │
└────────────────────────────────────────────────┘
```

### Why Both Are Needed

1. **Generic Topic Routing** solves the "publish" problem:
   - Emulator doesn't support complex subscription filters
   - Publishers need predictable, simple topic names (topic-00, topic-01)
   - Hash-based distribution ensures even load distribution

2. **Polling Mode** solves the "consume" problem:
   - Emulator doesn't support ServiceBusProcessor background listeners
   - Consumers need to actively poll using `ReceiveMessageAsync()`
   - Polling loop processes messages from all subscribed topics

### What About Production?

In production with real Azure Service Bus:
- Use **DefaultTopicRoutingStrategy** for semantic topic names (products-topic, inventory-topic)
- Use **SubscriptionMode.Processor** for efficient push-based message delivery
- Use topic filters and subscription rules for fine-grained event routing

## Performance Considerations

### Emulator Mode (Polling + Generic Topics)
- **Pros**:
  - Works with emulator
  - Simple, predictable behavior
  - No complex subscription rules needed
  - Hash-based load distribution
- **Cons**:
  - Higher CPU usage (continuous polling)
  - Slightly higher latency
  - All services receive all events (app-level filtering)
  - More network traffic
- **Best for**: Local development, integration tests

### Production Mode (Processor + Named Topics)
- **Pros**:
  - Push-based (efficient), lower latency, lower CPU usage
  - Semantic topic names (products-topic, inventory-topic)
  - Topic filters for efficient routing
  - Lower network traffic (only relevant events delivered)
- **Cons**:
  - Doesn't work with emulator
  - More complex configuration
- **Best for**: Production, Aspire with real Azure Service Bus

## Polling Interval Guidelines

| Scenario | Recommended Interval | Rationale |
|----------|---------------------|-----------|
| **Integration Tests** | 50-100ms | Fast feedback, test timeout constraints |
| **Local Development** | 200-500ms | Balance responsiveness and CPU usage |
| **Production** | N/A (use Processor mode) | Don't use polling in production |

## Troubleshooting

### Messages Not Being Received in Emulator

**Symptom**: ServiceBusProcessor starts successfully but never receives messages

**Solution**: Switch to Polling Mode:
```csharp
Mode = SubscriptionMode.Polling
```

### High CPU Usage in Polling Mode

**Symptom**: Worker consuming significant CPU even when idle

**Solution**: Increase polling interval:
```csharp
PollingInterval = TimeSpan.FromMilliseconds(500)  // or higher
```

### Tests Timing Out

**Symptom**: Integration tests fail with timeout errors

**Solution**: Decrease polling interval for faster message processing:
```csharp
PollingInterval = TimeSpan.FromMilliseconds(50)
```

### Events Published But Not Consumed

**Symptom**: Publisher sends events successfully but consumer never receives them

**Root Cause**: Missing one or both emulator compatibility strategies

**Solution**: Ensure BOTH strategies are configured:
```csharp
// 1. Publisher must use GenericTopicRoutingStrategy
services.AddSingleton<ITopicRoutingStrategy>(
  new GenericTopicRoutingStrategy(topicCount: 2)
);

// 2. Consumer must use Polling mode
var consumerOptions = new ServiceBusConsumerOptions {
  Mode = SubscriptionMode.Polling,
  PollingInterval = TimeSpan.FromMilliseconds(100)
};
```

### Wrong Topics or Subscriptions

**Symptom**: Messages going to wrong topics or subscriptions not found

**Root Cause**: Mismatch between publisher topics and consumer subscriptions

**Solution**: Ensure topic names align:
```csharp
// Publisher: GenericTopicRoutingStrategy creates topic-00, topic-01
new GenericTopicRoutingStrategy(topicCount: 2)

// Consumer: Subscribe to same topic names
consumerOptions.Subscriptions.Add(new TopicSubscription("topic-00", "sub-00-a"));
consumerOptions.Subscriptions.Add(new TopicSubscription("topic-01", "sub-01-a"));
```

### Multiple Services with Same Subscription Name

**Symptom**: Only one service instance receives messages

**Root Cause**: Subscription names must be unique per service

**Solution**: Use unique subscription names per service:
```csharp
// BFF Service
new TopicSubscription("topic-00", "sub-00-a")  // suffix 'a' for BFF

// Inventory Service
new TopicSubscription("topic-00", "sub-00-b")  // suffix 'b' for Inventory
```

## Related Documentation

- [ServiceBusConsumerWorker API Reference](../src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs) - Consumer implementation
- [SubscriptionMode Enum](../src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs#L576) - Processor vs Polling modes
- [GenericTopicRoutingStrategy](../src/Whizbang.Core/Routing/GenericTopicRoutingStrategy.cs) - Publisher routing strategy
- [ITopicRoutingStrategy Interface](../src/Whizbang.Core/Routing/ITopicRoutingStrategy.cs) - Routing abstraction
- [Azure Service Bus Emulator Limitations](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator) - Official Microsoft docs

## Testing

To verify polling mode works correctly:

```bash
# Run polling mode unit tests
dotnet test --filter "FullyQualifiedName~ServiceBusConsumerWorkerPollingTests"

# Run Aspire integration tests with emulator
dotnet test --filter "FullyQualifiedName~AspireIntegration"
```

## Migration Guide

### Upgrading Existing Tests to Full Emulator Compatibility

If you have existing integration tests using the emulator, you need to add BOTH strategies:

**Before** (Won't work with emulator):
```csharp
// Publisher configuration (defaults to named topics)
// No explicit ITopicRoutingStrategy registered

// Consumer configuration (defaults to Processor mode)
var options = new ServiceBusConsumerOptions {
  Subscriptions = {
    new TopicSubscription("products-topic", "my-subscription")
  }
};
```

**After** (Full emulator compatibility):
```csharp
// STRATEGY 1: Add Generic Topic Routing for publishers
services.AddSingleton<ITopicRoutingStrategy>(
  new GenericTopicRoutingStrategy(topicCount: 2)  // Routes to topic-00, topic-01
);

// STRATEGY 2: Add Polling Mode for consumers
var consumerOptions = new ServiceBusConsumerOptions {
  Mode = SubscriptionMode.Polling,  // Required for emulator
  PollingInterval = TimeSpan.FromMilliseconds(100),  // Fast polling for tests
};

// Subscribe to ALL generic topics (emulator can't filter by event type)
consumerOptions.Subscriptions.Add(new TopicSubscription("topic-00", "sub-00-a"));
consumerOptions.Subscriptions.Add(new TopicSubscription("topic-01", "sub-01-a"));

services.AddSingleton(consumerOptions);
services.AddHostedService<ServiceBusConsumerWorker>();
```

**Key Changes**:
1. Register `GenericTopicRoutingStrategy` for publishers
2. Change consumer subscriptions from named topics to generic topics (topic-00, topic-01)
3. Enable polling mode with appropriate interval
4. Subscribe to ALL generic topics (no filtering needed)
