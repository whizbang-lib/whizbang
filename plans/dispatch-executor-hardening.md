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
| E7 | Cascaded command not among the assembly's response types falls through the generated `CascadeToOutboxAsync` silently; command arms resolve an event topic | todo |
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
| E18 | Dispatcher-level `[AwaitPerspectiveSync]` reads LocalImmediateInline only (no-op for default receptors; double wait for explicit Inline); sync snippets never wait | todo |
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
| E28 | `_sendCoreAsync` does not set `MessageContextAccessor.CurrentContext`; an explicit context reaches the receptor only via the hop | fixed |

## Test infrastructure

- [ ] Move `DispatchScenarios` where Component and Integration can consume it (shared source or `Whizbang.Testing`); every level draws from one case list
- [ ] Handler-shape catalog of generated test types: void async, result async (single, tuple, array, Routed), void sync, result sync, object result, polymorphic, local-stage `[FireAt]`, pipeline-stage `[FireAt]`, runtime-registered, foreign-assembly, multiple receptors, `[AwaitPerspectiveSync]`, `[DefaultRouting]`; each × owned/unowned × command/event/system command
- [ ] Shared probes: per-receptor invocation counts, cascaded-result capture, outbox/event-store recording with mode and schedule

## Component: execution matrix (real generated dispatcher)

- [ ] Every public entry point and overload (inventory section A) × every handler shape × owned/unowned × kind: handler ran exactly the expected number of times, result cascaded with its route, outbox rows and mark, event-store rows, receipt or exception
- [ ] Consistency property: whenever the decision says run here, a handler ran exactly once; whenever it ran, the decision said so
- [ ] Internal cascade entry points: receptor-result cascade (single, tuple, array, Routed per mode), RPC extraction, worker cascade via `DispatcherEventCascader`, composite fan-out
- [ ] Tangential features, at least one execution test per entry point: lifecycle stages (LocalImmediate*, PreOutbox, PostInbox, ImmediateDetached once per service, PostLifecycle), PublishOnceAsync, DispatchOutboxCollector, security context to handler and outbox row, perspective sync tracking and waits, tags, system commands, multi-stream commands, scheduled publishes and saga ticks, ephemeral, NoRebroadcast, envelope registry, trace store, metrics, priority, StreamId generation/guard, Timeout/CancellationToken, BatchFailureMode, copy mark, event-store-only routes
- [ ] Worker-stage invocation × every handler shape (PostInbox, PreOutbox, perspective stages), including sync receptors

## Integration (real Postgres, two services)

- [ ] Marked copy not run twice by the sender, run by the receiver; unmarked echo skipped
- [ ] Owned command never appears in `wh_outbox`, on every entry point and handler shape
- [ ] System command handled once on every service
- [ ] Unowned send with a local handler: handled here and by the owner
- [ ] Scheduled publish and scheduled send each handled exactly once by their own service, single and batch
- [ ] Event store: store-only row never transported; cause-before-effect ordering for both cascade forms; command routed store-only throws
- [ ] Rollback leaves no outbox or event-store rows; cancellation during flush leaves no partial rows
- [ ] Large body still offloads in the publish worker
- [ ] RabbitMQ and Azure Service Bus smoke: unowned send with local handler, marked copy, scheduled delivery

## Generator

- [ ] Snapshot/behavior tests for every changed emission (publisher typed blocks, sync in publish groups, registry sync entries, polymorphic expansion on local paths, CascadeToOutbox fallthrough)
- [ ] Regenerated caches checked in where the repo tracks them

## Mutation and benchmarks

- [ ] Stryker on `Dispatch/Policy/**` and the executor paths in `Dispatcher.cs`; every survivor killed or justified here
- [ ] `DispatcherBenchmarks` LocalInvoke/Send before and after; LocalInvoke stays under 20 ns and 0 B

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
