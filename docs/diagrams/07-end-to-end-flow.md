# End-to-End Event Processing Flow

The complete journey of a command through the Whizbang system — from API request to updated read model available for queries.

```mermaid
sequenceDiagram
    autonumber
    participant API as API / Client
    participant Disp as Dispatcher
    participant Reg as Receptor Registry
    participant CmdR as Command Receptor
    participant Casc as Event Cascader
    participant ES as Event Store
    participant OT as Outbox Table
    participant OW as Outbox Worker
    participant Xport as Transport
    participant TC as Transport Consumer
    participant IT as Inbox Table
    participant PR as Perspective Runner
    participant PS as Perspective Store
    participant QR as Query Receptor

    Note over API,QR: ━━ PHASE 1: Command Dispatch ━━

    API->>Disp: SendAsync(PlaceOrderCommand)
    activate Disp
    Disp->>Disp: Create MessageEnvelope, MessageId (UUIDv7), Attach ScopeContext
    Disp->>Reg: GetReceptorsFor(PlaceOrderCommand, ImmediateAsync)
    Reg-->>Disp: OrderCommandReceptor
    Disp->>CmdR: HandleAsync(command)
    activate CmdR

    Note over CmdR: Validate business rules, apply domain logic

    CmdR-->>Disp: OrderPlacedEvent
    deactivate CmdR

    Note over API,QR: ━━ PHASE 2: Event Cascading & Persistence ━━

    Disp->>Casc: CascadeFromResultAsync(OrderPlacedEvent)
    activate Casc
    Casc->>Casc: Resolve DispatchMode (Routed wrapper, DefaultRouting attr, or default Outbox)

    alt Mode includes EventStore
        Casc->>ES: AppendAsync(streamId, envelope)
        Note over ES: Append-only write to stream, Sequence N+1
    end

    alt Mode includes Outbox
        Casc->>OT: Write OutboxRecord
        Note over OT: Transactional write with event store append
    end

    alt Mode includes LocalDispatch
        Casc->>Reg: GetReceptorsFor(OrderPlacedEvent, ImmediateAsync)
        Reg-->>Casc: local event receptors
        Casc->>Casc: Invoke local receptors
    end

    deactivate Casc
    Disp-->>API: IDeliveryReceipt
    deactivate Disp

    Note over API,QR: ━━ PHASE 3: Outbox Processing ━━

    OW->>OT: Claim unpublished records (lease-based, partition-aware)
    activate OW
    Note over OW: PreOutbox lifecycle stages
    OW->>Xport: Publish OrderPlacedEvent
    Note over OW: PostOutbox lifecycle stages
    OW->>OT: Mark published
    deactivate OW

    Note over API,QR: ━━ PHASE 4: Cross-Service Delivery ━━

    Xport->>TC: Deliver OrderPlacedEvent
    activate TC
    TC->>IT: Check inbox deduplication (MessageId + HandlerName)
    Note over TC: PreInbox lifecycle stages
    TC->>IT: Write InboxRecord

    TC->>Reg: GetReceptorsFor(OrderPlacedEvent, stage)
    Reg-->>TC: InventoryReceptor, NotificationReceptor

    loop Each receptor
        TC->>CmdR: HandleAsync(OrderPlacedEvent)
        Note over CmdR: Process event (side effects, cascades)
    end

    Note over TC: PostInbox lifecycle stages
    TC->>IT: Mark processed
    TC-->>Xport: Ack
    deactivate TC

    Note over API,QR: ━━ PHASE 5: Perspective Projection ━━

    PR->>ES: ReadAsync(streamId, lastCheckpoint)
    activate PR
    ES-->>PR: OrderPlacedEvent, ...

    Note over PR: PrePerspective lifecycle stages

    loop Each event in stream
        PR->>PR: Perspective.Apply(event) — pure function
    end

    PR->>PS: UpsertAsync(streamId, model)
    Note over PS: Read model updated (EF Core / Dapper / etc.)

    Note over PR: PostPerspective lifecycle stages
    Note over PR: PostAllPerspectives (after WhenAll perspectives)
    Note over PR: PostLifecycle (final)
    deactivate PR

    Note over API,QR: ━━ PHASE 6: Query ━━

    API->>Disp: LocalInvokeAsync(GetOrderQuery)
    Disp->>QR: HandleAsync(query)
    activate QR
    QR->>PS: GetByStreamIdAsync(orderId)
    PS-->>QR: OrderSummaryModel
    QR-->>Disp: OrderSummaryResult
    deactivate QR
    Disp-->>API: OrderSummaryResult
```

## Simplified Overview

```mermaid
flowchart LR
    subgraph Write["Write Path"]
        C["Command"] --> R["Receptor"] --> E["Event(s)"]
    end

    subgraph Persist["Persistence"]
        E --> ES["Event Store"]
        E --> OB["Outbox"]
    end

    subgraph Deliver["Delivery"]
        OB --> T["Transport"] --> IB["Inbox"]
    end

    subgraph Project["Projection"]
        ES --> PW["Perspective<br/>Worker"]
        IB -.-> PW
        PW --> PS["Read Model<br/>Store"]
    end

    subgraph Read["Read Path"]
        Q["Query"] --> QR["Receptor"] --> PS
        QR --> Res["Result"]
    end

    style Write fill:#ffebee,stroke:#c62828
    style Persist fill:#e3f2fd,stroke:#1565c0
    style Deliver fill:#fff3e0,stroke:#ef6c00
    style Project fill:#e8f5e9,stroke:#2e7d32
    style Read fill:#f3e5f5,stroke:#7b1fa2
```

## Processing Guarantees Summary

| Guarantee | Mechanism |
|-----------|-----------|
| **Commands processed exactly once** | Receptor invocation is synchronous within dispatch |
| **Events persisted durably** | Transactional append to event store |
| **Cross-service at-least-once** | Outbox pattern with transport acknowledgment |
| **Consumer exactly-once** | Inbox deduplication by MessageId + HandlerName |
| **Projections eventually consistent** | Perspective workers poll event store |
| **Ordering within stream** | Stream-aware Phase 7 coordination |
| **Full traceability** | Envelope hops with causation/correlation chains |
| **Replay-safe** | ProcessingMode flag; `[FireDuringReplay]` opt-in |

## Error Handling Flow

```mermaid
flowchart TB
    Process["Process Message"] --> Success{"Success?"}
    Success -->|Yes| Ack["Ack + Update Checkpoint"]
    Success -->|No| Retry{"Attempts < Max?"}
    Retry -->|Yes| Backoff["Exponential Backoff<br/>Increment Attempts"]
    Retry -->|No| DLQ["Dead Letter<br/>Set FailureReason<br/>Update StatusFlags"]
    Backoff --> Process

    style Success fill:#fff9c4,stroke:#f9a825
    style Ack fill:#c8e6c9,stroke:#388e3c
    style DLQ fill:#ffcdd2,stroke:#c62828
```
