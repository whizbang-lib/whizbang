# Message Type Hierarchy

The foundation of Whizbang's type system. All messages implement `IMessage`, branching into three distinct intents: commands (change state), events (record facts), and queries (request data). Receptors handle messages with compile-time type safety and zero reflection.

```mermaid
classDiagram
    direction TB

    class IMessage {
        <<marker interface>>
        Root of all messages
    }

    class ICommand {
        <<marker interface>>
        Intent to change state
    }

    class IEvent {
        <<marker interface>>
        Fact about state change
    }

    class IQuery {
        <<marker interface>>
        Request for data
    }

    class ReceptorWithResponse["IReceptor&lt;TMessage, TResponse&gt;"] {
        <<interface>>
        +HandleAsync(TMessage, CancellationToken) ValueTask‹TResponse›
    }

    class ReceptorVoid["IReceptor&lt;TMessage&gt;"] {
        <<interface>>
        +HandleAsync(TMessage, CancellationToken) ValueTask
    }

    class PerspectiveBase["IPerspectiveBase&lt;TModel, TEvents...&gt;"] {
        <<marker interface>>
        Declares model + event types
        for source generator scanning
    }

    IMessage <|-- ICommand : extends
    IMessage <|-- IEvent : extends
    IMessage <|-- IQuery : extends

    ICommand ..> ReceptorWithResponse : processed by
    ICommand ..> ReceptorVoid : processed by
    IQuery ..> ReceptorWithResponse : processed by
    IEvent ..> ReceptorVoid : processed by
    IEvent ..> PerspectiveBase : projected by

    style IMessage fill:#4a90d9,stroke:#2c5f8a,color:#fff
    style ICommand fill:#e8744f,stroke:#b85a3d,color:#fff
    style IEvent fill:#6ab04c,stroke:#4a8035,color:#fff
    style IQuery fill:#9b59b6,stroke:#7d3f98,color:#fff
    style ReceptorWithResponse fill:#f0c040,stroke:#c4a030,color:#333
    style ReceptorVoid fill:#f0c040,stroke:#c4a030,color:#333
    style PerspectiveBase fill:#45b7d1,stroke:#3590a8,color:#fff
```

## Key Concepts

| Type | Purpose | Handled By |
|------|---------|------------|
| `ICommand` | Express intent to change state | `IReceptor<TCommand, TResponse>` — validates rules, returns events |
| `IEvent` | Immutable fact about what happened | `IReceptor<TEvent>` — side effects (void) |
| `IQuery` | Request data without side effects | `IReceptor<TQuery, TResult>` — returns data |

### Receptor Patterns

- **`IReceptor<TMessage, TResponse>`** — Returns a typed result. Used for commands (returns events) and queries (returns data). Supports `LocalInvokeAsync` for in-process RPC.
- **`IReceptor<TMessage>`** — Zero-allocation void handler. Used for event side effects (notifications, cache busting, logging). Supports `PublishAsync` fire-and-forget.
- Both patterns are **AOT-compatible** — source generators create compile-time delegate invocations, never reflection.
