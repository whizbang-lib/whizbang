# Whizbang.Testing AI Documentation

This directory contains focused documentation for AI assistants working with the Whizbang.Testing library.

## Available Documents

| Document | Description | When to Read |
|----------|-------------|--------------|
| [test-harnesses.md](test-harnesses.md) | Test harnesses for integration testing | Writing or debugging integration tests |

## Quick Reference

### Transport Harnesses

**TransportTestHarness** - Full transport tests with warmup:
```csharp
await using var harness = TransportTestHarness.Create(transport, payloadFactory, contentSelector);
await harness.SetupSubscriptionAsync(subscribeDestination, publishDestination);
var envelope = await harness.PublishAndWaitAsync(destination, timeout);
```

**MessageIdAwaiter** - Simple single-message waiting:
```csharp
var awaiter = new MessageIdAwaiter();
var subscription = await transport.SubscribeAsync(awaiter.Handler, destination);
await transport.PublishAsync(envelope, destination);
var messageId = await awaiter.WaitAsync(TimeSpan.FromSeconds(30));
```

**CountingMessageAwaiter** - Wait for multiple messages:
```csharp
var awaiter = new CountingMessageAwaiter(expectedCount: 3);
var subscription = await transport.SubscribeAsync(awaiter.Handler, destination);
// Publish 3 messages
await awaiter.WaitAsync(TimeSpan.FromSeconds(30));
```

### Lifecycle Harnesses

**LifecycleStageAwaiter** - Single-host lifecycle waiting:
```csharp
using var awaiter = LifecycleAwaiter.ForPerspectiveCompletion<TEvent>(host, perspectiveName);
await dispatcher.SendAsync(command);
await awaiter.WaitAsync(15000);
```

**MultiHostPerspectiveAwaiter** - Multi-host perspective waiting:
```csharp
using var awaiter = PerspectiveAwaiter.ForInventoryAndBff<TEvent>(
  inventoryHost, 2, bffHost, 2);
await dispatcher.SendAsync(command);
await awaiter.WaitAsync(15000);
```

## Key Principle

**Always use harnesses instead of raw `TaskCompletionSource`** - The harnesses internally use `TaskCreationOptions.RunContinuationsAsynchronously` to prevent deadlocks.

## Available Harnesses Summary

| Harness | Use Case | Location |
|---------|----------|----------|
| `TransportTestHarness<T>` | Transport tests with warmup | Transport/TransportTestHarness.cs |
| `MessageIdAwaiter` | Simple single-message waiting | Transport/MessageAwaiter.cs |
| `CountingMessageAwaiter` | Wait for N messages | Transport/MessageAwaiter.cs |
| `SignalAwaiter` | Simple completion signal | Transport/SubscriptionWarmup.cs |
| `LifecycleStageAwaiter<T>` | Single-host lifecycle stage | Lifecycle/LifecycleStageAwaiter.cs |
| `MultiHostPerspectiveAwaiter<T>` | Multi-host perspectives | Lifecycle/MultiHostPerspectiveAwaiter.cs |
