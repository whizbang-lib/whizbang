# Runtime message cache: parse once, rehydrate once, carry ephemeral facts

Issue: #1526. Status: phase 1 (measurement and design) done; no cache implemented. Scope: 1.0
(decided 2026-10-20).

This file is the design proposal. It records what was measured, where every parse happens today,
the shape of the cache, the order the existing ad hoc caches move onto it, and the decisions the
owner has to make before phase 2 starts.

## 1. What phase 1 added

- **`MessageParseDiagnostics`** (`src/Whizbang.Core/Observability/MessageParseDiagnostics.cs`): a
  process-wide, AOT-safe seam. A parse site calls `Record(messageId, source, kind)`; the compiler
  supplies member, file and line. With no observer subscribed it costs one volatile read. Observers
  implement `IMessageParseObserver`; `MessageParseRecorder` keeps the parses of the messages it
  tracks (or all of them), counts them and summarizes them by kind, source and site.
  Sources: `TransportReceive`, `OffloadRehydration`, `OutboxRead`, `InboxRead`, `EventStoreRead`,
  `LifecycleStage`, `ReceptorDispatch`, `MultiStreamRun`, `Redelivery`. Kinds: `Envelope`,
  `Payload`, `Metadata`, `Scope`.
- **Instrumented sites** (every one of them a parse of a stored or received message):

  | Source / kind | Site |
  |---|---|
  | OutboxRead / Envelope | `OutboxDrainWorker._toOutboxWork`; `EFCoreWorkCoordinator` coalesce-pending fetch |
  | InboxRead / Envelope | `InboxDrainWorker._toInboxWork` |
  | TransportReceive / Envelope | `AsbReceiveDecisionMaker.Decide`; `RabbitMQTransport` (batch and single); `InMemoryWireTransport` (testing wire) |
  | OffloadRehydration / Envelope | `BodyClaimRehydrator.MaybeRehydrateAsync` |
  | EventStoreRead / Payload, Metadata, Scope | `EFCoreEventStore._deserializePayload` (every typed read) and `DeserializeStreamEvents` (perspective drain); orphaned-lifecycle event read in `EFCoreWorkCoordinator` |
  | LifecycleStage / Payload | `LifecycleInvocationHelper` (distribute stages, outbox and inbox); `OutboxDrainWorker._tryResolveTypedEnvelope`; `OutboxPublishWorker`; `ServiceBusConsumerWorker` (two sites) |
  | ReceptorDispatch / Payload | `InboxDispatchWorker._resolveTypedEnvelope` (cache miss only) |
  | MultiStreamRun / Payload, Scope | `MultiStreamCommandBinding` read-back (miss only); `MultiStreamCommandWorker` scope restore |
  | Redelivery / Payload | `RedeliveryPump._buildComposite` |

  Not instrumented, listed so nobody reads a zero as "no parse": the Dapper event store and its
  `EventEnvelopeJsonbAdapter`, the ASB raw-receptor fallback (no message id in hand), and the
  hidden Npgsql parse when EF materializes a `jsonb` column into a `JsonElement` (section 3.3).
- **Regression pins** (each fails if a change adds a parse):

  | Test | Flow |
  |---|---|
  | `tests/Whizbang.Data.EFCore.Postgres.Tests/Diagnostics/MessageParseCountFlowTests.cs` | Real host (public `AddWhizbang` chain on Postgres), real JSON wire, one event, two perspectives, a receptor at the last lifecycle stage |
  | `tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandWorkerTests.cs` (`ParseCount_*`) | Multi-stream runs: K streams of one run, interleaved runs, the same run across batches |
  | `tests/Whizbang.Core.Component.Tests/MultiService/RedeliveryParseCountTests.cs` | Redelivery from the origin's stored events to receiving services |
  | `tests/Whizbang.Core.Tests/Offloads/BodyClaimRehydratorTests.cs` (`ParseCount_*`) | Offload claim-check rehydration |

## 2. Measurements

Counts are per message, from the pins above. The Postgres flow was run three times; every run gave
the same counts.

### 2.1 One event, published locally, applied by two perspectives (N = 2)

17 parses of one event.

| Parses | Kind | Source | Site | Why |
|---|---|---|---|---|
| 5 | Payload | LifecycleStage | `LifecycleInvocationHelper.cs` (outbox loop) | The flush runs five distribute stages (PreDistribute detached and inline, Distribute detached, PostDistribute detached and inline). Each stage parses the payload again, and does so whenever an `IReceptorInvoker` is registered, whether or not any receptor exists for that stage. |
| 4 | Payload | EventStoreRead | `EFCoreEventStore.DeserializeStreamEvents` | `get_stream_events` returns one row per (event, perspective), so the drain parses the event once per perspective (2). The drain's refetch loop then reads the stream again before the completion flush deletes the applied rows, and parses both rows again (2) before the cooldown filter discards them. |
| 4 | Metadata | EventStoreRead | same | as above, the stored metadata (hops) for every row |
| 4 | Scope | EventStoreRead | same | as above, the stored scope for every row |

The multiplier is `5 + 3 x 2N` for a local event (N perspectives), so 17 at N = 2 and 29 at N = 4.
The perspective runners themselves did not parse: drain mode hands the typed envelopes to every
perspective of the batch, and PostAllPerspectives and PostLifecycle reuse the batch's envelopes.

### 2.2 One event delivered from another service (wire, inbox, event store, N = 2)

Measured, not pinned (see 2.6): 21 parses within 30 s of the delivery.

| Parses | Kind | Source | Site |
|---|---|---|---|
| 1 | Envelope | TransportReceive | `InMemoryWireTransport` (the broker receive) |
| 1 | Envelope | InboxRead | `InboxDrainWorker._toInboxWork` |
| 1 | Payload | ReceptorDispatch | `InboxDispatchWorker._resolveTypedEnvelope` (shared by the four inbox stages) |
| 6 | Payload | EventStoreRead | `EFCoreEventStore.DeserializeStreamEvents` |
| 6 | Metadata | EventStoreRead | same |
| 6 | Scope | EventStoreRead | same |

The receive parse is thrown away: the typed envelope is re-serialized into the inbox row and the
drain parses it again. The drain read the two perspective rows three times; in this run neither
perspective reported the event applied within the wait while the last lifecycle stage did fire, so
the third read is most likely a retry (2.6).

### 2.3 Multi-stream run over K streams

| Shape | Payload parses | Scope parses |
|---|---|---|
| One run, K = 8 streams, one batch | 1 | 8 (one per stream; nothing caches it) |
| Two runs interleaved in one batch, 4 streams each | 4 per run (the one slot thrashes) | 0 (no scope) |
| The same run in a later batch (new string, same text) | 0 more, but each stream pays an ordinal compare of the whole payload | |

The one-slot cache fixed the per-stream cost that ran a large run out of memory, but only while one
run owns a batch. Two runs interleaved by the claim's ordering return to one parse per stream. The
slot also hands one mutable command instance to every stream's handler (constraint 1) and does not
re-check scope (constraint 4).

### 2.4 Redelivery

The origin parses each stored event once (`RedeliveryPump._buildComposite`, a `JsonDocument` per
event, to carry it raw). Each receiving service parses the bundle once at the wire. The dispatch
seam's fan-out hands each child the carried `JsonElement` without a parse. Each child then enters
the inbox and pays the inbound flow's parses again as its own row.

### 2.5 Offload rehydration

One parse of the downloaded body into the original envelope; a non-claim envelope passes through
with none. The rehydrated typed envelope is then re-serialized into the inbox row and parsed again
by the inbox drain (inbound flow).

### 2.6 What was not measured, and why

- **Inbound delivery (2.2) is measured but not pinned.** In the turnkey host, a delivery from a
  foreign service onto the event's namespace topic reached the inbox, the event store and the last
  lifecycle stage, but `PerspectiveWorker.OnPerspectiveEventProcessed` never reported the stream for
  either perspective within 30 s, while the drain read the rows three times. A local publish of the
  same event type applies both perspectives every time. Until that is explained (a harness gap, or
  a perspective apply that refuses a foreign event, for example over its scope) the count depends on
  retry timing, and a pin on it would be a timing test. Recorded as open work in section 7.
- **The standard (non-drain) perspective path.** Not exercised by the turnkey host, which drains.
  By reading: up to three event-store reads per (stream, perspective) group (`upcoming events`,
  `runner.RunAsync`, `_loadProcessedEventsAsync`), each a double parse on EF (section 3.3).

## 3. Where parses happen (beyond the instrumented sites)

### 3.1 Per-stage payload parses
`LifecycleInvocationHelper._processOutboxMessagesAsync` / `_processInboxMessagesAsync` parse once per
message per stage, gated only on an invoker being registered. This is the largest single
multiplier measured (5 of 17) and the cheapest to remove: parse once per flush and hand the typed
envelope to every stage, as `OutboxDrainWorker` already does for Pre and PostOutbox.

### 3.2 Drain rows per perspective, and the refetch
`get_stream_events` joins perspective work rows to the event store, so the same event body crosses
the wire once per perspective and is parsed once per row. `PerspectiveWorker` deduplicates only
after `DeserializeStreamEvents` (`_groupAndDedupeDrainModeEventsByStream`). The refetch loop
re-reads rows that were already applied (`_tryRefetchDrainModeStreamEventsAsync`) and parses them
before the cooldown filter discards them.

### 3.3 Hidden parses
- EF typed reads (`EFCoreEventStore` `ReadAsync`, `ReadPolymorphicAsync`, `GetEventsBetween*`):
  Npgsql parses the `jsonb` column into a `JsonElement`, then `_deserializePayload` calls
  `GetRawText()` and parses the text again. Two parses per payload, one of them invisible to the
  seam.
- `MessageHopConverter` builds a DOM per hop and deserializes sub-objects from it; every envelope or
  metadata read pays this per hop.
- Transport receive deserializes the typed envelope, then `TransportConsumerWorker` re-serializes it
  into the inbox row, which the inbox drain parses again: the parse on receive is thrown away.

### 3.4 Existing caches

| Cache | Key | Holds | Bound | Problems against the constraints |
|---|---|---|---|---|
| `EnvelopeRegistry` | payload object by reference | envelope of a live local dispatch | explicit unregister | none relevant: no JSON; keep as is |
| `InboxDeserializeCache` | message id | typed payload | count 10,000, TTL 2 min | not mentioned in the issue; key ignores version and handler, so sibling handler rows share one mutable payload; no scope re-check; `SweepExpired` is never called; count, not bytes |
| `PerspectiveWorker` `ProcessedEventCache` | event work id | ids only | lease-aligned TTL | a fact cache, not a payload cache: keep, but it is the model for "ephemeral facts" |
| `RecentlyProcessedEventCache` | event work id | ids only | TTL 5 min, cap 100,000 | as above |
| Per-batch `batchProcessedEvents` (PerspectiveWorker) | event id | typed envelope | one batch | already the "share across perspectives" behavior, scoped to a batch |
| `MultiStreamCommandBinding` one slot | payload string | command | one entry per command type | thrashes on interleaved runs; O(n) compare on a new string; shares one mutable command across streams; no scope check |
| Inbox composite expansion state | none exists | | | the issue lists it, but `CompositeInboxFanout` is stateless; a retry re-expands and re-serializes every child |

## 4. Design

### 4.1 `MessageRecord`
One immutable entry per message identity.

```csharp
public sealed class MessageRecord {
  public MessageRecordKey Key { get; }           // message id + content identity (4.2)
  public ReadOnlyMemory<byte> Utf8 { get; }      // the bytes as stored or received
  public string EnvelopeTypeName { get; }
  public ScopeStamp Scope { get; }               // tenant, user, and the scope's hash (4.4)
  public MessageFacts Facts { get; }             // first seen, source, size, parse count, stages fired (4.6)
  public bool TryGetPayload<T>(out T payload);   // lazily parsed once through JsonContextRegistry, then frozen
  public EnvelopeView CreateView();              // copy-on-write envelope state for one handler (4.3)
}
```

- Raw bytes are the unit of truth for the entry. The parsed payload is produced at most once per
  entry, through the registered JSON contexts (no reflection), and treated as immutable. Payload
  types are records or init-only in practice; the cache does not enforce deep immutability, so the
  rule is documented and an analyzer can follow (decision D3).
- `ReadOnlyMemory<byte>` rather than `string`: the receive side already holds bytes, and the store
  returns text that is encoded once on insert. Size accounting is exact.

### 4.2 Identity beyond the message id
`MessageRecordKey = (MessageId, ContentHash)` where `ContentHash` is a 64-bit XxHash3 of the stored
bytes, computed once on insert (no allocation, BCL `System.IO.Hashing`). A lookup by id alone
returns the entry only when the caller's bytes hash the same; a different body under the same id (an
upcast, a repair, an offload rehydration, a rewritten stored form) is a different entry and
replaces the old one. Where the caller holds a stored version instead of bytes (event store rows),
`(EventId, StreamVersion)` is accepted as the identity and the hash is computed lazily if bytes are
needed.

### 4.3 Copy-on-write envelope state
Hops, stamps and invocation records are per handler. `EnvelopeView` holds a reference to the
record's immutable envelope core (id, payload, dispatch context, original hops) plus a private
append list for hops and stamps added by this handler. `ReconstructWithPayload` becomes "create a
view". Nothing a handler appends is visible to another handler or written back into the entry.

### 4.4 Scope, re-checked on every read
The entry stores the scope it was stored or received under. `Get` takes the caller's current scope
context and returns the entry only when the scope matches (same tenant, and the caller's scope
satisfies the stored one); otherwise it is a miss, never an error, and the caller parses its own
copy. There is no cross-tenant path: the key alone never returns a payload.

### 4.5 Bounds and invalidation
- **Byte budget LRU.** One process-wide budget (decision D1), accounted in stored bytes plus an
  estimate of the parsed graph (2x bytes until measured). Eviction is approximate LRU in segments
  (a sharded clock), not a global lock on the hot path.
- **Idle cooldown.** An entry not read for the cooldown (default 30 s, decision D2) is dropped on the
  next sweep. The issue's TTL becomes this idle time: a message being worked stays; a finished one
  goes.
- **Explicit invalidation hooks**, each a one-line call at a point that already exists:

  | Event | Where | Effect |
  |---|---|---|
  | Completion | outbox and inbox completion flush (`*CompletionChannel` flushers), perspective completion flush | drop the entry once every consumer that shares it has completed (a reference count per entry, decremented on completion) |
  | Purge | retention and dead-letter purge steps | drop by id |
  | Ephemeral reap | the ephemeral reap step | drop by id; the reap is the floor for ephemeral events, so no cached copy may outlive it |
  | Rebuild / rewind | perspective rebuild and rewind entry points | drop the stream's entries, or bypass the cache for the rebuild's reads (decision D4) |
  | Stored-form rewrite | the canonical temporal rewrite | drop entries of rewritten streams; their bytes change |

### 4.6 Ephemeral facts
`MessageFacts`: first seen, source of first insert, size, parse count, lifecycle stages fired,
perspectives applied in this process, and timing of each. Exposed as a diagnostics read
(`IMessageRecordInspector`) and to runtime decisions only as hints: "this process already applied
event E to perspective P" short-circuits work that the database would also reject. The two existing
id caches (`ProcessedEventCache`, `RecentlyProcessedEventCache`) are the first consumers.

### 4.7 Per process, not authoritative
A miss is always correct. Every reader keeps its database path; the cache only removes repeated
parses within one process. Nothing is written back.

### 4.8 Both entry points populate one entry
Receive (wire and offload) inserts the bytes it parsed under `(MessageId, hash of the bytes it is
about to store)`. The inbox write stores those same bytes, so the drain's read of the stored row
hashes equal and hits. Database read-backs (outbox claim, inbox claim, event store, multi-stream,
redelivery) insert on their first read. This needs one change on receive: store the bytes received
(or a canonical re-serialization computed once) rather than a fresh serialization of the typed
envelope, so the two sides agree on the hash (section 3.3, third point).

## 5. Migration order, each behind its pin

Each step lowers one pinned count and changes the pin in the same pull request.

| Step | Change | Pin | Today | Target |
|---|---|---|---|---|
| 1 | Parse once per flush for the distribute stages (no cache needed, but uses the record's lazy payload) | flow 2.1 LifecycleStage | 5 | 1 |
| 2 | Deduplicate drain rows by event id before parsing; refetch skips rows in the cooldown cache before parsing | flow 2.1 EventStoreRead x3 kinds | 12 (= 3 x 2N) | 3 (once per event) |
| 3 | `MessageRecord` store with the byte budget, idle cooldown and hooks; event store drain and typed reads use it | standard-path pin (to add) | up to 6N | 1 |
| 4 | Multi-stream binding onto the store, keyed by run id and payload hash; scope parsed once per run | multi-stream pins | 4 per run interleaved, K scope | 1 per run, 1 scope |
| 5 | `InboxDeserializeCache` onto the store (adds hash identity, scope check, byte bound, copy-on-write views) | inbound pin (to finish, 2.6) | 1 per handler row | 1 per message |
| 6 | Receive inserts; inbox writes the received bytes | inbound pin | wire 1 + drain 1 per row | 1 |
| 7 | Redelivery and offload paths insert on parse | redelivery and offload pins | 1 per event, then inbound | unchanged at origin; consumer hits |
| 8 | Facts consumers: processed-event caches read facts from the record | existing dedup tests | | |

`EnvelopeRegistry` stays as is: it maps live objects to envelopes during a local dispatch and never
parses.

## 6. Answers to the issue's open questions

- **Which consumers share instances, and which only share parse results?** Share the parsed payload
  instance (read-only by contract): perspectives in a batch (they already do), lifecycle stages of
  one message, streams of one multi-stream run. Share only the parse result, with a per-handler
  envelope view: inbox handlers of one message (they mutate hops and stamps), outbox lifecycle
  stages. Never share across scopes. Evidence: 2.1 shows perspectives already share inside a batch
  at no cost; 2.3 and section 3.4 show the two places a mutable instance is shared today.
- **Where invalidation hooks live.** Completion flushers, the purge and reap steps, rebuild and
  rewind entry points, and the stored-form rewrite (4.5). All exist; each needs one call.
- **Default budget and TTL.** Recommendation: 64 MB per process and a 30 s idle cooldown, one global
  budget with a per-kind override only if measurement under load shows one kind starving another.
  The measured shapes need entries to live only for the span of one message's processing (drain to
  completion), which the idle cooldown covers.
- **1.0 or post-1.0.** In scope for 1.0 by the owner's decision. The measurement supports doing steps
  1, 2 and 4 first: they remove the measured multipliers (5x per flush, 3 x 2N per event, per-stream
  re-reads on interleaved runs) without the full store and without any new memory retention. The
  multi-stream thrash is an out-of-memory risk of the same class as the one the hotfix addressed.

## 7. Open work found in phase 1

- Find why a foreign delivery is not reported applied by either perspective in the turnkey host
  (2.6), then pin the inbound counts. If the apply is refused, that is a defect of its own and gets
  its own issue.
- Instrument the Dapper event store before any Dapper migration step.
- `InboxDeserializeCache.SweepExpired` is never called (expired entries leave only by the cap).
- Dapper `DeserializeStreamEvents` returns an empty list, so Dapper hosts never use drain mode
  (reported by the parse-site survey; not verified by a test here).

## 8. Decisions for the owner

- **D1.** One process-wide byte budget, or one per message kind (events, commands, multi-stream
  payloads)? Recommended: one, 64 MB default, configurable.
- **D2.** Idle cooldown default (recommended 30 s) and whether a hard TTL is wanted on top of it.
- **D3.** Payload immutability: rely on the documented rule, or add an analyzer that flags mutable
  payload types once the cache shares instances?
- **D4.** Rebuilds and rewinds: bypass the cache entirely, or read through it and drop the stream's
  entries at the end?
- **D5.** Receive-side storage: store the bytes received (changes what the inbox row holds, byte for
  byte) or keep re-serializing and give up the receive-to-drain hit?
- **D6.** Expose the facts (4.6) as public API for runtime decisions in consumer code, or keep them
  framework-internal plus diagnostics?
- **D7.** Order: ship steps 1, 2 and 4 (no new retention) before the store, as recommended?
