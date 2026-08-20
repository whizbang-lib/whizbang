# Transport-Topology Arc — Implementation Plan

Spec: docs proposals `per-namespace-command-inboxes` (order 38) + `transport-traffic-classes`
(order 37), delivered jointly per the specs' own directive. Baselines verified against develop
`bfc61f491` (post #510/#511).

## Already shipped (specs' phases 0–1)

- **Census** (spec migration step 0): measured. Shared inbox = 75.0% of namespace requests in
  a bulk-import window; 13.9 deliveries per ingress command (14 subscriptions, 13 discards);
  ~42 broker ops per command. Single-handler assumption validated at wire level.
- **#424 increment 1** (= PR #510): `SessionIdleTimeout` 1s→60s, configuration-bindable
  `AzureServiceBusOptions` (`Whizbang:Transports:AzureServiceBus`), idle ops-rate self-check
  (`AsbOpsRateSelfCheck`, warn > `OpsRateWarningThresholdPerSecond`).
  **Delta vs spec:** spec wants the self-check to DEGRADE the managed-health component, not
  just log — fold into Phase 2.
- Also shipped adjacent: post-stamp doorbell (mig 117/118, fenced-drain opt-in), turnkey
  PerspectiveWorker (+ full park without a registry), E2E visibility latency locks.

## Decisions locked by the arc owner (do not relitigate)

- Kind-aware `GetDestination`: events→domain topics, commands→`inbox.<contract-ns>`,
  system→system broadcast inbox.
- `GetCompositeGroupKey` defaults to `GetDestination` (invariant: same key ⇔ same destination).
- `ICompositeFactory`: constituents in → split enumerable out; unifies strategy key + count
  cap + byte budget.
- Namespace name is **`Whizbang.Core.Minting`** behind an **`IEventMint`** facade
  (`mint.Composites` / `mint.Collective` / `mint.Checkpoints`; per-family focused interfaces,
  facade is pure aggregation + single test seam).
- Ownership analyzer: one SERVICE per command type (error severity); N instances fine.
- **Full shared-inbox retirement** — no catch-all remnant (resolves the spec's stale migration
  parenthetical in favor of §resolved-design-decisions).

## Design resolutions proposed by this plan (spec gaps)

1. **`MessageKind` gains `System`** (currently `Unknown/Command/Event/Query`); broadcast
   classification lives inside the strategy keyed on kind, per spec. `MessageKindDetector`
   stays out of the routing hot path (dispatcher call sites keep literal kinds; the System
   branch is reached via strategy-internal classification of framework namespaces).
2. **Minted event families MOVE into `Whizbang.Core.Minting`** (owner decision 2026-08-19:
   pre-v1 is the window to get namespacing and types right; the consuming side corrects when
   the new package lands). Scope: the minted family types (RedeliveryComposite,
   CoalescedEventsComposite, AuditEventsComposite, CompositeEventBase, ICompositeEvent +
   carrier interfaces, and the checkpoint/collective/snapshot family types as each factory
   lands) plus the factories/facade. Migration mechanics, all in Phase 4:
   - Pinned-type ledger: populate `formerNames` for every moved type (WHIZ120 enforces);
     ADD A LOCK TEST that deserialization/type resolution honors formerNames for persisted
     rows written under the old names (wh_outbox/wh_inbox/wh_event_store EnvelopeType strings).
   - Broker filters: `SharedTopicInboxStrategy.CONTROL_PLANE_NAMESPACE` gains the
     `whizbang.core.minting.#` pattern ALONGSIDE `whizbang.core.messaging.#` for the
     transition (deployed subscription rules reconcile on redeploy); old pattern retires with
     the shared inbox in Phase 7.
   - Mixed-fleet window: old builds drop new-name envelopes at the receive gate — acceptable
     pre-v1 on dev slots; deploy consuming services as one wave after the package bump.
   - JsonContextRegistry discriminators + ControlPlaneTypeRegistry regenerate from the new
     namespaces automatically; grep for remaining string-literal namespace references
     (ControlPlaneDestination.For synthesizes from CLR namespace - follows automatically).
3. **`GetSubscriptions(context)` context type**: `InboxSubscriptionContext` (sealed record) —
   carries serviceName, owned domains, and the handled-message enumeration. Requires a NEW
   enumeration API on the receptor registry (today `IReceptorRegistryQuery` is predicates-only);
   generator must emit the handled-type list (AOT).
4. **Contract-namespace vs broker-namespace naming**: all new types disambiguate —
   `ContractNamespace` in routing-side names, `TransportNamespace` in #424-side names.
5. **Namespace selection ownership** (spec contradiction): routing strategy names the ENTITY
   (`GetDestination` stays namespace-unaware); the transport maps tag→TransportNamespace as a
   post-process. Manifest asserts entity names are routable under the tag rules. (Phase 8.)
6. **`ownedDomains` parameter stays** on `GetDestination` (spec elides it for readability;
   dropping it breaks 5 implementations for no gain).
7. **#424 RabbitMQ story** (spec hole): ASB-first for traffic classes; RabbitMQ maps
   TransportNamespace→separate connection/vhost, control-class TTL→`x-message-ttl`,
   sessionless→plain queue. Documented as a docs-PR follow-up when phase 8 lands.

## Phases (one PR each)

- **Phase 2 — adaptive acceptors + health degradation** (#424 incr 4a pulled forward;
  #427 declares it prerequisite). Session acceptors scale with observed active-session demand
  (floor 2–4, growth on pressure) instead of MaxConcurrentSessions standing army; per-entity
  budget hook for K-subscription services; ops-rate self-check degrades the managed-health
  component (closing the Phase-1 delta). ASB receive loop + RabbitMQ consumer-concurrency
  analogue.
- **Phase 3 — routing-seam widening, zero behavior change.** Plural
  `GetSubscriptions(InboxSubscriptionContext)`; receptor-registry enumeration API + generator;
  `MessageKind.System`; kind-aware `GetDestination` incl. system branch;
  `GetCompositeGroupKey`; topology-manifest helper (union of publish destinations +
  subscription set; consumers: startup provisioning, drift checks, acceptor budgets).
  `SharedTopicInboxStrategy` reimplemented on the plural interface returning its one
  subscription — bit-identical topology, locked by tests. Invariant tests: same key ⇔ same
  destination; route/split/subscribe/provision all projections of GetDestination.
- **Phase 4 — Minting.** `Whizbang.Core.Minting`: `IEventMint`, `ICompositeFactory`
  (group key + count cap + byte budget in one splitter), AOT-safe creation seam, analyzer vs
  direct construction (new WHIZ block 150+, EphemeralAnalyzer idiom). First consumers:
  CoalesceShipWorker (drop its Destination GroupBy) and RedeliveryPump (drop its chunking).
  Note: receive-side `MaxInnerEventsAllowed` guard STAYS receive-side.
- **Phase 5 — namespace inbox strategy + dark provisioning** (#427 migration 1). New
  registry-driven strategy (one subscription per handled contract-ns + system inbox);
  manifest-driven provisioning both transports (ASB topic+subscription; RMQ exchange+queue+
  binding per (service, ns)); existence cache; boot management-op budget assert; ownership
  analyzer ships here (+ startup drift check — census says build-time visibility alone is NOT
  sufficient for composite/raw-carry envelope routing, the spec's flagged highest-risk mapping).
- **Phase 6 — publisher flip per contract namespace** (#427 migration 2). E2E locks ride
  along: dual-delivery idempotency, flip-in-flight, rollback, per-namespace DLQ + replay,
  cross-namespace interleave ordering lock (the deliberate semantic change), **O(3N) broker-op
  throughput lock on both transports** (ASB: RecordingBatchSender/Receiver counters; RMQ:
  FakeChannel needs Ack/Nack recorders added).
- **Phase 7 — shared-inbox deletion + system broadcast inbox** (#427 migration 3). Retire
  `SharedTopicInboxStrategy`/`DomainTopicInboxStrategy`; broadcast/control types never route
  to per-namespace inboxes (analyzer + runtime test).
- **Phase 8 — tag-bound TransportNamespace routing** (#424 incr 2): `TagOptions.RouteNamespace`,
  `Transport.Namespaces` map, per-namespace clients + provisioning, `sys-` validation,
  single-namespace no-op guarantee.
- **Phase 9 — control class semantics** (#424 incr 3): `sys-control` tag, TTL≈2×cadence
  minting via `mint.Checkpoints`, sessionless subscriptions, non-durable receive path.
  Decide the `whizbang.core.commands.system` vs `whizbang.core.messaging` split here.
- **Phase 10 — backlog-age duty + OTel** (#424 incr 4b+5).
- **Spike (before phase 6 DLQ tests):** #424's open question — does connection-death lock
  loss increment DeliveryCount? Emulator investigation; DLQ backstop assumptions depend on it.

## Key current-state anchors (from the seam map, develop bfc61f491)

- `IInboxRoutingStrategy.GetSubscription` sole call site: `TransportSubscriptionBuilder.cs:80`
  (reads strategy off OPTIONS, not DI — fix to DI in phase 3).
- `IOutboxRoutingStrategy.GetDestination` call sites: `Dispatcher.cs:4127` (Event, .Address
  only — RoutingKey/Metadata DISCARDED), `Dispatcher.cs:4172` (Command), 
  `IntegrityCheckpointWorker.cs:171`. Generator mirrors: `DispatcherRegistrationsTemplate.cs:125`,
  `DispatcherTemplate.cs:42`.
- Both transports type-test `is SharedTopicOutboxStrategy` to recover the inbox topic
  (ASB SCE:159-193, RMQ SCE:109-141) — retire in phase 5/7.
- `RoutingOptions` inbox default topic INCONSISTENCY: `SharedTopicInboxStrategy()` = "inbox",
  but `InboxRoutingOptionsBuilder.UseSharedTopic` default = "whizbang.inbox" — fix in phase 3.
- `SharedTopicInboxStrategy` hardcodes `whizbang.core.commands.system` +
  `whizbang.core.messaging` patterns; `Commands/System/SystemCommands.cs` XML doc namespace
  claim is stale (says whizbang.system.commands).
- Composite mint seams to unify: `CoalesceShipWorker._foldGroupAsync` (GroupBy Destination
  :185, count-only cap), `RedeliveryPump.PublishAsync` (count+bytes chunking :142-145,
  192KB), receive-side cap `ICompositeEvent.MaxInnerEventsAllowed` 10k.
- No-consumer gates + composite exemptions (PR #511): `AsbReceiveDecisionMaker.cs:155-159`,
  `MessageDiscardPolicy.cs:136-138 + :171-173`, `TransportConsumerWorker.cs:499-506`.
- Free analyzer ID block: WHIZ150+.
- `IReceptorRegistryQuery` is predicates-only — enumeration API needed for GetSubscriptions.
