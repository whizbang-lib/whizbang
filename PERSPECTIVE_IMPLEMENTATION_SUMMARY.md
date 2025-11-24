# Perspective Invocation & Distributed Inbox Implementation Summary

**Status**: ✅ Database schema & message queue complete | ⏳ Perspective invocation in progress

This document provides complete implementation guidance for the remaining work to get perspectives working with distributed inbox processing.

---

## Completed Work ✅

### 1. Database Migrations
**Files**:
- `/whizbang/src/Whizbang.Data.Dapper.Custom/Migrations/002_add_message_queue_leasing.postgresql.sql`
- `/whizbang/src/Whizbang.Data.Dapper.Custom/Migrations/002_add_message_queue_leasing.sqlite.sql`

**Tables Created**:
- `whizbang_message_queue` - Pending messages with lease-based coordination
- `whizbang_processed_messages` (renamed from `whizbang_inbox`) - Idempotency tracking

### 2. Message Queue Interface & Implementation
**Files**:
- `/whizbang/src/Whizbang.Core/Messaging/IMessageQueue.cs`
- `/whizbang/src/Whizbang.Data.Dapper.Postgres/DapperPostgresMessageQueue.cs`

**Key Methods**:
1. `EnqueueAndLeaseAsync()` - Atomic enqueue + lease for instant processing
2. `CompleteAsync()` - Mark processed + delete from queue
3. `LeaseOrphanedMessagesAsync()` - Fallback for crash recovery

---

## Remaining Work ⏳

### Phase 1: IPerspectiveInvoker (Scoped Event Queueing)

**Purpose**: Queue events within a scope, invoke perspectives when scope disposes.

**File to Create**: `/whizbang/src/whizbang/src/Whizbang.Core/Perspectives/IPerspectiveInvoker.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Queues events within a scope and invokes perspectives when scope completes.
/// Implements Unit of Work pattern for perspective materialization.
/// Registered as Scoped service - one instance per HTTP request or message batch.
/// </summary>
public interface IPerspectiveInvoker : IAsyncDisposable {
  /// <summary>
  /// Queues an event to be sent to perspectives when scope completes.
  /// Called by Event Store after persisting event.
  /// Thread-safe for concurrent queueing within a scope.
  /// </summary>
  void QueueEvent(IEvent @event);

  /// <summary>
  /// Invokes perspectives for all queued events.
  /// Automatically called on scope disposal (IAsyncDisposable).
  /// Can be called manually for explicit control.
  /// </summary>
  Task InvokePerspectivesAsync(CancellationToken cancellationToken = default);
}
```

---

### Phase 2: PerspectiveInvokerGenerator (Source Generator)

**Purpose**: Generate AOT-compatible routing from events to `IPerspectiveOf<TEvent>` implementations.

#### A. Create PerspectiveInfo Value Record

**File**: `/whizbang/src/Whizbang.Generators/PerspectiveInfo.cs`

```csharp
namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered perspective.
/// Used for incremental generator caching.
/// </summary>
internal sealed record PerspectiveInfo(
  string ClassName,
  string EventType
);
```

#### B. Create PerspectiveInvokerGenerator

**File**: `/whizbang/src/Whizbang.Generators/PerspectiveInvokerGenerator.cs`

**Pattern**: Similar to `ReceptorDiscoveryGenerator.cs`

**Key Steps**:
1. Discover classes implementing `IPerspectiveOf<TEvent>`
2. Extract fully qualified type names for class and event type
3. Group by event type (one perspective class can handle multiple events)
4. Generate `GeneratedPerspectiveInvoker : IPerspectiveInvoker`

**Implementation Pseudocode**:
```csharp
[Generator]
public class PerspectiveInvokerGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.IPerspectiveOf";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline: Discover IPerspectiveOf implementations
    var perspectives = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate output
    context.RegisterSourceOutput(
        perspectives.Collect(),
        static (ctx, perspectives) => GeneratePerspectiveInvoker(ctx, perspectives!)
    );
  }

  private static PerspectiveInfo? ExtractPerspectiveInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);

    if (classSymbol is null) return null;

    // Find IPerspectiveOf<TEvent> interfaces
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => i.OriginalDefinition.ToDisplayString() == PERSPECTIVE_INTERFACE_NAME + "<TEvent>");

    // Return one PerspectiveInfo per event type (a perspective can handle multiple events)
    foreach (var iface in perspectiveInterfaces) {
      if (iface.TypeArguments.Length == 1) {
        yield return new PerspectiveInfo(
            ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            EventType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        );
      }
    }
  }

  private static void GeneratePerspectiveInvoker(
      SourceProductionContext context,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) return;

    // Group perspectives by event type
    var perspectivesByEvent = perspectives.GroupBy(p => p.EventType);

    var routingCode = new StringBuilder();
    foreach (var group in perspectivesByEvent) {
      var eventType = group.Key;

      routingCode.AppendLine($@"
        if (eventType == typeof({eventType})) {{
          var perspectives = _serviceProvider.GetServices<IPerspectiveOf<{eventType}>>();

          async Task PublishToPerspectives(IEvent evt) {{
            var typedEvt = ({eventType})evt;
            foreach (var perspective in perspectives) {{
              await perspective.Update(typedEvt);
            }}
          }}

          return PublishToPerspectives;
        }}");
    }

    // Use template (see below) and replace routing region
    var source = TemplateUtilities.ReplaceRegion(template, "PERSPECTIVE_ROUTING", routingCode.ToString());
    context.AddSource("PerspectiveInvoker.g.cs", source);
  }
}
```

#### C. Create Template

**File**: `/whizbang/src/Whizbang.Generators/Templates/PerspectiveInvokerTemplate.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Generated;

#region HEADER
// Auto-generated
#endregion

internal sealed class GeneratedPerspectiveInvoker : IPerspectiveInvoker {
  private readonly IServiceProvider _serviceProvider;
  private readonly List<IEvent> _queuedEvents = new();
  private readonly object _lock = new();
  private bool _disposed = false;

  public GeneratedPerspectiveInvoker(IServiceProvider serviceProvider) {
    _serviceProvider = serviceProvider;
  }

  public void QueueEvent(IEvent @event) {
    if (@event == null) throw new ArgumentNullException(nameof(@event));

    lock (_lock) {
      if (_disposed) {
        throw new ObjectDisposedException(nameof(GeneratedPerspectiveInvoker));
      }
      _queuedEvents.Add(@event);
    }
  }

  public async Task InvokePerspectivesAsync(CancellationToken ct = default) {
    List<IEvent> eventsToProcess;

    lock (_lock) {
      if (_queuedEvents.Count == 0) return;
      eventsToProcess = new List<IEvent>(_queuedEvents);
      _queuedEvents.Clear();
    }

    // Invoke perspectives for each queued event
    foreach (var @event in eventsToProcess) {
      var publisher = _getPerspectivePublisher(@event, @event.GetType());
      if (publisher != null) {
        await publisher(@event);
      }
    }
  }

  private PerspectivePublisher? _getPerspectivePublisher(IEvent @event, Type eventType) {
    #region PERSPECTIVE_ROUTING
    // Generated routing code inserted here
    #endregion

    return null;  // No perspectives for this event type
  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      _disposed = true;
      await InvokePerspectivesAsync();
    }
  }
}

public delegate Task PerspectivePublisher(IEvent @event);
```

**Don't forget to add template to `.csproj`**:
```xml
<ItemGroup>
  <Compile Remove="Templates\**\*.cs" />
  <None Include="Templates\**\*.cs" />
  <EmbeddedResource Include="Templates\**\*.cs" />
</ItemGroup>
```

---

### Phase 3: Modify Event Store Implementations

**Purpose**: Queue events to `IPerspectiveInvoker` after storing them.

#### A. InMemoryEventStore

**File**: `/whizbang/src/Whizbang.Core/Messaging/InMemoryEventStore.cs`

**Changes**:
```csharp
// Add field
private readonly IPerspectiveInvoker? _perspectiveInvoker;

// Update constructor
public InMemoryEventStore(
  IPolicyEngine policyEngine,
  IPerspectiveInvoker? perspectiveInvoker = null,  // Optional
  ILogger<InMemoryEventStore>? logger = null
) {
  _policyEngine = policyEngine;
  _perspectiveInvoker = perspectiveInvoker;
  _logger = logger;
}

// In AppendAsync method, after storing:
_streams.GetOrAdd(streamId, _ => new List<IMessageEnvelope>()).Add(envelope);

// Queue for perspective invocation at scope disposal
if (_perspectiveInvoker != null && envelope.GetPayload() is IEvent @event) {
  _perspectiveInvoker.QueueEvent(@event);
}
```

#### B. DapperPostgresEventStore

**File**: `/whizbang/src/Whizbang.Data.Dapper.Postgres/DapperPostgresEventStore.cs`

**Same pattern** - add `IPerspectiveInvoker?` parameter, queue after successful INSERT.

#### C. DapperSqliteEventStore

**File**: `/whizbang/src/Whizbang.Data.Dapper.Sqlite/DapperSqliteEventStore.cs`

**Same pattern** - add `IPerspectiveInvoker?` parameter, queue after successful INSERT.

---

### Phase 4: Modify Dispatcher to Write to Event Store

**File**: `/whizbang/src/Whizbang.Core/Dispatcher.cs`

**Changes**:
```csharp
// Add fields
private readonly IEventStore? _eventStore;
private readonly IPolicyEngine? _policyEngine;

// Update constructor
public Dispatcher(
  IServiceProvider serviceProvider,
  IEventStore? eventStore = null,
  IPolicyEngine? policyEngine = null,
  IOutbox? outbox = null,
  ITraceStore? traceStore = null,
  JsonSerializerOptions? jsonOptions = null
) {
  _serviceProvider = serviceProvider;
  _eventStore = eventStore;
  _policyEngine = policyEngine;
  _outbox = outbox;
  _traceStore = traceStore;
  _jsonOptions = jsonOptions;
}

// In PublishAsync method (around line 425):
public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : IEvent {
  if (@event == null) throw new ArgumentNullException(nameof(@event));

  var eventType = @event.GetType();

  // 1. Write to Event Store FIRST (queues to PerspectiveInvoker)
  if (_eventStore != null && _policyEngine != null) {
    var aggregateId = _policyEngine.GetAggregateId(@event);
    if (aggregateId != Guid.Empty) {
      // Need to create envelope - check if we already have one in current context
      // Or create new one with current hop
      var envelope = CreateEnvelopeForEvent(@event);
      await _eventStore.AppendAsync(aggregateId, envelope, default);
    }
  }

  // 2. Invoke event handlers (existing code)
  var publisher = _getReceptorPublisher(@event, eventType);
  await publisher(@event);

  // 3. Write to outbox (existing code)
  if (_outbox != null && _jsonOptions != null) {
    await PublishToOutboxAsync(@event, eventType);
  }
}

// Helper method to create envelope (may already exist)
private IMessageEnvelope CreateEnvelopeForEvent<TEvent>(TEvent @event) where TEvent : IEvent {
  var envelope = new MessageEnvelope<TEvent>(
    MessageId.New(),
    @event,
    new List<MessageHop>()
  );

  envelope.AddHop(new MessageHop {
    Type = HopType.Current,
    ServiceName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown",
    Timestamp = DateTimeOffset.UtcNow
  });

  return envelope;
}
```

---

### Phase 5: Modify ServiceBusConsumerWorker

**Purpose**: Write incoming messages to queue + lease, then process immediately.

**File**: `/whizbang/src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs`

**Major Changes**:
```csharp
public class ServiceBusConsumerWorker : BackgroundService {
  private readonly ITransport _transport;
  private readonly IServiceProvider _serviceProvider;  // Changed from IDispatcher
  private readonly IMessageQueue _messageQueue;  // New
  private readonly ILogger<ServiceBusConsumerWorker> _logger;
  private readonly string _instanceId;  // New
  private readonly ServiceBusConsumerOptions _options;

  public ServiceBusConsumerWorker(
    ITransport transport,
    IServiceProvider serviceProvider,
    IMessageQueue messageQueue,
    ILogger<ServiceBusConsumerWorker> logger,
    ServiceBusConsumerOptions? options = null
  ) {
    _transport = transport;
    _serviceProvider = serviceProvider;
    _messageQueue = messageQueue;
    _logger = logger;
    _instanceId = Guid.NewGuid().ToString("N");
    _options = options ?? new ServiceBusConsumerOptions();
  }

  // HandleMessageAsync changes:
  private async Task HandleMessageAsync(IMessageEnvelope envelope, CancellationToken ct) {
    try {
      // 1. Enqueue and lease atomically (includes idempotency check)
      var queuedMessage = new QueuedMessage {
        MessageId = envelope.MessageId.Value,
        EventType = envelope.GetPayload().GetType().FullName!,
        EventData = JsonSerializer.Serialize(envelope.GetPayload(), _jsonOptions),
        Metadata = JsonSerializer.Serialize(/* envelope metadata */)
      };

      var wasEnqueued = await _messageQueue.EnqueueAndLeaseAsync(
        queuedMessage,
        _instanceId,
        leaseDuration: TimeSpan.FromMinutes(5),
        ct
      );

      if (!wasEnqueued) {
        _logger.LogInformation(
          "Message {MessageId} already processed, skipping",
          envelope.MessageId
        );
        return;
      }

      // 2. Process message in NEW SCOPE
      await using var scope = _serviceProvider.CreateAsyncScope();
      var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

      await dispatcher.PublishAsync(envelope.GetPayload());

      // 3. Mark complete (delete from queue + mark processed)
      await _messageQueue.CompleteAsync(
        envelope.MessageId.Value,
        _instanceId,
        "ServiceBusConsumerWorker",
        ct
      );

      // Scope disposes here → PerspectiveInvoker.DisposeAsync() → perspectives invoked

    } catch (Exception ex) {
      _logger.LogError(ex, "Error processing message {MessageId}", envelope.MessageId);
      // Don't rethrow - let lease expire, another instance will retry
    }
  }
}
```

---

### Phase 6: Create InboxProcessorWorker (Fallback)

**Purpose**: Periodic polling to process orphaned messages from crashed instances.

**File**: `/whizbang/src/Whizbang.Core/Workers/InboxProcessorWorker.cs` (NEW)

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background service that processes orphaned messages from crashed instances.
/// Polls for messages with expired leases and processes them.
/// </summary>
public class InboxProcessorWorker : BackgroundService {
  private readonly IServiceProvider _serviceProvider;
  private readonly IMessageQueue _messageQueue;
  private readonly ILogger<InboxProcessorWorker> _logger;
  private readonly string _instanceId;

  public InboxProcessorWorker(
    IServiceProvider serviceProvider,
    IMessageQueue messageQueue,
    ILogger<InboxProcessorWorker> logger
  ) {
    _serviceProvider = serviceProvider;
    _messageQueue = messageQueue;
    _logger = logger;
    _instanceId = Guid.NewGuid().ToString("N");
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation(
      "InboxProcessorWorker started with instance ID: {InstanceId}",
      _instanceId
    );

    while (!stoppingToken.IsCancellationRequested) {
      try {
        // Lease orphaned messages
        var messages = await _messageQueue.LeaseOrphanedMessagesAsync(
          _instanceId,
          maxCount: 100,
          leaseDuration: TimeSpan.FromMinutes(5),
          stoppingToken
        );

        if (messages.Count == 0) {
          // No orphaned messages, wait before retrying
          await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
          continue;
        }

        // Process each message in its own scope
        foreach (var message in messages) {
          await using var scope = _serviceProvider.CreateAsyncScope();
          var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

          try {
            // Deserialize and dispatch
            var eventPayload = DeserializeEvent(message);
            await dispatcher.PublishAsync(eventPayload);

            // Mark complete
            await _messageQueue.CompleteAsync(
              message.MessageId,
              _instanceId,
              "InboxProcessorWorker",
              stoppingToken
            );

            // Scope disposes → perspectives invoked

          } catch (Exception ex) {
            _logger.LogError(ex,
              "Error processing orphaned message {MessageId}",
              message.MessageId
            );
            // Let lease expire, will be retried
          }
        }

      } catch (Exception ex) {
        _logger.LogError(ex, "Error in inbox processor loop");
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
      }
    }
  }

  private IEvent DeserializeEvent(QueuedMessage message) {
    // Deserialize based on event_type
    var type = Type.GetType(message.EventType);
    return (IEvent)JsonSerializer.Deserialize(message.EventData, type, _jsonOptions)!;
  }
}
```

---

### Phase 7: DI Registration Updates

**Files to modify**:

#### A. Event Store Registration (Postgres)

**File**: `/whizbang/src/Whizbang.Data.Dapper.Postgres/WhizbangPostgresExtensions.cs`

```csharp
services.AddSingleton<IEventStore>(sp => new DapperPostgresEventStore(
  sp.GetRequiredService<IPolicyEngine>(),
  sp.GetService<IPerspectiveInvoker>(),  // Add this
  sp.GetRequiredService<IDbConnectionFactory>(),
  sp.GetRequiredService<ILogger<DapperPostgresEventStore>>()
));
```

#### B. Dispatcher Registration

**File**: Find where Dispatcher is registered (likely in `Whizbang.Core` extensions)

```csharp
services.AddScoped<IDispatcher>(sp => new GeneratedDispatcher(
  sp,
  sp.GetService<IEventStore>(),     // Add this
  sp.GetService<IPolicyEngine>(),   // Add this
  sp.GetService<IOutbox>(),
  sp.GetService<ITraceStore>()
));
```

#### C. PerspectiveInvoker Registration

**Auto-generated** by `PerspectiveInvokerGenerator` → creates `AddWhizbangPerspectiveInvoker()` extension.

Call in application startup:
```csharp
services.AddWhizbangPerspectiveInvoker();  // Scoped
```

#### D. MessageQueue Registration

```csharp
services.AddSingleton<IMessageQueue, DapperPostgresMessageQueue>();
```

#### E. Workers Registration

```csharp
services.AddHostedService<ServiceBusConsumerWorker>();
services.AddHostedService<InboxProcessorWorker>();  // Fallback
```

---

### Phase 8: Integration Test Updates

**File**: `/whizbang/samples/ECommerce/tests/ECommerce.Integration.Tests/Fixtures/SharedIntegrationFixture.cs`

**Add to ConfigureServices**:
```csharp
// Register message queue
builder.Services.AddSingleton<IMessageQueue, DapperPostgresMessageQueue>();

// Register perspective invoker (generated)
builder.Services.AddWhizbangPerspectiveInvoker();

// Workers already registered via AddHostedService calls
```

---

## Testing Checklist

After completing all phases:

1. ✅ **Build**: `dotnet clean && dotnet build`
   - Verify generated `GeneratedPerspectiveInvoker.g.cs` exists
   - Check routing code includes all event types

2. ✅ **Run Integration Tests**: `dotnet test` in `ECommerce.Integration.Tests`
   - All 15 tests should pass
   - Verify perspectives are invoked
   - Verify database tables populated

3. ✅ **Multi-Instance Test** (optional):
   - Start 2+ instances of same service
   - Publish 100 messages
   - Verify no duplicate processing (check `processed_messages` table)
   - Verify lease distribution across instances

4. ✅ **Crash Recovery Test** (optional):
   - Enqueue messages
   - Kill service mid-processing
   - Verify InboxProcessorWorker picks up orphaned messages

5. ✅ **Format**: `dotnet format`

---

## Architecture Summary

**Complete Event Flow**:
```
1. HTTP Request / Inbox Message
   ↓
2. Dispatcher.PublishAsync(event)
   ↓
3. Event Store.AppendAsync(event)
   └→ PerspectiveInvoker.QueueEvent(event)  [queued, not invoked yet]
   ↓
4. Event Handlers (IReceptor<TEvent>)
   ↓
5. Outbox.StoreAsync(event)
   ↓
6. OutboxPublisherWorker → Service Bus
   ↓
7. ServiceBusConsumerWorker receives
   ↓
8. MessageQueue.EnqueueAndLeaseAsync() [idempotency + lease]
   ↓
9. Create Scope → Dispatcher.PublishAsync(event) [SAME FLOW AS STEP 2]
   ↓
10. Scope Disposes → PerspectiveInvoker.DisposeAsync()
   ↓
11. Perspectives invoked (IPerspectiveOf<TEvent>.Update())
   ↓
12. Database tables materialized
```

**Crash Recovery**:
- If instance crashes at step 9, lease expires
- InboxProcessorWorker picks up via `LeaseOrphanedMessagesAsync()`
- Processes in new scope
- Perspectives still invoked correctly

---

## File Checklist

**Created** ✅:
- [x] Database migrations (002_*.sql)
- [x] IMessageQueue interface
- [x] DapperPostgresMessageQueue implementation

**To Create** ⏳:
- [ ] IPerspectiveInvoker interface
- [ ] PerspectiveInfo.cs value record
- [ ] PerspectiveInvokerGenerator.cs
- [ ] PerspectiveInvokerTemplate.cs
- [ ] InboxProcessorWorker.cs

**To Modify** ⏳:
- [ ] InMemoryEventStore (add IPerspectiveInvoker parameter)
- [ ] DapperPostgresEventStore (add IPerspectiveInvoker parameter)
- [ ] DapperSqliteEventStore (add IPerspectiveInvoker parameter)
- [ ] Dispatcher (add IEventStore + IPolicyEngine parameters, call Event Store in PublishAsync)
- [ ] ServiceBusConsumerWorker (use IMessageQueue + scoped processing)
- [ ] DI registration files (pass new dependencies)
- [ ] Integration test fixture (register new services)

---

## Next Steps

Continue implementation starting with **Phase 1: IPerspectiveInvoker interface** above.

The implementation is well-structured with clear separation of concerns:
- **Message Queue**: Distributed coordination with lease-based processing
- **Perspective Invoker**: Scoped event batching with Unit of Work pattern
- **Source Generator**: AOT-compatible zero-reflection routing
- **Workers**: Primary (ServiceBus) + Fallback (Orphaned messages)

All integration tests should pass once complete! 🎯
