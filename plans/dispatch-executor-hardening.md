# Dispatch executor hardening: every entry point runs what the policy decided

Tracking: #1410 (the defect that exposed the gap). Follows #1395 / #1406.

## Why

#1406 centralized the dispatch *decision* and tested it thoroughly (decision table, every combination,
property checks). The *local run* still goes through five executor families: generated typed invokers,
generated publishers, foreign-assembly lookups, runtime registrations, and lifecycle-stage invocation.
Each family reaches a different set of handler shapes, and "has a handler" is computed separately from
"runs the handler". Component tests crossed entry points with one handler shape, and the planned
integration tests were not built. #1410 lived in exactly that gap.

## Rule for this PR

Every item starts as a failing test (RED) on the real generated dispatcher, then gets a fix (GREEN). An
item whose test passes first is recorded as "verified, not a defect" with the test kept. An item that
needs an owner decision becomes a GitHub issue labeled `question` and stays listed here.

## Suspected defects (from the executor inventory)

Status: `todo` / `red` / `fixed` / `not-a-defect` / `question #N`

| # | Suspected defect | Status |
|---|---|---|
| E0 | Owned command, result-returning receptor, publish/cascade: silently dropped (#1410) | fixed |
| E0b | Integrity checkpoint advances its watermark, then fails on an empty service identity every cycle | fixed |
| E1 | Event with a result-returning receptor (R not object): never runs on publish/cascade in its own service | fixed |
| E1b | Typed event receptors in the publisher are not stage-filtered: `[FireAt(pipeline)]` with R=object fires twice | fixed |
| E2 | Sync receptors (void or result) never run on publish/cascade; owned command with only a void sync receptor throws NoHandler on publish | fixed |
| E3 | Registry entries for sync receptors resolve the async interface: worker stages throw for `ISyncReceptor` | fixed |
| E4 | Polymorphic receptors (interface/base message type) unreachable on every local path | fixed |
| E5 | Generic overloads key on the static type: `SendAsync<TMessage>`, `SendManyAsync<T>`, `LocalSendManyAsync<T>`, `PublishManyAsync<TEvent>`, perspective wait; generic `SendAsync` ignores `Routed<T>` | fixed |
| E6 | Overloads with divergent fallbacks: `LocalInvokeAsync<TMessage,TResult>(m, ctx)` no sync/RPC; generic void `(m, ctx)` ignores foreign assemblies; options/receipt overloads no RPC extraction | fixed |
| E7 | Cascaded command not among the assembly's response types falls through the generated `CascadeToOutboxAsync` silently; command arms resolve an event topic | fixed |
| E7b | "Has a handler" counts a publisher entry that runs nothing (pipeline-stage-only receptor, response-type-only entry): owned command dropped, unowned copy marked handled | fixed |
| E8 | Generated invokers unwrap a top-level `Routed<T>` result before the dispatcher sees it: the route is lost and defaults to Both | fixed |
| E9 | Batch path drops `ScheduledFor`/`LocalDeferred`, priority, payload limit, token/timeout, NoRebroadcast, outbox collector, composite fan-out, schema gate, tracking; no-outbox throws instead of deferring | fixed |
| E10 | Runtime receptors: results discarded on publish/cascade; send takes only the first; synthetic envelope; no token | fixed |
| E11 | `_trackEventForSync` ignores the ambient scoped tracker: cascaded events invisible to LocalInvokeAndSync / WaitForPerspectives | fixed |
| E12 | `PublishAsync(e, options)` skips singleton tracking; neither publish overload tracks the scoped tracker; LocalNoPersist cascades are tracked | fixed |
| E13 | Worker-stage cascades ignore `[DefaultRouting]` | fixed |
| E14 | RPC-extraction cascade passes no event id, envelope or token (no tracking, non-idempotent ids, no source security) | fixed (the extracted response cascades with the invoke envelope and token through the shared loop) |
| E15 | RPC-extraction path skips envelope registry, trace store, tags, PostLifecycle, ImmediateDetached, metrics, perspective-sync wait | fixed (extraction runs through the traced invoke path) |
| E16 | Send/LocalInvoke run only the first receptor for a message type; publish runs all | question #1412 |
| E17 | `if (!isDefaultDispatch)` branch in generated cascade publishers is unreachable | fixed (removed with the publisher rewrite) |
| E18 | Dispatcher-level `[AwaitPerspectiveSync]` reads LocalImmediateInline only (no-op for default receptors; double wait for explicit Inline); sync snippets never wait | fixed |
| E19 | Void LocalInvoke paths never process tags | fixed (and a cascade's local run) |
| E20 | SendAsync local run fires ImmediateDetached but not PostLifecycle; publish/cascade fire neither | question #1413 |
| E21 | `PublishToOutboxDynamicAsync` hard-codes Mode=Both (ignores the copy mark), returns silently with no strategy, skips StreamIdGuard and the dispatch record | fixed |
| E22 | Generated cascade arms omit `recordLocalDispatch`: another host can double-fire a cascade that ran locally | fixed |
| E23 | Send and batch outbox writes ignore `DispatchOutboxCollector.Current` (bypass the atomic commit) | fixed |
| E24 | `PublishOnceAsync` claims then publishes non-atomically; a failed publish keeps the claim and the event is lost | question #1414 |
| E25 | Default interface methods for batch-options overloads drop `options`; security builder exposes only some overloads | question #1415 |
| E26 | LocalInvoke (non-options), receipt overloads, CascadeMessageAsync, batch publish skip the schema-ready gate | fixed |
| E27 | `_localInvokeWithOptionsAsync` passes `hasHandler: true`, so ContinueWhenNoHandler cannot apply | not-a-defect: a result-required call refuses without a handler by design (DispatchRules); the real check is now passed |
| E29 | The worker-stage invoker had no event cascader unless a transport consumer was added: every result returned at a worker stage was discarded | fixed |
| E30 | LocalInvoke<TResult> on a tuple-returning receptor ran the receptor twice (failed cast, then re-run through extraction), and swallowed an InvalidCastException the receptor threw itself | fixed |
| E31 | A receptor's sync context was set inside an async method, so it never reached the receptor; FireAlways receptors could not see their outcome | fixed |
| E32 | Batch calls ignored a Routed<T> message (route dropped, or the wrapper looked up as the message) | fixed |
| E33 | PublishAsync ignored a Routed<T> message | fixed |
| E34 | A sent or batched event routed Local or EventStoreOnly was never stored; send copies always took a command destination | fixed |
| E35 | A published event nothing here handles was treated as handled, so its copy claimed it was | fixed |
| E36 | A typed or void local invoke under a selected policy ignored the policy's outbox write and the message's route | fixed |
| E37 | A batch publish with no outbox threw for events instead of deferring or logging as a single publish does | fixed |
| E38 | A void local invoke could not reach a result-returning sync receptor (the any-invoker left sync receptors out) | fixed |
| E39 | An envelope registered for an object payload read back as null when looked up by the message's type (every untyped send, invoke and batch); publish and cascade local runs registered none | fixed |
| E40 | Cascade and receptor-result paths generated a stream id only for IHasStreamId messages, so a plain [StreamId] property stayed empty; local invoke generated none | fixed |
| E41 | Batch calls ignored the caller's priority and payload limit, and the batch outbox write ignored the token | fixed |
| E42 | `CascadeMessageAsync` with no source envelope and no ambient context handed the handlers a placeholder envelope with no message; message security refused it as a null payload (found by the real-Postgres tests) | fixed |
| E43 | Cause before effect: a send, a cascade and every batch wrote the message's copy after its local run, so an event its handler emitted reached the outbox and the event store first; a publish only won the race (found by the real-Postgres tests) | fixed |
| E44 | With message security and no caller context, a result-returning local invoke refuses after its handler ran (PostLifecycle stage) while every other entry runs | question #1419 |
| E45 | A publish's copy write, and a cascade's (no token on the generated cascade), never received the caller's cancellation token, so a write blocked in the database could not be canceled | fixed |
| E46 | A flush its caller canceled left its work queued: the scoped strategy's disposal safety net (and the immediate strategy's next flush) wrote it anyway with no token, so a canceled dispatch still sent its copy and its caller waited for it | fixed |
| E47 | The envelope a publish, cascade or local invoke registers for its local run stamped the message's sent time and correlation again after its copy was stored, so the local handlers saw values the stored copy did not carry; a batch stamped its copies only after its local runs (found by a consumer's redelivery-dedup test, #1455) | fixed |
| E48 | The envelope a local handler looks up carried a fresh message id, not the id of the copy the dispatch stores; for a send, the local envelope also anchors what its handlers emit, so it keeps its own id and a separate lookup envelope carries the copy's (#1464) | fixed |
| E49 | Perspective sync tracked an id the Postgres event stores never wrote: SyncTracking ran outside SecurityContext, which built the stored envelope with its own id (#1465) | fixed |
| E28 | `_sendCoreAsync` does not set `MessageContextAccessor.CurrentContext`; an explicit context reaches the receptor only via the hop | fixed |

## Test infrastructure

- [x] `DispatchScenarios` linked into the Component tests; every runnable row executes on the real dispatcher (Integration still to do)
- [ ] Handler-shape catalog of generated test types: void async, result async (single, tuple, array, Routed), void sync, result sync, object result, polymorphic, local-stage `[FireAt]`, pipeline-stage `[FireAt]`, runtime-registered, foreign-assembly, multiple receptors, `[AwaitPerspectiveSync]`, `[DefaultRouting]`; each × owned/unowned × command/event/system command
- [ ] Shared probes: per-receptor invocation counts, cascaded-result capture, outbox/event-store recording with mode and schedule

## Component: execution matrix (real generated dispatcher)

- [x] Every public entry point × every handler shape × owned/unowned × kind (324 cells): handler ran exactly once, result cascaded exactly once, copy written exactly when the policy sends on, marked handled here
- [x] Consistency property: decision-table rows (140) and the shape matrix (324) each assert run iff decided
- [ ] Internal cascade entry points: receptor-result cascade (single, tuple, array, Routed per mode), RPC extraction, worker cascade via `DispatcherEventCascader`, composite fan-out
- [ ] Tangential features, at least one execution test per entry point: lifecycle stages (LocalImmediate*, PreOutbox, PostInbox, ImmediateDetached once per service, PostLifecycle), PublishOnceAsync, DispatchOutboxCollector, security context to handler and outbox row, perspective sync tracking and waits, tags, system commands, multi-stream commands, scheduled publishes and saga ticks, ephemeral, NoRebroadcast, envelope registry, trace store, metrics, priority, StreamId generation/guard, Timeout/CancellationToken, BatchFailureMode, copy mark, event-store-only routes
- [ ] Worker-stage invocation × every handler shape (PostInbox, PreOutbox, perspective stages), including sync receptors

## Integration (real Postgres, two services)

`DispatchExecutionIntegrationTests` and `DispatchRoundTripIntegrationTests` (real outbox, real drain and inbox workers, wire envelope serialized between services):

- [x] Marked copy not run twice by the sender (PreOutbox and when it comes back), run once by the receiver; unmarked copy's only handler runs at PreOutbox
- [x] Owned command never appears in `wh_outbox`, on every entry point (Route.Outbox included); owned command with no handler refused
- [x] System command handled once on the sender and broadcast (`SystemCommandBroadcastIntegrationTests`)
- [x] Unowned send with a local handler: handled here, one marked copy, run once by the receiving service
- [x] Scheduled publish and scheduled send, single and batch: nothing runs now, one copy carrying the schedule and the deferred run, run exactly once when it comes back (fails if the deferred-copy rule is removed)
- [x] Event store: store-only row never transported; cause before effect for every entry and effect route (E43); command routed store-only refused
- [x] Copies diverted into a unit of work that never commits leave no rows
- [x] Cancellation while the copy is being written (provably queued behind a lock): the caller sees the cancellation and no rows are left, single, batch and cascade (E45, E46)
- [x] Offloaded body: the claim keeps the dispatch mark (existing hook test) and a rehydrated copy comes back with it (unit)
- [x] RabbitMQ and Azure Service Bus: the dispatch mark (handled here, deferred, plain) survives a real broker round trip; the drain worker, inbox worker and wire serialization of the full trip are covered on Postgres above

## Generator

- [x] Behavior tests for every changed emission (`ReceptorDiscoveryLocalRunEmissionTests`): (publisher typed blocks, sync in publish groups, registry sync entries, polymorphic expansion on local paths, CascadeToOutbox fallthrough)
- [ ] Regenerated caches checked in where the repo tracks them

## Mutation and benchmarks

- [ ] Stryker on `Dispatch/Policy/**` and the executor paths in `Dispatcher.cs`; every survivor killed or justified here
- [x] `DispatcherBenchmarks` LocalInvoke/Send before and after (Apple M-series, .NET 10, Release). Parity within noise. The plan's "20 ns, 0 B" figure is not what this benchmark measures on develop either: it includes envelope creation and receptor resolution.

  | Benchmark | develop c7f7510b0 | this branch |
  |---|---|---|
  | LocalInvoke_Lightweight_NoTracing | 1,137 ns / 3.47 KB | 1,148 ns / 3.48 KB |
  | LocalInvoke_Lightweight_WithTracing | 1,103 ns / 3.47 KB | 1,117 ns / 3.48 KB |
  | LocalInvoke_Lightweight_WithContext | 960 ns / 3.19 KB | 1,008 ns / 3.20 KB |
  | Send_Lightweight_NoTracing | 898 ns / 2.74 KB | 878 ns / 2.77 KB |
  | Send_Lightweight_WithTracing | 895 ns / 2.74 KB | 861 ns / 2.77 KB |
  | Send_Lightweight_WithContext | 736 ns / 2.42 KB | 721 ns / 2.49 KB |

## Consumer verification

- [ ] JDX full integration suite on the patched packages with the send-rewriting test wrapper removed

## Verified not a defect (tests kept)

- Runtime receptor result on SendAsync cascades (E10, send half)
- PublishMany with no outbox handles events locally without throwing (E9, no-outbox half)
- LocalSendMany refuses a future schedule like LocalInvoke (E9)

## Consumer run (JDX full integration suite, 0.2621.0, real sends): 72 of 1,478 failed

- 1 platform defect: E0 (#1410)
- 69 JDX test-infrastructure flakes that pass in isolation (static FastEndpoints resolver holding a disposed host; saga reader ordering)
- 1 JDX fixture bug predating the release (connection string key)
- 1 consumer test setup mistake
