# Inbox / Outbox Pattern

Whizbang implements the transactional outbox and inbox patterns for reliable cross-service messaging. The outbox guarantees **at-least-once delivery**, while the inbox provides **exactly-once processing** through deduplication.

```mermaid
sequenceDiagram
    autonumber
    participant App as Application Code
    participant Disp as Dispatcher
    participant DB_A as Service A Database
    participant OW as Outbox Worker
    participant Transport as Transport<br/>(Kafka / RabbitMQ /<br/>Service Bus / EventStore)
    participant TC as Transport Consumer
    participant DB_B as Service B Database
    participant Recep as Receptor

    Note over App,Recep: ── OUTBOX SIDE (Service A) ──

    App->>Disp: SendAsync(command)
    activate Disp
    Disp->>Disp: Create MessageEnvelope<br/>Resolve DispatchMode

    rect rgb(255, 243, 224)
        Note over Disp,DB_A: Transactional Write
        Disp->>DB_A: Write OutboxRecord<br/>(MessageId, Type, Payload, Metadata)
        Disp->>DB_A: Write to Event Store<br/>(if Local or Both mode)
    end

    Disp-->>App: IDeliveryReceipt
    deactivate Disp

    Note over OW,DB_A: Outbox Worker polls for unpublished records

    rect rgb(255, 235, 238)
        OW->>DB_A: Claim OutboxRecords<br/>(lease-based, partition-aware)
        activate OW
        Note over OW: PreOutbox [Async/Inline]
        OW->>Transport: Publish message
        Note over OW: PostOutbox [Async/Inline]
        OW->>DB_A: Mark PublishedAt<br/>Update StatusFlags
        deactivate OW
    end

    Note over App,Recep: ── INBOX SIDE (Service B) ──

    rect rgb(232, 245, 233)
        Transport->>TC: Deliver message
        activate TC
        TC->>DB_B: Check InboxRecord exists?<br/>(MessageId + HandlerName)

        alt Already processed (duplicate)
            TC-->>Transport: Ack (skip)
        else New message
            Note over TC: PreInbox [Async/Inline]
            TC->>DB_B: Write InboxRecord<br/>(MessageId, HandlerName, Payload)
            TC->>Recep: Invoke receptor(s)
            activate Recep
            Recep-->>TC: Result / Events
            deactivate Recep
            Note over TC: PostInbox [Async/Inline]
            TC->>DB_B: Mark ProcessedAt
            TC-->>Transport: Ack
        end
        deactivate TC
    end
```

## Outbox Record Structure

```mermaid
erDiagram
    OutboxRecord {
        Guid MessageId PK "Idempotency key"
        string Destination "Topic/queue name"
        string MessageType "Fully qualified type"
        json MessageData "Serialized payload"
        json Metadata "Envelope metadata (hops, trace)"
        json Scope "Tenant/user context"
        int Attempts "Delivery attempt count"
        string Error "Last error message"
        datetime CreatedAt "When enqueued"
        datetime PublishedAt "When sent to transport"
        datetime ProcessedAt "When confirmed"
        Guid InstanceId "Claiming worker instance"
        datetime LeaseExpiry "Worker lease timeout"
        Guid StreamId "Stream ordering key"
        int PartitionNumber "Partition assignment"
        enum StatusFlags "Processing status"
        enum FailureReason "Why it failed"
        datetime ScheduledFor "Delayed delivery"
    }

    InboxRecord {
        Guid MessageId PK "Idempotency key"
        string HandlerName PK "Receptor routing"
        string MessageType "For debugging"
        json MessageData "Serialized payload"
        json Metadata "Envelope metadata (hops, trace)"
        json Scope "Tenant/user context"
        int Attempts "Processing attempt count"
        string Error "Last error message"
        datetime ReceivedAt "When consumed"
        datetime ProcessedAt "When handled"
        Guid InstanceId "Claiming worker instance"
        datetime LeaseExpiry "Worker lease timeout"
        Guid StreamId "Stream ordering key"
        int PartitionNumber "Partition assignment"
        enum StatusFlags "Processing status"
        enum FailureReason "Why it failed"
        datetime ScheduledFor "Delayed processing"
    }
```

## Delivery Guarantees

| Aspect | Outbox (Sender) | Inbox (Receiver) |
|--------|-----------------|------------------|
| **Pattern** | Transactional outbox | Idempotent consumer |
| **Guarantee** | At-least-once delivery | Exactly-once processing |
| **Dedup Key** | MessageId | MessageId + HandlerName |
| **Concurrency** | Lease-based claiming | Lease-based claiming |
| **Ordering** | Stream-aware (Phase 7) | Stream-aware (Phase 7) |
| **Retry** | Automatic with backoff | Automatic with backoff |
| **Dead Letter** | After max attempts | After max attempts |

## Message Source Context

Receptors can inspect `ILifecycleContext.MessageSource` to know how a message arrived:

```
MessageSource.Local   → Dispatched in-process (no outbox/inbox)
MessageSource.Outbox  → Publishing from this service's outbox
MessageSource.Inbox   → Received from external transport
```
