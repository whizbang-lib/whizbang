# #2528: time-ordering checks for event and message ids, wired

Branch `fix/2528-time-ordering-checks-wired`. Scope: the "Decided" parts of the issue and "Required behavior"
items 1-6. The two open items (the framework's own stream ids moving to v7; a database backstop on `event_id`) are
not touched.

## Progress

| Step | State |
|---|---|
| Inventory (this file) | done |
| Interceptor replaces the call, on by default (item 4) | done |
| Validator: severity default Error, Warning logs once + metric, exemption list (items 1-3) | done |
| Wiring at the entry points (item 1) | done: strategies, EF Core / Dapper coordinators (outbox, inbox, dead letters, commit, coalesce), event stores (EF Core, Dapper, SQLite, in memory), dispatcher (cascade source, composite carried ids, subclass emission id) |
| Stream ids reported, not refused (item 5) | done at the event store appends and the dispatcher's outbox path (where the CLR type is known) |
| Analyzer for `new Guid(...)` / `Guid.Parse` into ids (item 6) | done: WHIZ037 |
| Guards (validator has callers; every entry point wired; exemption list complete) | done |
| PostgreSQL refusal matrix (EF Core and Dapper), no partial state | done |
| Docs site pages (`whizbang-ids#time-ordering-checks`, `#stream-id-versions`, `#framework-ids-that-are-not-time-ordered`, `operations/diagnostics/whiz037`, metrics anchor, WHIZ055/056/058 pages) | todo, docs repo |

## Two facts every entry point depends on

- The generated `MessageId.From(Guid)`, the explicit cast, `Parse` and the JSON converter's `Read` refuse any version
  but 7 (`WhizbangIdGenerator.cs`). The public constructor `MessageId(Guid)` and the `Value` initializer check nothing:
  they are the load path for EF Core materialization and the persistence converter.
- A derived id (`EmissionIdentity`, `CompositeChildIdentity`, `GeneratedStreamIdentity`, through `DerivedIdentity`)
  copies the source id's first 80 bits and always stamps version 7. A non-v7 anchor therefore yields an id that
  passes a version check but carries a meaningless timestamp. The check has to sit on the anchor too.

## Inventory: where an event or message id enters

Legend: **strict** = goes through the generated `From`; **raw** = a plain `Guid` or the unchecked constructor;
**minted** = `MessageId.New()` / `TrackedGuid.New()`; **derived** = through `DerivedIdentity` (v7, anchor unchecked).

### Dispatch (`src/Whizbang.Core/Dispatcher.cs`)

| Path | Id source | Non-v7 reachable |
|---|---|---|
| `SendAsync` / `PublishAsync` / `LocalInvokeAsync` and their `Many` forms, `PublishOnceAsync` | minted | no |
| `_createEnvelope(..., presetMessageId)` | internal copy id or minted | no |
| `CascadeMessageAsync(message, sourceEnvelope, ...)` (public) | derived from `sourceEnvelope.MessageId` | yes, through the anchor |
| `_mintPublishedMessageId` inside a receptor handling | derived, then `new MessageId(...)` (raw) | through the anchor |
| `FanOutCompositeLocallyAtPublishAsync` -> `_cascadeMessageAsync(presetEventId)` | `IIdentityPreservingComposite.InnerEventIds` (raw `Guid` list) | **yes** |
| protected `CascadeToOutboxAsync` / `CascadeToEventStoreOnlyAsync` -> `CascadeByRuntimeTypeAsync` | `new MessageId(eventId)` (raw) | a subclass could pass anything |
| `_buildOutboxMessage`, `_serializeToNewOutboxMessage` | copies `envelope.MessageId.Value` | follows the envelope |

### Transport receive and redelivery

| Path | Id source | Non-v7 reachable |
|---|---|---|
| Typed reads (ASB `_tryReadTyped`, RabbitMQ `_readTypedOrAsReceived`, `JsonMessageSerializer`) | strict (JSON converter) | no: refused at deserialization, dead-lettered at the broker |
| `ReceivedEnvelopeReader.TryRead` fallback | strict, null on a bad id | no |
| `AsbMessageHeaderReader` | strict (throws) | no |
| `ReceivedInboxMessageBuilder` | copies the strict envelope id | no |
| `CompositeInboxFanout` raw (390) and typed (484) expansion | `new MessageId(identityIds[i])`, the wire's `InnerEventIds` | **yes, from a remote producer** |
| Broker dead-letter drainers (ASB, RabbitMQ) `TryBuildImport` | raw `Guid.TryParse(broker id)` -> `wh_import_dead_letter` | **yes**; re-enters the inbox or outbox later, inside SQL |
| Redelivery and coalesce wrapper envelopes | minted | no |

### Dead-letter recovery, repair, backfill

| Path | Id source | Non-v7 reachable |
|---|---|---|
| `DeadLetterRecoveryWorker` -> `wh_recover_dead_letter` | moves only inside SQL | not through C# (the backstop is the open item) |
| `SubscriptionBackfillWorker` -> `RedeliveryRequestDispatch` | minted | no |
| Redelivery adoption / bundle reconciliation | ids that came from `InboxMessage` | follows the inbox write |

### Outbox and inbox writes

| Path | Funnel | Non-v7 reachable |
|---|---|---|
| `IWorkCoordinatorStrategy.QueueOutboxMessage` / `QueueInboxMessage` (Immediate, Interval, Batch, StreamAffinity, NonDisposing adapter) | `OutboxMessage.MessageId` / `InboxMessage.MessageId` are `required Guid` | **yes, public** |
| EF Core and Dapper `StoreInboxMessages*`, `StoreOutboxMessagesAsync`, `Store*DeadLettersAsync`, `CommitHandlerResult/BatchAsync`, coalesce fold | all through `_serializeNewOutboxMessages` / `_serializeNewInboxMessages` | **yes** |
| `ImportBrokerDeadLetterAsync` (EF Core, Dapper) | raw | yes (see drainers) |
| `RecordLifecycleCompletionAsync` | ids read back from SQL | only if a stored row is non-v7 |
| `ClaimWorkAsync` | carries no new messages | no |

### Event store appends

| Path | Id source | Non-v7 reachable |
|---|---|---|
| `EFCoreEventStore.AppendAsync(envelope)`, `DapperPostgresEventStore`, `DapperSqliteEventStore`, `InMemoryEventStore` | `envelope.MessageId.Value`, unchecked | **yes** (an envelope built with `new MessageId(g)`) |
| `AppendAsync(message)` overloads | minted | no |
| `AppendBatchAsync` | default interface method looping over `AppendAsync` | follows `AppendAsync` |
| Decorators (Auditing, AppendAndWait, SecurityContext, SyncTracking, Upcasting, ArchiveAware) | forward first; none has a side effect before forwarding | follows the inner store |

## Inventory: framework-minted ids that are not v7 by design

| Site | Version | Becomes a message or event id? | Read for time order? |
|---|---|---|---|
| `IntegrityDeliveryId.For` | v8 | No: `wh_integrity_deliveries.delivery_id`, never the inbox, outbox or event store | No: the drain orders by `queued_at`, the id is only the tie-break and the dedup key |
| `PerspectiveRebuildRequestHandler._runIdFor` | raw SHA-256 (arbitrary nibble) | No: a multi-stream run id inside a command payload | No |
| `SagaDeadlineScheduler.DeadlineScheduleId` | raw SHA-256 | No: a schedule id | No |
| `AuditLedger.RecordIdFor` | v5 | No: the `EventAudited.Id` payload field; the audit row's message id is minted | No |
| `SystemCommands.ChunkRequestId` | v5 | No: a payload request id | No |
| `RedeliveryPump.AnswerSessionFor` | v5 | No: a bundle stream / session id | stream id (open item) |
| `CollectiveOrdering.StreamIdFor`, `SagaItemStreams`, `AuditLedger.StreamFor` | v8 / v5 / v5 | No: stream ids | open item 1 |
| `EFCorePostgresPerspectiveStore._defaultMetadata.EventId = Guid.NewGuid()` | v4 | Not an `event_id` row | **see finding F1** |

None of the framework's non-v7 ids reaches a validated message or event id entry point, so no entry point needs an
exemption: `FrameworkNonTimeOrderedIds` lists every site with its reason, tests pin the values, and a source guard fails
when a new minting site appears without an entry.

## SQL that reads id time order (latest definitions)

- Version assignment: `_emit_event_store_chain` (`ROW_NUMBER() OVER (PARTITION BY stream ORDER BY o.message_id)`),
  `_emit_event_store_chain_for_inbox`.
- Tie-break or ordering on message id: claim_work, claim_orphaned_inbox/outbox, fetch_inbox_batch, fetch_outbox_batch,
  wh_continue_outbox_streams, stamp_pending_commit_sequences and others.
- Apply: claim_orphaned_perspective_events, get_stream_events, complete_perspective_cursor_work
  (`ORDER BY version, event_id`).
- Replay: wh_refile_events (event id as last resort), superseding history apply.
- `wh_lifecycle_completions`: range-partitioned daily by the timestamp in `event_id`; a non-v7 id lands in the default
  partition, which is never dropped.

## Findings for the owner

- **F1 (resolved, safe). A v4 "last applied event id" in perspective row metadata.** `EFCorePostgresPerspectiveStore`
  defaults a row's metadata `EventId` to `Guid.NewGuid()`, and `PerspectiveRunnerBase` reads it back as the last applied
  event id. `PerspectiveIdempotencyFilter` compares it for equality only (the ordering comparison was removed), so it is
  listed with that reason.
- **F2. Transport receive cannot honor `Warning`.** The generated JSON converter calls the strict `From`, so a non-v7
  id on the wire is refused at deserialization (an `ArgumentException`, dead-lettered at the broker) whatever the
  severity. Relaxing it to `Warning` would mean an unchecked converter for every strong id. Left strict.
- **F3. Third-party interception never compiled.** The interceptors for Marten, UUIDNext and Medo returned
  `TrackedGuid` from a method that returns `Guid` (or `Uuid7`), took no parameters where the original took one, and
  called the internal `TrackedGuid.FromIntercepted`. With interception on by default they would break a consumer
  build, so they are no longer generated: those libraries already return time-ordered ids, and an interceptor cannot
  attach metadata to a `Guid`.
- **F4. Recovery inside SQL is not checked in C#.** Dead-letter recovery and broker dead-letter import move ids
  inside SQL. Only the database backstop (open item 2) would catch a non-v7 id there.
- **F5. Stream-id reporting where only a type name is known.** The outbox and inbox writes carry the message type as a
  string, so they cannot tell whether a stream id belongs to an any-version strong id; stream ids are reported at the
  event store appends and the dispatcher's outbox path, where the CLR type is known. A remote producer's stream id that
  reaches only the inbox is not reported.
- **F6. "DeriveFrom" in the issue.** No `DeriveFrom` exists; the framework's derivations are
  `GeneratedStreamIdentity.Derive`, `EmissionIdentity.Derive` and `CompositeChildIdentity.ForChild`. WHIZ037 stays quiet
  on them because they are not one of its shapes.
- **F7. Dead-letter stores are checked too.** `Store*DeadLettersAsync` funnels through the same serializer, so a non-v7
  id is refused there as well, rather than parked in `wh_dead_letters` to re-enter later inside SQL. The broker's own
  dead-letter queue still holds such a message.
