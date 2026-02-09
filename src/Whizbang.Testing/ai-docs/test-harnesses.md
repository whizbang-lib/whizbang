# Test Harnesses

This document describes the test harnesses available in `Whizbang.Testing` for writing integration tests. These harnesses encapsulate common patterns and ensure proper async safety by using `TaskCreationOptions.RunContinuationsAsynchronously` to prevent deadlocks.

## Why Harnesses?

**Problem**: `TaskCompletionSource` without `RunContinuationsAsynchronously` can cause deadlocks when:
1. `TrySetResult()` runs continuations synchronously
2. The continuation calls `Dispose()` on the subscription
3. `Dispose()` waits for the handler to complete
4. The handler is waiting for `TrySetResult()` to return

**Solution**: All harnesses use `TaskCreationOptions.RunContinuationsAsynchronously` internally, so test authors don't need to remember this pattern.

---

## Transport Harnesses

All transport harnesses are in `Whizbang.Testing/Transport/`.

### TransportTestHarness\<TPayload\>

**Location**: `Whizbang.Testing/Transport/TransportTestHarness.cs`

**Use Case**: Full transport testing with automatic warmup pattern for push-based transports (Azure Service Bus, RabbitMQ).

```csharp
// Create harness with envelope factory and content selector
await using var harness = TransportTestHarness.Create(
  transport,
  content => new TestMessage(content),  // Payload factory
  msg => msg.Content                     // Content selector for warmup detection
);

// Setup subscription with automatic warmup
await harness.SetupSubscriptionAsync(
  new TransportDestination(topic, subscriptionName),  // Subscribe destination
  new TransportDestination(topic)                      // Publish destination
);

// Publish and wait for receipt
var envelope = await harness.PublishAndWaitAsync(
  new TransportDestination(topic),
  TimeSpan.FromSeconds(5),
  "test-content"
);

Assert.That(envelope).IsNotNull();
```

### MessageIdAwaiter

**Location**: `Whizbang.Testing/Transport/MessageAwaiter.cs`

**Use Case**: Simple single-message waiting that extracts MessageId.

```csharp
var awaiter = new MessageIdAwaiter();
var subscription = await transport.SubscribeAsync(
  awaiter.Handler,
  new TransportDestination("topic-00", "sub-00-a")
);

try {
  await transport.PublishAsync(envelope, destination);
  var messageId = await awaiter.WaitAsync(TimeSpan.FromSeconds(30));
  Assert.That(messageId).IsNotNull();
} finally {
  subscription.Dispose();
}
```

### CountingMessageAwaiter

**Location**: `Whizbang.Testing/Transport/MessageAwaiter.cs`

**Use Case**: Wait for multiple messages to be received.

```csharp
var awaiter = new CountingMessageAwaiter(expectedCount: 3);
var subscription = await transport.SubscribeAsync(
  awaiter.Handler,
  new TransportDestination("topic-00", "sub-00-a")
);

try {
  // Publish 3 messages
  for (int i = 0; i < 3; i++) {
    await transport.PublishAsync(envelope, destination);
  }
  await awaiter.WaitAsync(TimeSpan.FromSeconds(30));
  Assert.That(awaiter.ReceivedCount).IsEqualTo(3);
} finally {
  subscription.Dispose();
}
```

### SignalAwaiter

**Location**: `Whizbang.Testing/Transport/SubscriptionWarmup.cs`

**Use Case**: Simple signal for warmup detection or custom completion patterns.

```csharp
var awaiter = new SignalAwaiter();

// In handler
awaiter.Signal();

// In test
await awaiter.WaitAsync(TimeSpan.FromSeconds(5));
Assert.That(awaiter.IsSignaled).IsTrue();
```

### Key Features

- **Warmup Pattern**: TransportTestHarness sends messages until one is received
- **Discriminating Awaiters**: Distinguishes warmup messages from test messages
- **Auto-cleanup**: Disposes subscriptions on disposal
- **Thread-safe**: All awaiters use `RunContinuationsAsynchronously`

---

## 2. LifecycleStageAwaiter<TMessage>

**Location**: `Whizbang.Testing/Lifecycle/LifecycleStageAwaiter.cs`

**Use Case**: Waiting for a specific lifecycle stage to fire on a single host. Replaces manual `TaskCompletionSource` + `GenericLifecycleCompletionReceptor` pattern.

### Usage

```csharp
// Wait for PostPerspectiveInline (most common - guarantees data is persisted)
using var awaiter = LifecycleAwaiter.ForPerspectiveCompletion<ProductCreatedEvent>(
  host,
  perspectiveName: "ProductCatalogPerspective"  // Optional filter
);

await dispatcher.SendAsync(command);
var message = await awaiter.WaitAsync(15000);  // Timeout in ms

Assert.That(awaiter.InvocationCount).IsEqualTo(1);
Assert.That(awaiter.LastMessage).IsNotNull();
```

### Factory Methods

```csharp
// Generic - any lifecycle stage
using var awaiter = LifecycleAwaiter.For<TMessage>(host, LifecycleStage.PreOutboxInline);

// PostPerspectiveInline - perspective completion (most common)
using var awaiter = LifecycleAwaiter.ForPerspectiveCompletion<TEvent>(host, perspectiveName);

// PrePerspectiveInline - before perspective runs
using var awaiter = LifecycleAwaiter.ForPrePerspective<TEvent>(host, perspectiveName);

// ImmediateAsync - fires right after command handler returns
using var awaiter = LifecycleAwaiter.ForImmediateAsync<TCommand>(host);

// Distribute stages (auto-skips Inbox-sourced messages)
using var awaiter = LifecycleAwaiter.ForPreDistribute<TEvent>(host);
using var awaiter = LifecycleAwaiter.ForPostDistribute<TEvent>(host);

// Outbox/Inbox stages
using var awaiter = LifecycleAwaiter.ForPostOutbox<TEvent>(host);
using var awaiter = LifecycleAwaiter.ForPostInbox<TEvent>(host);
```

### Key Features

- **Auto-registration**: Registers receptor on construction
- **Auto-cleanup**: Unregisters receptor on disposal
- **Message capture**: Access `LastMessage` and `InvocationCount`
- **Filtering**: Optional perspective name and message filter
- **Distribute stage safety**: Automatically skips Inbox-sourced messages for Distribute stages to prevent duplicate counting (events fire both when published from Outbox and when received at Inbox)

---

## 3. MultiHostPerspectiveAwaiter<TEvent>

**Location**: `Whizbang.Testing/Lifecycle/MultiHostPerspectiveAwaiter.cs`

**Use Case**: Waiting for perspective processing to complete across multiple hosts (e.g., Inventory + BFF). Replaces `fixture.CreatePerspectiveWaiter()` pattern.

### Usage

```csharp
// Wait for perspectives on both Inventory and BFF hosts
using var awaiter = PerspectiveAwaiter.ForInventoryAndBff<ProductCreatedEvent>(
  inventoryHost, inventoryPerspectives: 2,  // e.g., InventoryLevels + ProductCatalog
  bffHost, bffPerspectives: 2               // e.g., ProductCatalog + InventoryLevels
);

await dispatcher.SendAsync(command);
await awaiter.WaitAsync(15000);

// Now safe to query perspective data from either host
var product = await bffProductLens.GetByIdAsync(productId);
Assert.That(product).IsNotNull();
```

### Factory Methods

```csharp
// Generic - any number of hosts
using var awaiter = PerspectiveAwaiter.ForHosts<TEvent>(
  (host1, expectedPerspectives1),
  (host2, expectedPerspectives2),
  (host3, expectedPerspectives3)
);

// Two-host convenience (Inventory + BFF pattern)
using var awaiter = PerspectiveAwaiter.ForInventoryAndBff<TEvent>(
  inventoryHost, inventoryPerspectives,
  bffHost, bffPerspectives
);
```

### Key Features

- **Multi-host coordination**: Waits for all hosts to complete
- **Perspective deduplication**: Counts unique perspectives, not invocations
- **Timeout diagnostics**: Shows per-host progress on timeout
- **Auto-cleanup**: Unregisters all receptors on disposal

---

## Migration Guide

### Before (Manual TCS Pattern)

```csharp
var completionSource = new TaskCompletionSource<bool>();  // BUG: Missing flag!
var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
  completionSource,
  perspectiveName: "ProductCatalogPerspective"
);

var registry = host.Services.GetRequiredService<ILifecycleReceptorRegistry>();
registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);

try {
  await dispatcher.SendAsync(command);
  await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(15));
  Assert.That(receptor.InvocationCount).IsEqualTo(1);
} finally {
  registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);
}
```

### After (Harness Pattern)

```csharp
using var awaiter = LifecycleAwaiter.ForPerspectiveCompletion<ProductCreatedEvent>(
  host, "ProductCatalogPerspective"
);

await dispatcher.SendAsync(command);
await awaiter.WaitAsync(15000);

Assert.That(awaiter.InvocationCount).IsEqualTo(1);
```

---

## Lifecycle Stages Reference

| Stage | Timing | Blocking | Common Use |
|-------|--------|----------|------------|
| `ImmediateAsync` | After command handler returns | No | Verify command was handled |
| `PreDistributeInline` | Before ProcessWorkBatchAsync | Yes | Pre-distribution hooks |
| `PreDistributeAsync` | Before ProcessWorkBatchAsync | No | Async pre-distribution |
| `DistributeAsync` | Parallel with ProcessWorkBatchAsync | No | Parallel processing |
| `PostDistributeAsync` | After ProcessWorkBatchAsync | No | Async post-distribution |
| `PostDistributeInline` | After ProcessWorkBatchAsync | Yes | Verify distribution complete |
| `PreOutboxInline` | Before transport publish | Yes | Pre-publish hooks |
| `PreOutboxAsync` | Parallel with transport publish | No | Async pre-publish |
| `PostOutboxAsync` | After transport publish | No | Async post-publish |
| `PostOutboxInline` | After transport publish | Yes | Verify message published |
| `PreInboxInline` | Before receptor invocation | Yes | Pre-receive hooks |
| `PreInboxAsync` | Parallel with receptor invocation | No | Async pre-receive |
| `PostInboxAsync` | After receptor completes | No | Async post-receive |
| `PostInboxInline` | After receptor completes | Yes | Verify message received |
| `PrePerspectiveInline` | Before perspective RunAsync | Yes | Pre-perspective hooks |
| `PrePerspectiveAsync` | Parallel with perspective RunAsync | No | Async pre-perspective |
| `PostPerspectiveAsync` | After perspective completes | No | Async post-perspective |
| `PostPerspectiveInline` | After perspective completes | Yes | **Test synchronization** |

**Most Important for Tests**: `PostPerspectiveInline` - guarantees perspective data is persisted before the receptor completes.

---

## Best Practices

1. **Always use harnesses** instead of raw `TaskCompletionSource`
2. **Use `using` statements** to ensure cleanup on test failure
3. **Create awaiter BEFORE dispatching** to avoid race conditions
4. **Use appropriate timeouts** - longer for integration tests with infrastructure
5. **Filter by perspective name** when testing specific perspectives
