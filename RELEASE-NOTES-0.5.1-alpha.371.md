# Release Notes - v0.5.1-alpha

## Highlights

This release focuses on **perspective sync improvements**, **distributed tracing enhancements**, and **significant test coverage increases**. Key highlights include symmetric sync APIs, observability callbacks, and critical bug fixes for command dispatching.

---

## Breaking Changes

None.

---

## New Features

### Symmetric Sync Methods with Observability Callbacks

Added symmetric waiting strategies to both `Dispatcher` and `EventStore` with new observability callbacks:

**Dispatcher:**
```csharp
// Wait for specific perspective after invoking command with result
var result = await dispatcher.LocalInvokeAndSyncAsync<CreateOrder, OrderCreated, OrderProjection>(
    command,
    onWaiting: ctx => logger.LogInformation("Waiting for {Perspective}...", ctx.PerspectiveType),
    onDecisionMade: ctx => logger.LogInformation("Sync completed: {Outcome}", ctx.Outcome));

// Wait for specific perspective after invoking void command
await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<ProcessPayment, PaymentProjection>(command);
```

**EventStore:**
```csharp
// Wait for ALL perspectives after appending (new symmetric method)
await eventStore.AppendAndWaitAsync<OrderCreated>(streamId, events);

// Existing: Wait for specific perspective
await eventStore.AppendAndWaitForPerspectiveAsync<OrderCreated, OrderProjection>(streamId, events);
```

**Callback Contexts:**
- `SyncWaitingContext` - Called ONLY when actual waiting occurs (perspective type, event count, stream IDs, timeout)
- `SyncDecisionContext` - ALWAYS called when sync decision is made (outcome, elapsed time, whether waiting occurred)

### Event Completion Awaiter

New `IEventCompletionAwaiter` interface for RPC-style perspective sync waiting:

```csharp
public interface IEventCompletionAwaiter {
    Task<SyncResult> WaitForCompletionAsync<TPerspective>(
        Guid streamId,
        TimeSpan timeout,
        CancellationToken ct = default);
}
```

### Per-Event Perspective Tracing

Added granular tracing spans for individual event processing within perspectives, enabling better observability of event handling performance.

---

## Bug Fixes

### Critical: Perspective Sync Timeout for Commands

**Fixed:** `LocalInvokeAsync` with commands (`ICommand`) that have `[AwaitPerspectiveSync]` on their receptors no longer timeout.

**Root Cause:** Perspectives only process `IEvent` types, not commands. The sync mechanism was waiting for perspective updates that would never happen.

**Solution:**
- Added early return in `Dispatcher._awaitPerspectiveSyncIfNeededAsync` when message is not `IEvent`
- Source generator now checks if message implements `IEvent` before generating sync code
- Commands and plain `IMessage` types skip perspective sync entirely

```csharp
// This now works correctly - no timeout!
[AwaitPerspectiveSync(typeof(OrderProjection), TimeoutMs = 5000)]
public class CreateOrderReceptor : IReceptor<CreateOrderCommand> {
    public ValueTask HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => ...;
}
```

### EF Core JSONB Change Detection

**Fixed:** EF Core was not detecting changes to polymorphic JSONB models, causing updates to be silently ignored.

### Azure Service Bus Subscription Naming

**Fixed:** Subscription names now derive from metadata instead of routing key, fixing issues with message routing in Azure Service Bus.

### Azure Service Bus SQL Filter Expressions

**Fixed:** Use `sys.Label` instead of `[Subject]` in SqlFilter expressions for proper message filtering.

### Distributed Tracing Fixes

- Receptor spans now extract parent context from envelope hops correctly
- Lifecycle and Perspective spans respect `TraceComponents` flags
- Perspective spans link to original request trace
- Distributed trace context added to `TransportConsumerWorker`
- Parent context cascades correctly when batch span is disabled

### Polymorphic JsonTypeInfo Discovery

**Fixed:** Source generator now auto-discovers polymorphic `JsonTypeInfo` for property types, eliminating manual registration requirements.

### DbContext Concurrency in HotChocolate

**Fixed:** DbContext concurrency errors in HotChocolate parallel resolvers by ensuring proper scope isolation.

---

## Performance Improvements

### CI/CD Optimizations

- Implemented dual-build approach for coverage optimization (parallel instrumented + regular builds)
- Added `-NoBuild` flag to `Run-Tests.ps1` for faster CI execution when using pre-built artifacts
- Fixed coverage calculation to exclude samples/benchmarks/tools directories

### Regex Security

Added timeout to `Regex.IsMatch` calls to prevent potential ReDoS (Regular Expression Denial of Service) attacks.

---

## Testing Improvements

- **50+ new tests** added for improved code coverage
- Fixed multiple race condition tests that were flaky in CI
- Added comprehensive tests for:
  - `SyncEventTracker` and `PerspectiveSyncAwaiter`
  - `DispatchOptions` and routing
  - `ScopedLensFactory` and `DispatcherEventCascader`
  - `ReceptorInvoker` edge cases
  - `LenientDateTimeOffsetConverter`
  - Subscription resilience (OnDisconnected, retry logging)
  - Generator records (`GuidInterceptionInfo`, `StreamIdInfo`)
- Replaced flaky `Task.Delay` patterns with reliable `TaskCompletionSource` signals
- Added verbose logging mode for Unit and Integration test modes

---

## Documentation

### New Documentation Pages

**Core Concepts:**
- `core-concepts/event-store` - Event Store API with append-and-wait patterns
- `core-concepts/messages` - Messages and Events concepts
- `core-concepts/stream-id` - Stream ID and event streams
- `core-concepts/event-streams` - Event stream management
- `core-concepts/assembly-registry` - Assembly registry for type discovery
- `core-concepts/scoped-lenses` - Scoped lens queries
- `core-concepts/security-context-propagation` - Security context flow
- `core-concepts/time-provider` - Time provider integration
- `core-concepts/envelope-registry` - Envelope registry
- `core-concepts/envelope-serialization` - Envelope serialization
- `core-concepts/lifecycle-stages` - Lifecycle stages
- `core-concepts/delivery-receipts` - Delivery receipt tracking
- `core-concepts/message-associations` - Message associations
- `core-concepts/persistence` - Persistence patterns
- `core-concepts/perspectives/event-completion` - Event completion awaiting (new!)

**Diagnostics:**
- `diagnostics/WHIZ062` - Type validation analyzer
- `diagnostics/WHIZ080` - Response type analyzer
- `diagnostics/WHIZ802` - Vector dimensions analyzer
- `diagnostics/WHIZ807` - Field validation analyzer

**Lenses:**
- `lenses/lens-query-factory` - Lens query factory patterns
- `lenses/scoped-queries` - Scoped query patterns
- `lenses/temporal-query` - Temporal query patterns

**Components:**
- `components/caching` - Caching component
- `components/data/postgres` - PostgreSQL data access
- `components/transports` - Transport abstractions
- `components/workers/transport-consumer` - Transport consumer workers

**Integration:**
- `di/service-registration` - DI registration patterns
- `di/all-services`, `di/lens-services`, `di/perspective-services` - Service registration helpers
- `observability/tracing` - Distributed tracing
- `rest/setup`, `rest/filtering`, `rest/mutations` - REST API integration
- `signalr/notification-hooks` - SignalR notification hooks
- `graphql/lens-integration` - GraphQL lens integration
- `mutations/hooks` - Mutation lifecycle hooks

### Updated Documentation

- **Perspective Sync** - Added observability callbacks section, Events Only callout
- **Event Completion** - New event-based vs perspective-based waiting comparison
- **Azure Service Bus** - Added subscription naming, auto-provisioning, and routing filter sections
- **Tracing** - Added per-event spans, parent context extraction, and perspective sync spans
- **Routing** - Updated with domain topic provisioning
- **Security** - Enhanced message security context documentation
- **System Events** - Updated with transport filtering

### Code-Docs Linking (`<docs>` tags)

Added bidirectional linking between source code and documentation:
- **399 total mappings** in `code-docs-map.json`
- **245 unique documentation URLs** covered
- Key additions:
  - `Dispatcher._awaitPerspectiveSyncIfNeededAsync` → `core-concepts/perspectives/perspective-sync#dispatcher-integration`
  - `IEventCompletionAwaiter` → `core-concepts/perspectives/event-completion`
  - `SyncEventTracker` → `core-concepts/perspectives/perspective-sync#tracker-implementation`
  - Azure Service Bus classes → `components/transports/azure-service-bus#*`
  - Tracing components → `observability/tracing#*`

### Code-Tests Linking (`<tests>` tags)

Added test coverage awareness:
- **813 code symbols** with test mappings
- **5,480 test methods** indexed
- Convention-based discovery (`ClassNameTests` → `ClassName`)
- Explicit `<tests>` tags for complex mappings

---

## Upgrade Guide

1. **No breaking changes** - Direct upgrade should work
2. **Optional:** Take advantage of new observability callbacks on sync methods for better debugging
3. **Recommended:** If you have receptors with `[AwaitPerspectiveSync]` that handle commands, they will now execute faster (no unnecessary waiting)

---

## Contributors

- Claude Opus 4.5 (AI pair programmer)
