# Core Dispatch Flow

The Dispatcher is the central routing hub. It provides three distinct dispatch patterns, each with different semantics for how messages reach their receptors and what guarantees are provided.

```mermaid
flowchart TB
    subgraph Entry["Dispatch Entry Points"]
        Send["<b>SendAsync</b><br/>Command dispatch<br/>Returns IDeliveryReceipt"]
        Invoke["<b>LocalInvokeAsync</b><br/>In-process RPC<br/>Returns TResult"]
        Publish["<b>PublishAsync</b><br/>Event broadcast<br/>Fire-and-forget"]
    end

    subgraph Envelope["Envelope Creation"]
        CreateEnv["Create MessageEnvelope&lt;T&gt;<br/>━━━━━━━━━━━━━━━━━━━━<br/>• Generate MessageId (UUIDv7)<br/>• Capture CallerInfo<br/>• Attach ScopeContext<br/>• Register in EnvelopeRegistry"]
    end

    subgraph Routing["Dispatch Mode Resolution"]
        direction TB
        CheckRoute{"Message wrapped<br/>in Routed&lt;T&gt;?"}
        CheckAttr{"Has [DefaultRouting]<br/>attribute?"}
        DefaultMode["Default: Outbox"]

        CheckRoute -->|Yes| UseRouted["Use Routed.Mode"]
        CheckRoute -->|No| CheckAttr
        CheckAttr -->|Yes| UseAttr["Use attribute mode"]
        CheckAttr -->|No| DefaultMode
    end

    subgraph Modes["DispatchModes (Flags)"]
        direction TB
        Local["<b>Local</b><br/>LocalDispatch | EventStore<br/>━━━━━━━━━━━━━━━━━━━━<br/>Dispatch to in-process receptors<br/>AND persist to event store"]
        LocalNP["<b>LocalNoPersist</b><br/>LocalDispatch only<br/>━━━━━━━━━━━━━━━━━━━━<br/>Ephemeral in-process dispatch<br/>No event store write"]
        Outbox["<b>Outbox</b><br/>━━━━━━━━━━━━━━━━━━━━<br/>Write to outbox table<br/>Transport delivers to other services"]
        Both["<b>Both</b><br/>LocalDispatch | Outbox<br/>━━━━━━━━━━━━━━━━━━━━<br/>Local dispatch + outbox write"]
        ESOnly["<b>EventStoreOnly</b><br/>EventStore only<br/>━━━━━━━━━━━━━━━━━━━━<br/>Persist without local dispatch"]
        None["<b>None</b><br/>━━━━━━━━━━━━━━━━━━━━<br/>Discriminated union marker<br/>No routing"]
    end

    subgraph Targets["Processing Targets"]
        Receptors["Receptor Registry<br/>(source-generated lookup)"]
        EventStore["Event Store<br/>(append-only)"]
        OutboxTable["Outbox Table<br/>(at-least-once delivery)"]
    end

    Send --> CreateEnv
    Invoke --> CreateEnv
    Publish --> CreateEnv
    CreateEnv --> Routing

    UseRouted --> Modes
    UseAttr --> Modes
    DefaultMode --> Modes

    Local --> Receptors
    Local --> EventStore
    LocalNP --> Receptors
    Outbox --> OutboxTable
    Both --> Receptors
    Both --> OutboxTable
    ESOnly --> EventStore

    style Send fill:#e8744f,stroke:#b85a3d,color:#fff
    style Invoke fill:#4a90d9,stroke:#2c5f8a,color:#fff
    style Publish fill:#6ab04c,stroke:#4a8035,color:#fff
    style CreateEnv fill:#f5f5f5,stroke:#999
    style Receptors fill:#f0c040,stroke:#c4a030,color:#333
    style EventStore fill:#45b7d1,stroke:#3590a8,color:#fff
    style OutboxTable fill:#e056a0,stroke:#b04080,color:#fff
```

## Dispatch Patterns

### SendAsync — Command Delivery
```
Caller → SendAsync(command) → Envelope → Route → Receptors/EventStore/Outbox
         Returns: IDeliveryReceipt (confirmation of dispatch, not processing)
```

### LocalInvokeAsync — In-Process RPC
```
Caller → LocalInvokeAsync<TMsg, TResult>(msg) → Envelope → Receptor → TResult
         Returns: Business result directly (zero-allocation ValueTask)
         Variant: LocalInvokeAndSyncAsync — waits for all perspectives to catch up
```

### PublishAsync — Event Broadcast
```
Caller → PublishAsync(event) → Envelope → All registered receptors
         Returns: IDeliveryReceipt (fire-and-forget semantics)
```

## Route Factory

```csharp
// Explicit routing wrappers
Route.Local(event)          // In-process + event store
Route.LocalNoPersist(event) // In-process only (ephemeral)
Route.Outbox(event)         // Cross-service delivery
Route.Both(event)           // In-process + outbox
Route.EventStoreOnly(event) // Persistence only
Route.None()                // Discriminated union marker
```

## Event Cascading

When a receptor returns messages, the `IEventCascader` determines routing:

1. Check if message is wrapped in `Routed<T>` → use explicit mode
2. Check message's `[DefaultRouting]` attribute → use attribute mode
3. Check receptor's `[DefaultRouting]` attribute → use receptor default
4. Fall back to system default → **Outbox**
