# Event Sourcing & Perspectives

Events are persisted to an append-only event store. **Perspectives** are Whizbang's read model projections — pure functions that transform event streams into queryable state. They support snapshots, replay, and time-travel debugging.

```mermaid
flowchart TB
    subgraph Write["Write Side"]
        Cmd["Command"]
        Receptor["Receptor<br/>━━━━━━━━━━<br/>Validates rules<br/>Emits events"]
        Events["Event(s)"]

        Cmd --> Receptor --> Events
    end

    subgraph Store["Event Store (Append-Only)"]
        direction TB
        Stream["Stream (by AggregateId)"]
        E1["Event 1<br/>seq: 1"]
        E2["Event 2<br/>seq: 2"]
        E3["Event 3<br/>seq: 3"]
        E4["Event 4<br/>seq: 4"]
        EN["Event N<br/>seq: N"]

        Stream --- E1 --- E2 --- E3 --- E4 -.- EN
    end

    subgraph Perspectives["Perspective Workers"]
        direction TB

        subgraph P1["Perspective A (e.g., OrderSummary)"]
            direction LR
            Snap1["Snapshot<br/>(optional)<br/>at seq: 2"]
            Apply1["Apply events<br/>seq 3 → N"]
            Model1["Read Model A"]
            Snap1 --> Apply1 --> Model1
        end

        subgraph P2["Perspective B (e.g., InventoryView)"]
            direction LR
            Snap2["Snapshot<br/>(optional)"]
            Apply2["Apply events"]
            Model2["Read Model B"]
            Snap2 --> Apply2 --> Model2
        end

        subgraph P3["Perspective C (e.g., AuditLog)"]
            direction LR
            Apply3["Apply events<br/>(no snapshot)"]
            Model3["Read Model C"]
            Apply3 --> Model3
        end
    end

    subgraph Read["Read Side (Queries)"]
        Query["Query"]
        QReceptor["Query Receptor"]
        PStore["Perspective Store<br/>━━━━━━━━━━━━━━<br/>GetByStreamIdAsync<br/>GetByPartitionKeyAsync"]
        Result["Query Result"]

        Query --> QReceptor --> PStore --> Result
    end

    Events --> Store
    Store --> P1
    Store --> P2
    Store --> P3
    Model1 -.-> PStore
    Model2 -.-> PStore
    Model3 -.-> PStore

    style Write fill:#ffebee,stroke:#c62828
    style Store fill:#e3f2fd,stroke:#1565c0
    style Perspectives fill:#e8f5e9,stroke:#2e7d32
    style Read fill:#f3e5f5,stroke:#7b1fa2
    style P1 fill:#c8e6c9,stroke:#388e3c
    style P2 fill:#c8e6c9,stroke:#388e3c
    style P3 fill:#c8e6c9,stroke:#388e3c
```

## Perspective Runner Lifecycle

```mermaid
flowchart LR
    subgraph Normal["RunAsync (Normal Processing)"]
        direction TB
        Load["Load last checkpoint<br/>(lastProcessedEventId)"]
        ReadES["Read events from<br/>event store after checkpoint"]
        ApplyEach["For each event:<br/>Perspective.Apply(event)"]
        Save["Save model to<br/>PerspectiveStore"]
        Checkpoint["Update checkpoint<br/>(PerspectiveCursorCompletion)"]

        Load --> ReadES --> ApplyEach --> Save --> Checkpoint
    end

    subgraph Rewind["RewindAndRunAsync (Late Event)"]
        direction TB
        FindSnap["Find nearest snapshot<br/>before triggering event"]
        Restore["Restore model<br/>from snapshot"]
        Replay["Replay events<br/>from snapshot → current"]
        SaveR["Save updated model"]

        FindSnap --> Restore --> Replay --> SaveR
    end

    subgraph Bootstrap["BootstrapSnapshotAsync"]
        direction TB
        BuildFull["Build model from<br/>all events (seq 0 → N)"]
        CreateSnap["Create snapshot at<br/>current position"]

        BuildFull --> CreateSnap
    end
```

## Snapshot Store Operations

```mermaid
flowchart TB
    subgraph SnapshotStore["IPerspectiveSnapshotStore"]
        Create["CreateSnapshotAsync<br/>━━━━━━━━━━━━━━━━━━<br/>Save model state at event ID"]
        GetLatest["GetLatestSnapshotAsync<br/>━━━━━━━━━━━━━━━━━━<br/>Most recent snapshot"]
        GetBefore["GetLatestSnapshotBeforeAsync<br/>━━━━━━━━━━━━━━━━━━<br/>Nearest snapshot before event<br/>(for rewind)"]
        HasAny["HasAnySnapshotAsync<br/>━━━━━━━━━━━━━━━━━━<br/>Check if snapshots exist"]
        Prune["PruneOldSnapshotsAsync<br/>━━━━━━━━━━━━━━━━━━<br/>Keep N most recent"]
        Delete["DeleteAllSnapshotsAsync<br/>━━━━━━━━━━━━━━━━━━<br/>Full rebuild required"]
    end

    Rewind["Late-arriving event<br/>triggers rewind"] --> GetBefore
    Optimization["Periodic maintenance"] --> Prune
    Rebuild["Full rebuild requested"] --> Delete

    style SnapshotStore fill:#e3f2fd,stroke:#1565c0
```

## Perspective Type Declaration

Perspectives declare their model and event types via generic marker interfaces. The source generator scans these to produce compile-time projection code:

```
IPerspectiveBase<TModel>                          → 0 event types (manual)
IPerspectiveBase<TModel, TEvent1>                 → 1 event type
IPerspectiveBase<TModel, TEvent1, TEvent2>        → 2 event types
...
IPerspectiveBase<TModel, T1, T2, ..., T15>        → up to 15 event types
```

## Key Properties

| Property | Description |
|----------|-------------|
| **Pure functions** | `Apply(event)` is deterministic — same events always produce same model |
| **Independently rebuildable** | Each perspective can be rebuilt from event stream without affecting others |
| **Snapshot-optimized** | Long streams use snapshots to avoid replaying from event 0 |
| **Time-travel** | Rewind to any point by replaying events up to that position |
| **Multi-event** | A single perspective can handle up to 15 different event types |
