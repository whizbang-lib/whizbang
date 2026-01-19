# Whizbang VSCode Extension - Implementation Plan

## Status

- **Created**: 2025-11-02
- **Target Version**: v0.4.0 (MVP), v0.5.0 (Advanced), v0.6.0 (Power User)
- **Dependencies**: v0.2.0 (caller info capture), v0.3.0 (persistent trace store)
- **Priority**: HIGH - Killer feature that differentiates Whizbang

---

## Executive Summary

Build a **VSCode extension** that enables developers to:
1. **Navigate message flows** during development (GitLens-style code annotations)
2. **Debug distributed systems** like local code (runtime trace navigation)
3. **Visualize message flows** graphically (flow diagrams, live monitoring)

**The Vision**:
- **Development Time**: Click "3 receptors handle this" → See all handlers (even across services)
- **Runtime**: Click message in trace → Jump to exact source code line
- **Visualization**: See complete distributed flows, time-travel through execution

**Key Differentiator**: No other event-driven framework provides this level of IDE integration.

---

## Part 1: Development-Time Navigation (GitLens-Style)

### The Problem

When writing event-driven code:
- ❌ "Which receptors handle this message?" → Manual search
- ❌ "Where is this message dispatched?" → Grep across projects
- ❌ "What messages does this receptor handle?" → Check code, hope it's documented
- ❌ Cross-service message flow → Impossible to track without documentation

### The Solution (Code Lens Annotations)

**Inline annotations** showing message flow relationships:

```csharp
// File: Orders.Service/Commands/CreateOrderCommand.cs
public record CreateOrderCommand : ICommand {
    // Whizbang: ↑ 2 dispatchers | ↓ 3 receptors
    public string OrderId { get; init; }
}

// File: Orders.Service/Dispatchers/OrderDispatcher.cs
public class OrderDispatcher {
    public async Task CreateOrderAsync(CreateOrderRequest request) {
        var cmd = new CreateOrderCommand { OrderId = request.Id };

        // Whizbang: → OrdersReceptor.HandleCreateOrderAsync
        //           → AuditReceptor.HandleAsync
        //           → AnalyticsReceptor.TrackOrderCreated
        await _dispatcher.DispatchAsync(cmd);
    }
}

// File: Orders.Service/Receptors/OrdersReceptor.cs
public class OrdersReceptor {
    // Whizbang: ← OrderDispatcher.CreateOrderAsync
    //           ← OrderSaga.RetryCreateOrder
    public async Task HandleCreateOrderAsync(CreateOrderCommand cmd, PolicyContext ctx) {
        var evt = new OrderCreatedEvent { OrderId = cmd.OrderId };

        // Whizbang: → InventoryReceptor.HandleOrderCreatedAsync (Inventory.Service)
        //           → NotificationsReceptor.HandleOrderCreatedAsync (Notifications.Service)
        //           → AnalyticsReceptor.HandleOrderCreatedAsync
        await _dispatcher.DispatchAsync(evt, ctx);
    }
}
```

**Click annotation** → Navigate to dispatcher/receptor (even across services!)

---

### Feature 1: Message Type Annotations

**On message classes**, show dispatchers, receptors, and perspectives:

```csharp
// Commands show dispatchers and receptors
// Whizbang: ↑ 2 dispatchers | ↓ 3 receptors | Show Flow Diagram
public record CreateOrderCommand : ICommand {
    public string OrderId { get; init; }
}

// Events show dispatchers, receptors, AND perspectives
// Whizbang: ↑ 2 dispatchers | ↓ 3 receptors | 📊 4 perspectives | Show Flow Diagram
public record OrderCreatedEvent : IEvent {
    public string OrderId { get; init; }
}
```

**Actions**:
- **Click "2 dispatchers"** → Show list:
  ```
  Dispatchers (2):
  ├─ OrderDispatcher.CreateOrderAsync (Orders.Service)
  └─ OrderSaga.RetryCreateOrder (Orders.Service)

  [Go to First] [Show All]
  ```

- **Click "3 receptors"** → Show list:
  ```
  Receptors (3):
  ├─ OrdersReceptor.HandleCreateOrderAsync (Orders.Service)
  ├─ AuditReceptor.HandleAsync (Orders.Service)
  └─ AnalyticsReceptor.TrackOrderCreated (Analytics.Service)

  [Go to First] [Show All]
  ```

- **Click "4 perspectives"** → Show list:
  ```
  Perspectives (4):
  ├─ OrderListPerspective.Update (Orders.Service)
  │  Updates: Order list view
  ├─ CustomerStatsPerspective.Update (Analytics.Service)
  │  Updates: Customer statistics
  ├─ SearchIndexPerspective.Update (Search.Service)
  │  Updates: Order search index
  └─ CachePerspective.Update (Orders.Service)
     Updates: Order cache

  [Go to First] [Show All]
  ```

- **Click "Show Flow Diagram"** → Visual graph view

**Implementation Strategy**:

**1. Static Analysis** (fast, always available):
```typescript
// Find all DispatchAsync calls with this message type
function findDispatchers(messageType: string): Location[] {
    // Search workspace for:
    // - .DispatchAsync<MessageType>(
    // - .DispatchAsync(new MessageType
    // - .DispatchAsync(messageVariable) where messageVariable : MessageType

    return vscode.workspace.findFiles('**/*.cs')
        .then(files => parseFilesForDispatchers(files, messageType));
}

// Find all receptors that handle this message type
function findReceptors(messageType: string): Location[] {
    // Search workspace for:
    // - HandleAsync(MessageType msg, ...)
    // - Handle(MessageType msg, ...)
    // - Method signature with parameter of MessageType

    return vscode.workspace.findFiles('**/*.cs')
        .then(files => parseFilesForReceptors(files, messageType));
}
```

**2. Roslyn Analysis** (accurate, requires compilation):
```csharp
// Use Roslyn to find all invocations
var compilation = await project.GetCompilationAsync();
var dispatcherSymbol = compilation.GetTypeByMetadataName("IDispatcher");
var dispatchMethod = dispatcherSymbol.GetMembers("DispatchAsync");

foreach (var syntaxTree in compilation.SyntaxTrees) {
    var model = compilation.GetSemanticModel(syntaxTree);
    var invocations = syntaxTree.GetRoot()
        .DescendantNodes()
        .OfType<InvocationExpressionSyntax>();

    foreach (var invocation in invocations) {
        var symbolInfo = model.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol == dispatchMethod) {
            // Extract message type from generic parameter or argument
            var messageType = ExtractMessageType(invocation, model);
            // Record: this location dispatches messageType
        }
    }
}
```

**3. Cross-Project Support**:
```json
// .whizbang/message-registry.json (generated at build time)
{
  "messages": [
    {
      "type": "CreateOrderCommand",
      "assembly": "Orders.Contracts",
      "dispatchers": [
        {
          "class": "OrderDispatcher",
          "method": "CreateOrderAsync",
          "project": "Orders.Service",
          "file": "Dispatchers/OrderDispatcher.cs",
          "line": 45
        }
      ],
      "receptors": [
        {
          "class": "OrdersReceptor",
          "method": "HandleCreateOrderAsync",
          "project": "Orders.Service",
          "file": "Receptors/OrdersReceptor.cs",
          "line": 23
        },
        {
          "class": "InventoryReceptor",
          "method": "HandleOrderCreatedAsync",
          "project": "Inventory.Service",
          "file": "Receptors/InventoryReceptor.cs",
          "line": 67
        }
      ],
      "perspectives": []  // Commands don't have perspectives
    },
    {
      "type": "OrderCreatedEvent",
      "assembly": "Orders.Contracts",
      "dispatchers": [
        {
          "class": "OrdersReceptor",
          "method": "HandleCreateOrderAsync",
          "project": "Orders.Service",
          "file": "Receptors/OrdersReceptor.cs",
          "line": 45
        }
      ],
      "receptors": [
        {
          "class": "InventoryReceptor",
          "method": "HandleOrderCreatedAsync",
          "project": "Inventory.Service",
          "file": "Receptors/InventoryReceptor.cs",
          "line": 23
        }
      ],
      "perspectives": [
        {
          "class": "OrderListPerspective",
          "method": "Update",
          "project": "Orders.Service",
          "file": "Perspectives/OrderListPerspective.cs",
          "line": 15,
          "updateTarget": "Order list view"
        },
        {
          "class": "CustomerStatsPerspective",
          "method": "Update",
          "project": "Analytics.Service",
          "file": "Perspectives/CustomerStatsPerspective.cs",
          "line": 23,
          "updateTarget": "Customer statistics"
        },
        {
          "class": "SearchIndexPerspective",
          "method": "Update",
          "project": "Search.Service",
          "file": "Perspectives/SearchIndexPerspective.cs",
          "line": 18,
          "updateTarget": "Order search index"
        },
        {
          "class": "CachePerspective",
          "method": "Update",
          "project": "Orders.Service",
          "file": "Perspectives/CachePerspective.cs",
          "line": 31,
          "updateTarget": "Order cache"
        }
      ]
    }
  ]
}
```

**Build-time generation** (MSBuild task):
```xml
<!-- In Directory.Build.targets -->
<Target Name="GenerateWhizbangMessageRegistry" AfterTargets="Compile">
  <Exec Command="dotnet whizbang-analyze $(ProjectDir) --output .whizbang/message-registry.json" />
</Target>
```

---

### Feature 2: Dispatcher Annotations

**On DispatchAsync calls**, show where it goes:

```csharp
public async Task CreateOrderAsync(CreateOrderRequest request) {
    var cmd = new CreateOrderCommand { OrderId = request.Id };

    // Whizbang: → 3 receptors | Show Flow
    await _dispatcher.DispatchAsync(cmd);
    //                            ↑ Click here
}
```

**Hover tooltip**:
```
CreateOrderCommand will be handled by:

├─ OrdersReceptor.HandleCreateOrderAsync (Orders.Service)
│  Policy: Order Processing Policy
│  Execution: SerialExecutor
│  Topic: orders | Stream: order-{id} | Partition: 3 of 16
│
├─ AuditReceptor.HandleAsync (Orders.Service)
│  Policy: Audit All Commands
│  Execution: ParallelExecutor
│  Topic: audit | Stream: audit-shared
│
└─ AnalyticsReceptor.TrackOrderCreated (Analytics.Service)
   Policy: Analytics Events
   Execution: ParallelExecutor
   Topic: analytics | Stream: analytics-shared

[Go to OrdersReceptor] [Show Flow Diagram]
```

**Features**:
- **Show receptors** (even in other projects/services)
- **Show policy** that will match
- **Show routing** (topic, stream, partition)
- **Click to navigate** to receptor
- **Flow diagram** showing complete dispatch chain

---

### Feature 3: Receptor Annotations

**On receptor methods**, show who dispatches to it:

```csharp
// Whizbang: ← 2 dispatchers | Show Callers
public async Task HandleCreateOrderAsync(CreateOrderCommand cmd, PolicyContext ctx) {
    //                          ↑ Hover here
    // ...
}
```

**Hover tooltip**:
```
CreateOrderCommand is dispatched by:

├─ OrderDispatcher.CreateOrderAsync (Orders.Service:45)
│  Direct dispatch from API endpoint
│
└─ OrderSaga.RetryCreateOrder (Orders.Service:189)
   Retry logic for failed orders

[Go to OrderDispatcher] [Show All Dispatchers]
```

**Features**:
- **Show all dispatchers** (who sends this message)
- **Context** (why they dispatch it)
- **Navigate** to dispatcher source

---

### Feature 4: Perspective Annotations

**On perspective classes**, show which events they consume:

```csharp
// Whizbang: Consumes 4 events | Updates: Order list view
public class OrderListPerspective :
    IPerspectiveOf<OrderCreated>,
    IPerspectiveOf<OrderUpdated>,
    IPerspectiveOf<OrderShipped>,
    IPerspectiveOf<OrderCancelled> {

    private readonly Dictionary<Guid, OrderListItem> _orders;

    // Whizbang: ← 2 dispatchers produce this event
    public Task Update(OrderCreated @event) {
        _orders[@event.OrderId] = new OrderListItem {
            Id = @event.OrderId,
            Status = "Created"
        };
        return Task.CompletedTask;
    }

    public Task Update(OrderUpdated @event) { /* ... */ }
    public Task Update(OrderShipped @event) { /* ... */ }
    public Task Update(OrderCancelled @event) { /* ... */ }
}
```

**Class-level annotation**:
- Shows total number of events consumed
- Shows what data is being updated (read model name)

**Method-level annotation**:
- Shows which dispatchers/receptors produce this event
- Click to navigate to event producers

**Hover tooltip** (on class):
```
OrderListPerspective updates: Order list view

Consumes events:
├─ OrderCreated (2 dispatchers)
│  └─ Updates order list with new order
├─ OrderUpdated (1 dispatcher)
│  └─ Updates order details
├─ OrderShipped (1 dispatcher)
│  └─ Updates order status
└─ OrderCancelled (2 dispatchers)
   └─ Removes or marks order as cancelled

[Show All Events] [Show Flow Diagram]
```

**Hover tooltip** (on Update method):
```
OrderCreated is produced by:

├─ OrdersReceptor.HandleCreateOrderAsync (Orders.Service:45)
│  Primary order creation flow
│
└─ OrderReconciliationJob.RecreateOrder (Orders.Service:189)
   Reconciliation job for missing orders

Also consumed by:
├─ CustomerStatsPerspective.Update (Analytics.Service)
├─ SearchIndexPerspective.Update (Search.Service)
└─ CachePerspective.Update (Orders.Service)

[Go to OrdersReceptor] [Show All Consumers]
```

**Features**:
- **Show event producers** (who creates the events this perspective consumes)
- **Show other perspectives** (who else consumes these events)
- **Navigate** to event producer or other perspective source
- **Read model context** (what data is being built/maintained)

**Discovery Strategy**:

Perspectives are discovered by finding classes implementing `IPerspectiveOf<TEvent>`:

```csharp
// Roslyn analyzer finds:
var perspectiveInterface = compilation.GetTypeByMetadataName("Whizbang.IPerspectiveOf`1");

foreach (var namedType in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type)) {
    var interfaces = namedType.AllInterfaces;

    foreach (var iface in interfaces) {
        if (iface.OriginalDefinition.Equals(perspectiveInterface)) {
            // This is a perspective!
            var eventType = iface.TypeArguments[0];  // Extract TEvent

            // Record: namedType is a perspective that handles eventType
            registry.AddPerspective(namedType, eventType);
        }
    }
}
```

**Multi-Event Perspectives**:

A single perspective can implement multiple `IPerspectiveOf<T>` interfaces:

```json
{
  "perspectives": [
    {
      "class": "OrderListPerspective",
      "project": "Orders.Service",
      "file": "Perspectives/OrderListPerspective.cs",
      "updateTarget": "Order list view",
      "events": [
        {
          "type": "OrderCreated",
          "method": "Update",
          "line": 23
        },
        {
          "type": "OrderUpdated",
          "method": "Update",
          "line": 31
        },
        {
          "type": "OrderShipped",
          "method": "Update",
          "line": 39
        },
        {
          "type": "OrderCancelled",
          "method": "Update",
          "line": 47
        }
      ]
    }
  ]
}
```

---

### Feature 5: Cross-Service Flow Visualization

**"Show Flow Diagram"** command opens graphical view:

```
Message Flow: CreateOrderCommand

┌────────────────────────────────────────────────────────────┐
│                     API Gateway                            │
│                 OrdersController.CreateOrder               │
└─────────────────────────┬──────────────────────────────────┘
                          ↓
                  CreateOrderCommand
                          ↓
┌─────────────────────────┴──────────────────────────────────┐
│                    Orders.Service                          │
│                 OrderDispatcher.CreateOrderAsync           │
└─────────────────────────┬──────────────────────────────────┘
                          ↓
                  CreateOrderCommand (dispatched)
                          ↓
        ┌─────────────────┼─────────────────┐
        ↓                 ↓                 ↓
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│Orders.Service│  │Orders.Service│  │Analytics     │
│OrdersReceptor│  │AuditReceptor │  │Analytics     │
│[RECEPTOR]    │  │[RECEPTOR]    │  │Receptor      │
└──────┬───────┘  └──────────────┘  │[RECEPTOR]    │
       ↓                             └──────────────┘
OrderCreatedEvent (dispatched)
       ↓
       ├──────────────────┬──────────────────┬──────────────────────┐
       ↓                  ↓                  ↓                      ↓
┌──────────────┐  ┌──────────────┐  ┌──────────────┐   ┌─────────────────────┐
│Inventory     │  │Notifications │  │Analytics     │   │PERSPECTIVES         │
│Inventory     │  │Notifications │  │Analytics     │   │(Read Models)        │
│Receptor      │  │Receptor      │  │Receptor      │   ├─────────────────────┤
│[RECEPTOR]    │  │[RECEPTOR]    │  │[RECEPTOR]    │   │📊 OrderList         │
└──────────────┘  └──────────────┘  └──────────────┘   │📊 CustomerStats     │
                                                        │📊 SearchIndex       │
                                                        │📊 Cache             │
                                                        └─────────────────────┘
```

**Interactive**:
- **Click node** → Open source file
- **Hover node** → Show details
- **Color coding** → Blue (receptor), Green (other service), Orange (perspective)
- **Show/hide perspectives** → Toggle read model visibility
- **Export** → PNG, SVG

---

### Feature 6: "Find Message Usages"

**Context menu** on message type:

```
Right-click CreateOrderCommand → Whizbang: Find Message Usages

Results:

Dispatchers (2):
├─ Orders.Service/Dispatchers/OrderDispatcher.cs:45
│  CreateOrderAsync()
│
└─ Orders.Service/Sagas/OrderSaga.cs:189
   RetryCreateOrder()

Receptors (3):
├─ Orders.Service/Receptors/OrdersReceptor.cs:23
│  HandleCreateOrderAsync()
│
├─ Orders.Service/Receptors/AuditReceptor.cs:67
│  HandleAsync()
│
└─ Analytics.Service/Receptors/AnalyticsReceptor.cs:89
   TrackOrderCreated()

Policies (1):
└─ Orders.Service/Configuration/OrderPolicies.cs:42
   When(ctx => ctx.MatchesAggregate<Order>())
```

**Features**:
- **Group by category** (dispatchers, receptors, policies)
- **Show in tree view** (collapsible)
- **Click to navigate**
- **Export** to markdown

---

### Feature 7: Message Flow Breadcrumbs

**Breadcrumb navigation** at top of editor:

```
OrdersController.CreateOrder → OrderDispatcher.CreateOrderAsync → CreateOrderCommand → OrdersReceptor.HandleCreateOrderAsync
                                                                                        ↑ You are here
```

**Features**:
- **Click any breadcrumb** → Navigate to that location
- **Auto-update** as you navigate code
- **Show message type** in bold

---

### Feature 8: Quick Navigation Commands

**Command Palette** commands:

```
Ctrl+Shift+W D    Whizbang: Go to Dispatcher
Ctrl+Shift+W R    Whizbang: Go to Receptor
Ctrl+Shift+W F    Whizbang: Show Message Flow
Ctrl+Shift+W U    Whizbang: Find Message Usages
```

**"Go to Dispatcher"** (when cursor on message type):
- If 1 dispatcher → Jump directly
- If multiple → Show quick pick menu

**"Go to Receptor"** (when cursor on DispatchAsync):
- If 1 receptor → Jump directly
- If multiple → Show quick pick menu

---

## Part 2: Runtime Debugging & Visualization

### Why This Extension?

### The Problem

Debugging distributed systems at runtime:
- ❌ Grep through logs across multiple services
- ❌ Correlate timestamps manually
- ❌ Guess at message flow
- ❌ No way to "jump to code" from production traces
- ❌ Can't visualize cross-service interactions
- ❌ Time-consuming and error-prone

### The Solution (Whizbang VSCode Extension)

With the extension:
- ✅ **Click to jump to source** - From trace → exact file:line
- ✅ **Visual message flows** - See distributed traces graphically
- ✅ **Time-travel debugging** - Scrub through message history
- ✅ **Live monitoring** - Watch messages flow in real-time
- ✅ **Policy debugging** - See why routing decisions were made
- ✅ **Cross-service navigation** - Jump between microservices seamlessly

---

## Data Foundation (Already Built in v0.2.0)

### Caller Information Capture

Every `MessageHop` captures:
```csharp
public record MessageHop {
    // Caller information (zero-overhead, compile-time)
    public string? CallerMemberName { get; init; }      // "HandleCreateOrderAsync"
    public string? CallerFilePath { get; init; }        // "/src/Orders/OrdersReceptor.cs"
    public int? CallerLineNumber { get; init; }         // 127

    // Service identity
    public required string ServiceName { get; init; }   // "Orders.Service"
    public required string MachineName { get; init; }

    // Routing context
    public string? Topic { get; init; }
    public string? StreamKey { get; init; }

    // Timing
    public required DateTimeOffset Timestamp { get; init; }
    public TimeSpan? Duration { get; init; }

    // Policy decisions
    public PolicyDecisionTrail? Trail { get; init; }
}
```

### Trace Storage

```csharp
public interface ITraceStore {
    Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct);
    Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct);
    Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct);
    Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct);
    Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
```

**We have all the data** - just need to visualize it!

---

## Extension Features

### Phase 1: Core Features (v0.4.0)

#### 1. Message Trace View

**Tree View in Sidebar**:
```
WHIZBANG TRACES
├─ 📊 Live Messages (last 100)
│  ├─ 🟢 OrderCreatedEvent (order-12345) - 2s ago
│  ├─ 🟢 InventoryReservedEvent (inventory-456) - 3s ago
│  └─ 🔴 EmailFailedEvent (ERROR) - 5s ago
│
├─ 🔍 Recent Correlations
│  ├─ correlation-abc-123 (5 messages) - 10s ago
│  │  ├─ OrderCreatedEvent
│  │  ├─ InventoryReservedCommand
│  │  ├─ InventoryReservedEvent
│  │  ├─ SendEmailCommand
│  │  └─ EmailSentEvent
│  │
│  └─ correlation-def-456 (3 messages) - 1m ago
│
└─ ⚠️ Errors (last 24h)
   └─ 🔴 NullReferenceException in EmailReceptor (3 occurrences)
```

**Actions**:
- **Click message** → Show details panel
- **Double-click message** → Jump to source code
- **Right-click** → "Show Causal Chain", "Show Correlation", "Show Policy Trail"

#### 2. Jump to Source

**Primary Feature**: Click any message → VSCode opens the file at the exact line.

**Implementation**:
```typescript
function jumpToSource(hop: MessageHop) {
    const filePath = hop.callerFilePath;
    const lineNumber = hop.callerLineNumber;

    vscode.workspace.openTextDocument(filePath).then(doc => {
        vscode.window.showTextDocument(doc).then(editor => {
            const position = new vscode.Position(lineNumber - 1, 0);
            editor.selection = new vscode.Selection(position, position);
            editor.revealRange(new vscode.Range(position, position),
                vscode.TextEditorRevealType.InCenter);
        });
    });
}
```

**Cross-Service Support**:
- If file path is absolute → open directly
- If file is in workspace → open in current window
- If file is in different repo → prompt to open in new window
- Support multi-root workspaces

#### 3. Message Details Panel

**Webview Panel** showing complete message context:

```
┌─────────────────────────────────────────────────┐
│ OrderCreatedEvent                               │
│ order-12345                                     │
├─────────────────────────────────────────────────┤
│ Identity                                        │
│   MessageId:      msg-abc-123                   │
│   CorrelationId:  corr-xyz-789                  │
│   CausationId:    msg-parent-456                │
│                                                 │
│ Journey (3 hops)                                │
│   1. API Gateway                                │
│      ├─ Service: API.Gateway                    │
│      ├─ File: /src/API/OrdersController.cs:67  │ [Jump]
│      ├─ Method: CreateOrderAsync                │
│      └─ Time: 14:23:45.123                      │
│                                                 │
│   2. Orders Service                             │
│      ├─ Service: Orders.Service                 │
│      ├─ File: /src/Orders/OrdersReceptor.cs:127│ [Jump]
│      ├─ Method: HandleCreateOrderAsync          │
│      ├─ Time: 14:23:45.134 (+11ms)              │
│      └─ Policy: "Order Processing Policy"       │ [Show Trail]
│                                                 │
│   3. Event Store                                │
│      ├─ Service: Orders.Service                 │
│      ├─ Topic: orders                           │
│      ├─ Partition: 3 of 16                      │
│      ├─ Sequence: 789                           │
│      └─ Time: 14:23:45.245 (+111ms)             │
│                                                 │
│ Payload                                         │
│   {                                             │
│     "orderId": "12345",                         │
│     "items": [...],                             │
│     "total": 59.98                              │
│   }                                             │
│                                                 │
│ Metadata                                        │
│   priority: high                                │
│   source: api-gateway                           │
│   enriched: true                                │
└─────────────────────────────────────────────────┘
```

**Interactive Elements**:
- **[Jump]** buttons → Jump to source code
- **[Show Trail]** → Show policy decision trail
- **Expandable sections** → Progressive disclosure
- **Copy buttons** → Copy IDs, payload, etc.

#### 4. Visual Flow Diagram

**Graph View** using D3.js or similar:

```
   API Gateway
   (CreateOrder)
        ↓
   Orders Service ──────┐
   (OrderCreated)       │
        ↓               │
        ├───> Inventory Service
        │     (ReserveInventory)
        │            ↓
        │     (InventoryReserved)
        │
        └───> Notifications Service
              (SendEmail)
                   ↓
              (EmailSent)
```

**Features**:
- **Click node** → Show message details
- **Double-click node** → Jump to source
- **Hover node** → Show tooltip (service, method, timing)
- **Color coding** → Green (success), Red (error), Yellow (slow)
- **Timing annotations** → Show duration between messages
- **Zoom/pan** → Navigate large flows

#### 5. Policy Decision Trail

**Dedicated View** for policy debugging:

```
Policy Evaluation: OrderCreatedEvent

1. ❌ High Priority Policy
   Rule: ctx.HasTag("priority:critical")
   Reason: No "priority:critical" tag found

2. ❌ Bulk Order Policy
   Rule: ctx.GetMetadata("itemCount") > 100
   Reason: itemCount is 2 (not > 100)

3. ✅ Order Processing Policy
   Rule: ctx.MatchesAggregate<Order>()
   Matched: TRUE

   Configuration Applied:
   ├─ Topic: orders
   ├─ Stream: order-12345
   ├─ Execution: SerialExecutor
   ├─ Partitions: 16
   ├─ Partition Router: HashPartitionRouter
   └─ Sequence Provider: InMemorySequenceProvider

   [View Source: OrderPolicies.cs:42]
```

**Features**:
- **Clear match/no-match indicators**
- **Explanations for why policies matched/didn't match**
- **Jump to policy source code**
- **Show applied configuration**

---

### Phase 2: Advanced Features (v0.5.0)

#### 6. Time-Travel Debugging

**Timeline Scrubber**:

```
Timeline: correlation-abc-123
14:23:45 ─────────────────────────────────────────> 14:23:46

Events:
14:23:45.123 OrderCreated
14:23:45.134 CreateOrder (Command)
14:23:45.245 OrderCreated (Event)
14:23:45.267 ReserveInventory (Command)
14:23:45.289 InventoryReserved (Event)
14:23:45.312 SendEmail (Command)
14:23:45.456 EmailSent (Event)

[◀ Prev] [▶ Next] [⏸ Pause] [⏩ Play]

Current Position: 14:23:45.245
Viewing: OrderCreated (Event)
```

**Features**:
- **Scrub through timeline** → See system state at any point
- **Play/pause** → Animate message flow
- **Step forward/backward** → Navigate event by event
- **Speed control** → 0.5x, 1x, 2x, 4x playback
- **Bookmarks** → Mark important points in timeline

#### 7. Live Monitoring Dashboard

**Real-Time View**:

```
┌─ Live Messages (last 10 seconds) ──────────────┐
│ 🟢 OrderCreated      → orders/partition-3   8ms│
│ 🟢 InventoryReserved → inventory/partition-1 12ms│
│ 🟡 SendEmail         → notifications     45ms (SLOW)│
│ 🟢 EmailSent         → notifications     18ms│
│ 🔴 PaymentFailed     → payments          ERROR │
└─────────────────────────────────────────────────┘

┌─ Throughput ────────────────────────────────────┐
│ orders:       ████████░░  120 msg/s             │
│ inventory:    ██████░░░░   80 msg/s             │
│ notifications: ████░░░░░░   50 msg/s             │
│ payments:     ██░░░░░░░░   30 msg/s             │
└─────────────────────────────────────────────────┘

┌─ Latency (p95) ─────────────────────────────────┐
│ orders:        25ms  [████░░░░░░]               │
│ inventory:     18ms  [███░░░░░░░]               │
│ notifications: 120ms [██████████] ALERT         │
│ payments:      32ms  [█████░░░░░]               │
└─────────────────────────────────────────────────┘
```

**Features**:
- **Real-time updates** via WebSocket
- **Alerts** for errors, slow messages, high latency
- **Click message** → Show details
- **Pause/resume** live feed
- **Filter** by topic, service, message type

#### 8. Code Lens Integration (Runtime Metrics)

**Inline Annotations in Editor** (combined with static analysis):

```csharp
// File: Orders.Service/Receptors/OrdersReceptor.cs

public class OrdersReceptor {
    // Whizbang: ← 2 dispatchers | 1,234 messages processed | Avg: 25ms | Last: 2s ago
    public async Task HandleCreateOrderAsync(CreateOrderCommand cmd, PolicyContext ctx) {
        var evt = new OrderCreatedEvent { OrderId = cmd.OrderId };

        // Whizbang: → 3 receptors | 1,234 dispatches | Last error: NullRef 2h ago
        await _dispatcher.DispatchAsync(evt, ctx);
    }
}
```

**Features**:
- **Static metrics** (2 dispatchers, 3 receptors) + **Runtime metrics** (1,234 processed)
- **Message count** at each call site
- **Average duration**
- **Last execution time**
- **Recent errors** (click to view details)
- **Refresh on demand**

#### 9. Search & Filter

**Powerful Search**:

```
Search Traces
┌─────────────────────────────────────────────────┐
│ 🔍 order-12345                                  │
└─────────────────────────────────────────────────┘

Filters:
☑ Message Type    ☐ Service         ☐ Time Range
☐ Topic           ☐ Error Status    ☐ Correlation ID

Results (3):
├─ OrderCreatedEvent (14:23:45.123)
├─ OrderUpdatedEvent (14:24:12.456)
└─ OrderCancelledEvent (14:25:33.789)
```

**Features**:
- **Full-text search** across all traces
- **Filter by** message type, service, topic, error status, time range
- **Saved searches** for common queries
- **Search history**

---

### Phase 3: Power User Features (v0.6.0)

#### 10. Distributed Breakpoints

**Set Breakpoints on Messages**:

```csharp
// File: Orders.Service/Receptors/OrdersReceptor.cs

public async Task HandleCreateOrderAsync(CreateOrderCommand cmd, PolicyContext ctx) {
    // Whizbang Breakpoint: Pause when order-12345 arrives
    var evt = new OrderCreatedEvent { OrderId = cmd.OrderId };

    await _dispatcher.DispatchAsync(evt, ctx);
}
```

**Features**:
- **Break on specific messages** (by ID, correlation, pattern)
- **Break on errors** (pause when exception occurs)
- **Break on slow operations** (pause when duration > threshold)
- **VSCode debugger integration** → Attach to running process

**Implementation**:
- Backend sends WebSocket notification when breakpoint hit
- Extension pauses live feed, highlights message
- User can inspect, step through, continue

#### 11. Performance Profiling

**Identify Bottlenecks**:

```
Performance Profile: correlation-abc-123

Total Duration: 1,234ms

Breakdown:
├─ OrderCreated → OrdersReceptor (11ms)   1%  ▏
├─ ReserveInventory → InventoryReceptor (123ms)  10% ████
├─ ⚠️ InventoryReserved → Database Write (890ms)  72% ██████████████ BOTTLENECK
├─ SendEmail → EmailService (180ms)  15% ██████
└─ EmailSent → Complete (30ms)   2% ▏

Recommendations:
⚠️ Database write is taking 72% of total time
   → Consider async writes or batching
   → [View Source: InventoryRepository.cs:89]
```

**Features**:
- **Flamegraph-style visualization**
- **Bottleneck detection** (automatic)
- **Recommendations** for optimization
- **Comparative analysis** (compare runs)

#### 12. Message Replay

**Replay Messages for Testing**:

```
Replay: OrderCreatedEvent (order-12345)

Original Execution:
├─ Timestamp: 14:23:45.123
├─ Service: Orders.Service
├─ Result: SUCCESS
└─ Duration: 234ms

Replay Options:
☑ Use same payload
☐ Modify payload [Edit JSON]
☑ Use same correlation ID
☐ Use different environment (Production → Staging)

[Replay Message] [Cancel]
```

**Features**:
- **Replay exact message** (same payload, IDs)
- **Replay with modifications** (edit payload, change IDs)
- **Replay entire correlation** (replay whole workflow)
- **Compare original vs replay** (diff results)

---

## Repository Structure & Organization

### Separate GitHub Repository

**Repository Name**: `whizbang-vscode`
**URL**: `https://github.com/whizbang-lib/whizbang-vscode`

**Rationale for Separate Repo**:
1. **Different release cadence** - Extension can evolve independently of library
2. **Different technology stack** - TypeScript (extension) vs C# (library)
3. **Different versioning** - Extension follows VSCode extension versioning (semver)
4. **Marketplace publishing** - Needs separate npm package and VS Code marketplace entry
5. **Different contributors** - TypeScript/VSCode experts may differ from .NET experts
6. **Size concerns** - Large repos slow down git operations

### Repository Ecosystem

```
whizbang-lib GitHub Organization:
├── whizbang/                    # Main .NET library
│   ├── src/Whizbang.Core/
│   ├── src/Whizbang.Generators/ # Roslyn analyzer (used by extension)
│   └── tests/
│
├── whizbang-lib.github.io/      # Documentation site
│   ├── src/assets/docs/
│   └── mcp-docs-server/
│
├── whizbang-vscode/             # VSCode extension (NEW)
│   ├── src/                     # TypeScript extension code
│   ├── analyzers/               # .NET Roslyn analyzer (references main library)
│   ├── package.json             # VSCode extension manifest
│   ├── .vscodeignore
│   └── README.md
│
└── whizbang-examples/           # Example applications (future)
    ├── OrderManagement/
    └── Inventory/
```

### Extension Structure (whizbang-vscode/)

```
whizbang-vscode/
├── src/                         # TypeScript extension code
│   ├── extension.ts             # Extension entry point
│   ├── staticAnalysis/          # Development-time navigation
│   ├── runtime/                 # Runtime debugging features
│   ├── views/                   # Tree views, webviews
│   └── commands/                # Command implementations
│
├── analyzers/                   # .NET Roslyn analyzer
│   ├── Whizbang.VSCode.Analyzer/
│   │   ├── MessageRegistryGenerator.cs
│   │   └── Whizbang.VSCode.Analyzer.csproj
│   └── build/                   # Built analyzer DLLs
│
├── media/                       # Icons, images
├── syntaxes/                    # Language grammars (if needed)
├── snippets/                    # Code snippets
├── package.json                 # VSCode extension manifest
├── tsconfig.json                # TypeScript configuration
├── .vscodeignore               # Files to exclude from extension package
├── .gitignore
├── README.md                    # Extension documentation
├── CHANGELOG.md                # Extension changelog
└── LICENSE                      # MIT (same as main library)
```

### Dependency Management

**Extension depends on Library**:

```json
// package.json dependencies
{
  "dependencies": {
    "typescript": "^5.0.0",
    "vscode": "^1.80.0",
    // NO dependency on whizbang library itself
  },
  "devDependencies": {
    "@types/node": "^20.0.0",
    "@types/vscode": "^1.80.0"
  }
}
```

**Roslyn Analyzer References Library**:

```xml
<!-- analyzers/Whizbang.VSCode.Analyzer/Whizbang.VSCode.Analyzer.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <!-- Reference Whizbang.Core via NuGet (NOT project reference) -->
    <PackageReference Include="Whizbang.Core" Version="0.2.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.7.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.Common" Version="4.7.0" />
  </ItemGroup>
</Project>
```

**Key Point**: The analyzer references the **published NuGet package**, not a local project reference. This ensures the extension works with any version of Whizbang the user has installed.

### Versioning Strategy

**Extension Versioning** (Semantic Versioning):
- **v1.0.0** - MVP (Development-time navigation for v0.2.0+ library)
- **v1.1.0** - Runtime debugging (requires v0.3.0+ library for persistent trace store)
- **v2.0.0** - Breaking changes (e.g., new message registry format)

**Compatibility Matrix**:

| Extension Version | Min Library Version | Max Library Version | Features |
|-------------------|---------------------|---------------------|----------|
| 1.0.0 | 0.2.0 | 1.x.x | Development-time navigation |
| 1.1.0 | 0.3.0 | 1.x.x | + Runtime debugging |
| 1.2.0 | 0.3.0 | 1.x.x | + Live monitoring |
| 2.0.0 | 1.0.0 | 2.x.x | Breaking: New analyzer format |

**Documented in**:
- Extension README.md
- VS Code marketplace listing
- GitHub releases

### Publishing Pipeline

**VS Code Marketplace**:

```yaml
# .github/workflows/publish.yml
name: Publish Extension

on:
  release:
    types: [published]

jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup Node.js
        uses: actions/setup-node@v3
        with:
          node-version: '18'

      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '9.0.x'

      - name: Build Analyzer
        run: |
          cd analyzers/Whizbang.VSCode.Analyzer
          dotnet build -c Release
          cp bin/Release/netstandard2.0/*.dll ../../build/

      - name: Install dependencies
        run: npm ci

      - name: Build Extension
        run: npm run compile

      - name: Package Extension
        run: npx vsce package

      - name: Publish to Marketplace
        run: npx vsce publish -p ${{ secrets.VSCE_PAT }}
        env:
          VSCE_PAT: ${{ secrets.VSCE_PAT }}
```

**GitHub Releases**:
- Tag format: `v1.0.0`, `v1.1.0`, etc.
- Attach `.vsix` file to release
- Include changelog in release notes

### Development Workflow

**For Extension Contributors**:

```bash
# Clone extension repo
git clone https://github.com/whizbang-lib/whizbang-vscode.git
cd whizbang-vscode

# Install dependencies
npm install

# Build Roslyn analyzer (requires .NET SDK)
cd analyzers/Whizbang.VSCode.Analyzer
dotnet build
cd ../..

# Open in VSCode
code .

# Press F5 to launch Extension Development Host
# Make changes, test, repeat
```

**For Testing with Local Library Changes**:

```bash
# Terminal 1: Build library locally
cd ../whizbang
dotnet pack -c Release -o /tmp/whizbang-local

# Terminal 2: Update analyzer to use local package
cd whizbang-vscode/analyzers/Whizbang.VSCode.Analyzer
# Edit .csproj to point to /tmp/whizbang-local
dotnet build

# Test extension with local analyzer changes
```

### License & Ownership

- **License**: MIT (same as main library)
- **Copyright**: Whizbang Contributors
- **Organization**: whizbang-lib GitHub organization
- **Maintainers**: Same team as main library

### Documentation Cross-References

**Extension README links to**:
- Main library: `https://github.com/whizbang-lib/whizbang`
- Documentation site: `https://whizbang-lib.github.io`
- Getting started guide: `https://whizbang-lib.github.io/docs/v0.2.0/guides/getting-started`

**Library README links to**:
- VSCode extension: `https://github.com/whizbang-lib/whizbang-vscode`
- Marketplace listing: `https://marketplace.visualstudio.com/items?itemName=whizbang-lib.whizbang`

**Documentation site includes**:
- Extension page: `docs/v0.4.0/tooling/vscode-extension.md`
- Installation instructions
- Feature showcase with screenshots
- Troubleshooting guide

---

## Technical Architecture

### Extension Structure

```
whizbang-vscode/
├── src/
│   ├── extension.ts              # Entry point
│   ├── staticAnalysis/           # Development-time features
│   │   ├── messageRegistry.ts    # Message-to-dispatcher/receptor mapping
│   │   ├── codeLensProvider.ts   # GitLens-style annotations
│   │   ├── hoverProvider.ts      # Hover tooltips
│   │   ├── navigationCommands.ts # Go to Dispatcher/Receptor
│   │   └── flowDiagramGenerator.ts # Static flow diagrams
│   ├── views/
│   │   ├── traceTreeView.ts      # Tree view provider
│   │   ├── messageDetailsPanel.ts # Webview panel
│   │   ├── flowDiagramView.ts    # Graph visualization
│   │   └── liveMonitorView.ts    # Real-time dashboard
│   ├── providers/
│   │   ├── codeLensProvider.ts   # Code lens integration (runtime)
│   │   ├── hoverProvider.ts      # Hover tooltips (runtime)
│   │   └── completionProvider.ts # Auto-complete
│   ├── commands/
│   │   ├── jumpToSource.ts       # Jump to code
│   │   ├── showDetails.ts        # Show message details
│   │   ├── showFlowDiagram.ts    # Show graph
│   │   └── replayMessage.ts      # Replay functionality
│   ├── services/
│   │   ├── traceService.ts       # Query TraceStore API
│   │   ├── webSocketService.ts   # Real-time updates
│   │   └── cacheService.ts       # Local caching
│   └── models/
│       ├── messageEnvelope.ts    # TypeScript models
│       ├── messageHop.ts
│       └── policyDecision.ts
│
├── analyzers/
│   └── Whizbang.Analyzers/       # Roslyn analyzer (C# project)
│       ├── MessageAnalyzer.cs    # Find dispatchers/receptors
│       ├── RegistryGenerator.cs  # Generate message registry
│       └── Diagnostics.cs        # IDE warnings/suggestions
│
├── media/
│   ├── styles/                   # Webview CSS
│   ├── scripts/                  # Webview JS
│   └── icons/                    # Extension icons
│
├── package.json                  # Extension manifest
├── tsconfig.json
└── README.md
```

### Message Registry Generation

**Build-time tool** (dotnet tool):

```bash
# Install tool
dotnet tool install -g Whizbang.MessageAnalyzer

# Run analysis
dotnet whizbang-analyze ./MyProject.sln --output .whizbang/message-registry.json
```

**MSBuild Integration**:

```xml
<!-- Directory.Build.targets -->
<Target Name="GenerateWhizbangRegistry" AfterTargets="Build">
  <Exec Command="dotnet whizbang-analyze $(SolutionDir) --output $(SolutionDir).whizbang/message-registry.json"
        Condition="Exists('$(SolutionDir).whizbang')" />
</Target>
```

**Registry Format**:

```json
{
  "version": "1.0",
  "solution": "MyProject.sln",
  "messages": [
    {
      "type": "CreateOrderCommand",
      "namespace": "Orders.Contracts.Commands",
      "assembly": "Orders.Contracts",
      "file": "Commands/CreateOrderCommand.cs",
      "line": 12,
      "dispatchers": [
        {
          "class": "OrderDispatcher",
          "method": "CreateOrderAsync",
          "project": "Orders.Service",
          "file": "Dispatchers/OrderDispatcher.cs",
          "line": 45,
          "context": "Direct dispatch from API endpoint"
        },
        {
          "class": "OrderSaga",
          "method": "RetryCreateOrder",
          "project": "Orders.Service",
          "file": "Sagas/OrderSaga.cs",
          "line": 189,
          "context": "Retry logic for failed orders"
        }
      ],
      "receptors": [
        {
          "class": "OrdersReceptor",
          "method": "HandleCreateOrderAsync",
          "project": "Orders.Service",
          "file": "Receptors/OrdersReceptor.cs",
          "line": 23,
          "parameters": "CreateOrderCommand cmd, PolicyContext ctx"
        },
        {
          "class": "AuditReceptor",
          "method": "HandleAsync",
          "project": "Orders.Service",
          "file": "Receptors/AuditReceptor.cs",
          "line": 67,
          "parameters": "CreateOrderCommand cmd, PolicyContext ctx"
        },
        {
          "class": "AnalyticsReceptor",
          "method": "TrackOrderCreated",
          "project": "Analytics.Service",
          "file": "Receptors/AnalyticsReceptor.cs",
          "line": 89,
          "parameters": "CreateOrderCommand cmd"
        }
      ],
      "policies": [
        {
          "name": "Order Processing Policy",
          "file": "Configuration/OrderPolicies.cs",
          "line": 42,
          "predicate": "ctx => ctx.MatchesAggregate<Order>()"
        }
      ]
    }
  ]
}
```

### Backend API (ASP.NET Core)

```
Whizbang.TraceAPI/
├── Controllers/
│   ├── TracesController.cs       # REST API for traces
│   ├── LiveFeedController.cs     # WebSocket endpoint
│   └── ReplayController.cs       # Message replay
│
├── Services/
│   ├── TraceQueryService.cs      # Query ITraceStore
│   ├── LiveFeedService.cs        # Real-time pub/sub
│   └── ReplayService.cs          # Message replay logic
│
├── Hubs/
│   └── TraceFeedHub.cs           # SignalR hub
│
└── Program.cs
```

**API Endpoints**:
```
GET  /api/traces/{messageId}              # Get single trace
GET  /api/traces/correlation/{correlationId} # Get correlation
GET  /api/traces/causal/{messageId}       # Get causal chain
GET  /api/traces/timerange?from=&to=      # Time range query
POST /api/traces/search                   # Advanced search
WS   /api/live                            # WebSocket live feed
POST /api/replay/{messageId}              # Replay message
```

---

## Development Phases

### Phase 1: Development-Time Navigation (4 weeks)

**Goal**: GitLens-style code annotations for message flow.

**Features**:
- [x] Message type annotations (dispatchers/receptors count)
- [x] Dispatcher annotations (which receptors handle this)
- [x] Receptor annotations (who dispatches to this)
- [x] Message registry generation (Roslyn analyzer)
- [x] "Go to Dispatcher/Receptor" commands
- [x] "Find Message Usages" command
- [x] Static flow diagram generator

**Deliverables**:
1. VSCode extension with static analysis
2. Roslyn analyzer (dotnet tool)
3. MSBuild integration
4. Documentation

**Timeline**:
- Week 1: Roslyn analyzer, message registry generation
- Week 2: Code lens provider, hover provider
- Week 3: Navigation commands, flow diagram
- Week 4: Polish, testing, documentation

### Phase 2: Runtime Debugging (6 weeks)

**Goal**: Basic extension with core runtime features.

**Features**:
- [x] Trace tree view (live messages, recent correlations)
- [x] Message details panel
- [x] Jump to source (runtime)
- [x] Basic REST API (query traces)
- [x] WebSocket live feed
- [x] Combined code lens (static + runtime metrics)

**Deliverables**:
1. VSCode extension (installable .vsix)
2. Backend API (ASP.NET Core)
3. Basic documentation
4. Demo video

**Timeline**:
- Week 5-6: Extension scaffolding, tree view
- Week 7-8: Backend API, TraceStore integration
- Week 9: Message details panel, jump to source
- Week 10: WebSocket live feed, polish

### Phase 3: Enhanced Features (4 weeks)

**Goal**: Add visualization and policy debugging.

**Features**:
- [x] Visual flow diagram
- [x] Policy decision trail view
- [x] Search & filter
- [x] Combined code lens (static + runtime)

**Timeline**:
- Week 11-12: Flow diagram (D3.js integration)
- Week 13: Policy decision trail
- Week 14: Search, combined code lens

### Phase 4: Advanced Features (6 weeks)

**Goal**: Power user features.

**Features**:
- [x] Time-travel debugging
- [x] Live monitoring dashboard
- [x] Performance profiling
- [x] Message replay

**Timeline**:
- Week 15-16: Time-travel debugging
- Week 17-18: Live monitoring dashboard
- Week 19-20: Performance profiling, message replay

### Phase 5: Documentation Site Updates (2 weeks)

**Goal**: Comprehensive documentation on whizbang-lib.github.io for the VSCode extension.

**Location**: `whizbang-lib.github.io/src/assets/docs/v0.4.0/tooling/`

**Documentation Files to Create**:

1. **vscode-extension.md** - Extension overview
   - What is the Whizbang VSCode extension?
   - Key features (development-time + runtime)
   - Benefits and use cases
   - Screenshots and GIFs
   - Link to VS Code Marketplace
   - Link to GitHub repo (whizbang-vscode)

2. **installation.md** - Installation and setup
   - Prerequisites (.NET SDK, Whizbang library version)
   - Installation from VS Code Marketplace
   - Installation from .vsix file
   - Configuration options
   - Verifying installation (message registry generation)
   - Troubleshooting installation issues

3. **development-navigation.md** - Development-time features
   - Message type annotations (dispatchers, receptors, perspectives)
   - Code lens providers and hover tooltips
   - "Go to Dispatcher/Receptor/Perspective" commands
   - Cross-service navigation
   - Static flow diagrams
   - Message registry and Roslyn analyzer
   - Code examples with screenshots

4. **runtime-debugging.md** - Runtime features (requires v0.3.0+)
   - Prerequisites (persistent trace store)
   - Jump to source from traces
   - Visual message flow diagrams
   - Time-travel debugging
   - Live monitoring dashboard
   - Policy decision trail debugging
   - Performance profiling
   - Message replay
   - Code examples with screenshots

5. **troubleshooting.md** - Common issues and solutions
   - Extension not detecting messages
   - Message registry not updating
   - Roslyn analyzer build issues
   - Cross-service navigation not working
   - Runtime features not connecting
   - Performance issues
   - Common error messages

**Additional Content**:
- Add extension card to documentation homepage
- Add "Tooling" section to main navigation
- Add screenshots/GIFs to showcase features
- Add video tutorial (5-10 minutes)
- Add FAQ section

**Timeline**:
- Week 21: Create overview, installation, and development-navigation docs
- Week 22: Create runtime-debugging and troubleshooting docs, add screenshots/GIFs

**Deliverables**:
1. 5 comprehensive documentation pages
2. Screenshots/GIFs of all major features
3. Video tutorial (optional but recommended)
4. Updated site navigation
5. GitHub issue templates for extension support

**Success Criteria**:
- [ ] All 5 documentation files created
- [ ] Each page has at least 3 code examples
- [ ] Each major feature has a screenshot or GIF
- [ ] Mobile-friendly formatting
- [ ] SEO-optimized (meta descriptions, structured data)
- [ ] Search-indexed (all content searchable)
- [ ] Cross-references to library docs
- [ ] Links to extension repo and marketplace

---

## Success Metrics

### Adoption Metrics

**Development-Time Features**:
- **Installs** (target: 2,000 in first 3 months)
- **Active users** (target: 80% monthly active)
- **Daily usage** (target: 30 min/day per developer)

**Runtime Features**:
- **Installs** (target: 1,000 in first 3 months)
- **Active users** (target: 70% monthly active)
- **Daily usage** (target: 10 min/day per user)

### Feature Usage

**Development-Time**:
- **Code lens annotations** - Most used (target: 90% of users)
- **Go to Dispatcher/Receptor** - High value (target: 75% of users)
- **Flow diagram** - Power user (target: 40% of users)

**Runtime**:
- **Jump to source** - Most used feature (target: 80% of users)
- **Flow diagram** - High value feature (target: 50% of users)
- **Live monitoring** - Power user feature (target: 20% of users)

### Performance

- **Code lens update** < 100ms
- **Go to definition** < 50ms
- **Flow diagram render** < 500ms
- **Trace load time** < 500ms
- **Jump to source** < 200ms
- **Live feed latency** < 100ms

### User Satisfaction

- **NPS Score** > 50 (promoters)
- **GitHub Stars** > 1,000 in first 6 months
- **Issues** < 20 open bugs at any time

---

## Competitive Advantage

**vs. Other Tools**:

| Feature | Jaeger | Zipkin | App Insights | Seq | **Whizbang** |
|---------|--------|--------|--------------|-----|--------------|
| **Development-Time Navigation** | ❌ | ❌ | ❌ | ❌ | ✅ **GitLens-style** |
| **Go to Dispatcher/Receptor** | ❌ | ❌ | ❌ | ❌ | ✅ **Cross-service** |
| **Static Flow Diagrams** | ❌ | ❌ | ❌ | ❌ | ✅ **Code analysis** |
| Visual Traces | ✅ | ✅ | ✅ | ❌ | ✅ |
| Jump to Source | ❌ | ❌ | ❌ | ❌ | ✅ **Exact line** |
| IDE Integration | ❌ | ❌ | Partial | ❌ | ✅ **Native** |
| Message-Centric | ❌ | ❌ | ❌ | Partial | ✅ |
| Policy Debugging | ❌ | ❌ | ❌ | ❌ | ✅ |
| Time-Travel | ❌ | ❌ | ❌ | ❌ | ✅ |

**Unique Selling Points**:
1. **Development-time navigation** - No other tool helps you navigate message flows while writing code
2. **Cross-service navigation** - Jump between dispatchers and receptors across projects
3. **Static + runtime integration** - Code lens shows both static analysis and runtime metrics
4. **Source-level debugging** - Jump to exact line from production traces
5. **Message-first design** - Built for event-driven architectures

---

## Next Steps

1. **Review this plan** with stakeholders
2. **Prototype Phase 1** (Development-Time Navigation)
   - 2-week spike
   - Roslyn analyzer
   - Code lens provider
   - "Go to Dispatcher" command
3. **Validate with developers** (10-15 developers)
4. **Refine plan** based on feedback
5. **Begin full development** (22 weeks total):
   - Phase 1: Development-Time Navigation (weeks 1-4)
   - Phase 2: Runtime Debugging (weeks 5-10)
   - Phase 3: Enhanced Features (weeks 11-14)
   - Phase 4: Advanced Features (weeks 15-20)
   - Phase 5: Documentation Site Updates (weeks 21-22)

---

## Conclusion

The Whizbang VSCode Extension transforms both **development** and **debugging** of event-driven systems:

**Development Time**:
- **GitLens-style navigation** - See who dispatches/receives messages
- **Cross-service navigation** - Jump between microservices seamlessly
- **Static flow diagrams** - Understand message flows before running code
- **Integrated metrics** - See runtime stats while writing code

**Runtime**:
- **Click to jump to source** - No more log archaeology
- **Visual message flows** - Understand complex workflows
- **Time-travel debugging** - See what happened and why
- **Live monitoring** - Watch your system in real-time

This extension makes Whizbang **dramatically easier to use** and becomes a **compelling reason** to choose Whizbang over alternatives.

---

## Changelog

### 2025-11-02 - Perspective Support, Repository Organization & Documentation Plan

**Added**:
- **Feature 4: Perspective Annotations** - GitLens-style annotations for Perspectives (read model projections)
  - Class-level annotations showing events consumed and read model updated
  - Method-level annotations showing event producers
  - Hover tooltips with perspective details
  - Roslyn analyzer discovery strategy for `IPerspectiveOf<TEvent>`
- **Repository Structure & Organization** section
  - Rationale for separate `whizbang-vscode` GitHub repository
  - Repository ecosystem diagram
  - Dependency management strategy
  - Versioning and compatibility matrix
  - Publishing pipeline (GitHub Actions, VS Code Marketplace)
  - Development workflow for contributors
  - License and ownership details
  - Documentation cross-references
- **Phase 5: Documentation Site Updates** (2 weeks)
  - 5 comprehensive documentation files for whizbang-lib.github.io
  - vscode-extension.md, installation.md, development-navigation.md, runtime-debugging.md, troubleshooting.md
  - Screenshots/GIFs, video tutorial, updated navigation
  - Success criteria with 8 checkpoints

**Updated**:
- **Feature 1: Message Type Annotations** - Now shows perspectives count on events (📊 4 perspectives)
- **Message Registry JSON** - Added `perspectives[]` array with event-specific data
- **Feature 5: Cross-Service Flow Visualization** - Flow diagram now shows perspectives as read models
- **Development Phases** - Expanded from 4 to 5 phases (added documentation phase)
- Renumbered features 4-7 to 5-8 to accommodate new Perspective feature

**Key Additions**:
1. Perspectives shown alongside Dispatchers and Receptors in code lens
2. Click perspectives to navigate to read model implementations
3. Support for multi-event perspectives (single class consuming multiple events)
4. Separate repository strategy with clear versioning and publishing plan
5. Complete documentation plan integrated into development timeline (weeks 21-22)
