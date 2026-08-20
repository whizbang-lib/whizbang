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

## Delivery model (owner decision 2026-08-19): ONE PR for the whole arc

The remaining phases (3-10) accumulate on the single branch `feature/transport-topology`
(based on develop e9dec8417, post phase-2/#513) and ship as ONE PR whose acceptance gate is
the transport-parity E2E suite + the O(3N) broker-op throughput lock. Phases below are the
COMMIT organization within that branch, not PR boundaries. Phase 2 shipped separately as
PR #513 before this decision; it stays (no revert).

## Phases (commit groups on the arc branch)

- **Phase 2 — adaptive acceptors + health degradation** (#424 incr 4a pulled forward;
  #427 declares it prerequisite). Session acceptors scale with observed active-session demand
  (floor 2–4, growth on pressure) instead of MaxConcurrentSessions standing army; per-entity
  budget hook for K-subscription services; ops-rate self-check degrades the managed-health
  component (closing the Phase-1 delta). ASB receive loop + RabbitMQ consumer-concurrency
  analogue.
  **STATUS: implemented on this branch (uncommitted).** `AsbAcceptorGovernor` (floor 4 default,
  double-on-pressure ≥80%/window, halve-on-quiet <25%/window, ceiling = MaxConcurrentSessions)
  applied to RUNNING processors via SDK `UpdateConcurrency` (available in Azure.Messaging.ServiceBus
  7.20.1 — no stop/recreate needed); options `EnableAdaptiveAcceptors`(true)/`AcceptorFloor`(4)/
  `AcceptorEvaluationInterval`(30s) bound in the AOT post-configure; ops-rate projection now
  floor/live-slot-based in adaptive mode and stored on the transport; `AsbOpsRateHealthSource`
  degrades the `"transport"` managed-health component while the projection exceeds threshold
  (recovers on re-projection, incl. adaptive decay); per-entity budget hook
  `AsbOpsRateSelfCheck.AcceptorCeilingForIdleOpsBudget` (pure math, phase-5 consumer);
  RabbitMQ = doc note on RabbitMQOptions (push consumers have no idle accept economics;
  prefetch/DOP adaptation deferred to traffic classes).
- **Phase 3 — routing-seam widening, zero behavior change.** Plural
  `GetSubscriptions(InboxSubscriptionContext)`; receptor-registry enumeration API + generator;
  `MessageKind.System`; kind-aware `GetDestination` incl. system branch;
  `GetCompositeGroupKey`; topology-manifest helper (union of publish destinations +
  subscription set; consumers: startup provisioning, drift checks, acceptor budgets).
  `SharedTopicInboxStrategy` reimplemented on the plural interface returning its one
  subscription — bit-identical topology, locked by tests. Invariant tests: same key ⇔ same
  destination; route/split/subscribe/provision all projections of GetDestination.
  **STATUS: implemented on feature/topology-phase3-routing-seam (uncommitted).**
  `MessageKind.System` (appended, values stable) + detector framework-system tier
  (attribute > system-ns > interface > ns > suffix; detector still off the hot path);
  `HandledMessageInfo` + `IReceptorRegistryQuery.GetHandledMessages()` DIM (defaults [])
  emitted by `ReceptorRegistryQueryGenerator` into `ReceptorRegistryContribution.HandledMessages`
  (aggregated/deduped/sorted by `WhizbangReceptorRegistryQuery.GetHandledMessages`);
  `InboxSubscriptionContext` + plural DIM wrapping singular(Command), explicit overrides on
  both built-in inbox strategies; `TransportSubscriptionBuilder.BuildInboxDestinations()`
  (plural, DI-strategy-first with options fallback, registry-fed context) — both
  `TransportConsumerBuilderExtensions` factory sites + `AddTransportSubscriptionBuilder`
  now resolve strategy/registry from DI; `UseSharedTopic` inbox default fixed
  "whizbang.inbox"→"inbox" (RED-locked); `GetCompositeGroupKey` DIM (Address|RoutingKey,
  same-key⇔same-destination property test); dormant System branches on both outbox
  strategies (locked for Command/Event/System); `TopologyManifest`/`TopologyManifestBuilder`
  pure projection over `MessageTypeCatalogEntry`. NO `[Obsolete]` on singular
  `GetSubscription` — CS0618 is escalated to error repo-wide (Directory.Build.props); doc-note
  deprecation instead, removal rides the shared-inbox retirement phase.
- **Phase 4 — Minting.** `Whizbang.Core.Minting`: `IEventMint`, `ICompositeFactory`
  (group key + count cap + byte budget in one splitter), AOT-safe creation seam, analyzer vs
  direct construction (new WHIZ block 150+, EphemeralAnalyzer idiom). First consumers:
  CoalesceShipWorker (drop its Destination GroupBy) and RedeliveryPump (drop its chunking).
  Note: receive-side `MaxInnerEventsAllowed` guard STAYS receive-side.
  **STATUS: implemented on feature/transport-topology (uncommitted).** Family moves landed
  (ICompositeEvent/CompositeEventBase/carrier interfaces/RedeliveryComposite/
  CoalescedEventsComposite/AuditEventsComposite/Fanout* → `Whizbang.Core.Minting`;
  CompositeInboxFanout + EventFlags stay receive-side in Messaging). Migration mechanics:
  ledger wired as AdditionalFiles into Whizbang.Core.csproj (WHIZ120 governance now ACTIVE
  in-repo — it was inert before, and the extraction target was silently refreshing the ledger
  on rename), formerNames recorded for all three moved pinned types (old-name deserialization
  + EventMarkerResolver formerNames fallback RED→GREEN-locked in
  MintedTypeRenameCompatibilityTests), `whizbang.core.minting.#` admitted alongside
  `whizbang.core.messaging.#` on SharedTopicInboxStrategy (publish⇔subscribe lock incl.
  ControlPlaneDestination subject synthesis). `IEventMint`/`ICompositeFactory`/
  `CompositeMintRequest` (strategy-first `CompositeGroupKey.FromStrategy` + stamped-key
  `FromKey`) registered turnkey in AddWhizbang; both producers refactored onto the factory
  behavior-preserving (all pre-existing tests green unchanged). WHIZ150 (Warning) ships as
  `MintedCompositeConstructionAnalyzer` (exempt: Minting namespace, Whizbang.Core assembly,
  BuildComposite/CompositeFactory builder lambdas; tests NoWarn per the WHIZ110 idiom).
- **Phase 5 — namespace inbox strategy + dark provisioning** (#427 migration 1). New
  registry-driven strategy (one subscription per handled contract-ns + system inbox);
  manifest-driven provisioning both transports (ASB topic+subscription; RMQ exchange+queue+
  binding per (service, ns)); existence cache; boot management-op budget assert; ownership
  analyzer ships here (+ startup drift check — census says build-time visibility alone is NOT
  sufficient for composite/raw-carry envelope routing, the spec's flagged highest-risk mapping).
  **STATUS: implemented on feature/transport-topology (uncommitted).**
  `NamespaceInboxStrategy` (opt-in via `Inbox.UseNamespaceInboxes()`, NOT default): per
  DISTINCT handled COMMAND contract-ns `inbox.<ns>` (dedup, lowercase, `OwnedCommandInbox`
  metadata marker) + system broadcast inbox `inbox.whizbang` (patterns from
  `SharedTopicInboxStrategy.BuildRoutingPatterns(∅)` — system/control/minting by construction;
  whizbang.core.* subtree NEVER gets per-ns inboxes, minted-subject admit lock) + transitional
  shared subscription bit-identical to today (retires phase 7). Composite/raw-carry surface:
  `InboxSubscriptionContext.ConsumedEventNamespaces` (fed from EventSubscriptionDiscovery —
  reused, not duplicated) + `ComputeConsumedNamespaces` union. DARK provisioning:
  `TopologyManifest` gained `ServiceName`; `IInfrastructureProvisioner.ProvisionManifestAsync`
  DIM (no-op default); manifest TryAdd factory (AddTransportConsumer both overloads +
  AddTransportSubscriptionBuilder); worker calls it before subscribing. ASB: shared
  `ServiceBusEntityProvisioning` (transport's ensure-topic/create-subscription/SqlFilter
  internals extracted; both paths delegate — settings parity by construction), per-process
  existence caches, boot op budget ≤2/topic+4/subscription+1/owned-inbox asserted, zero ops on
  re-provision. RMQ: shared `RabbitMQEntityProvisioning` (exchange+DLX/DLQ+queue-args+bindings),
  same cache/budget locks via FakeChannel declare-counters. Ownership: WHIZ151 (Error,
  CompilationEnd) — duplicate command INBOX receptors (lifecycle-only/[FireAt] exempt,
  System-kind exempt, kind detection shared with the registry generator via
  `CompileTimeMessageClassification`); runtime cross-service check = provisioning drift
  detection (ASB: `GetSubscriptionsAsync` enumeration on owned inboxes; RMQ: best-effort
  exchange-exists-without-our-queue passive probe) → `TopologyDriftState` +
  `TopologyDriftHealthSource` ("topology" component, Degraded) + structured error log.
  DARK guarantee locked: default shared-strategy manifest names exactly today's entities.
- **Phase 6 — publisher flip per contract namespace** (#427 migration 2). E2E locks ride
  along: dual-delivery idempotency, flip-in-flight, rollback, per-namespace DLQ + replay,
  cross-namespace interleave ordering lock (the deliberate semantic change), **O(3N) broker-op
  throughput lock on both transports** (ASB: RecordingBatchSender/Receiver counters; RMQ:
  FakeChannel needs Ack/Nack recorders added).
  **STATUS: implemented on feature/transport-topology (uncommitted).**
  SPIKE FIRST (EmulatorLockLossDeliveryCountSpikeTests, recorded reality on the emulator):
  connection-death SESSION lock loss does NOT increment DeliveryCount (stays 1) — the open
  question CONFIRMED for session entities; explicit abandon DOES (2); NON-session
  message-lock loss DOES (2) and the plain-subscription DLQ valve fires end-to-end.
  DLQ posture: rely on explicit dead-letter paths (handler failure → abandon) only, never
  storm-driven count exhaustion, on session-enabled inboxes; validate against a real
  namespace before relying on the inverse (emulator fidelity caveat documented in-test).
  The flip: `NamespaceOutboxStrategy` (opt-in `Outbox.UseNamespaceRouting()`): events
  byte-identical to DomainTopicOutboxStrategy; UNflipped commands byte-identical to
  SharedTopicOutboxStrategy; FLIPPED namespaces → `inbox.<ns>` via the shared
  `CommandInboxNaming` helper (NamespaceInboxStrategy refactored onto it — publisher and
  subscriber naming agree by construction); framework-reserved (whizbang.core.*) flips to
  the broadcast inbox, never a per-ns inbox; System kind → broadcast inbox unconditionally.
  Flip set on RoutingOptions (`RouteCommandNamespaceToInbox` repeatable /
  `RouteAllCommandNamespacesToInbox`), consulted LIVE, configuration-bindable
  (`Whizbang:Routing:CommandNamespacesToInbox`, `"*"` = all; explicit-key AOT binder in
  WithRouting's deferred IOptions factory — rollback = remove the entry, no redeploy).
  Publish-time authority: TransportPublishStrategy gained the name-based
  `namespaceRouting` seam (the outbox row carries type-name strings; both transports' DI
  factories wire it via the `is NamespaceOutboxStrategy` branch). Flipped destinations are
  marked `RequireProvisionedEntity`: transports NEVER auto-create consumer-provisioned
  inbox entities (manifest provisioning also skips publish-side `inbox.*`), verify
  existence (ASB admin pre-check / EntityNotFound wrap; RMQ passive probe on a dedicated
  channel), and throw the new `UnroutableDestinationException` carrying the entity name —
  loud, never a silent broker drop; negative answers uncached so outbox retries succeed
  after provisioning. E2E locks: NamespaceInboxFlipE2ELockTests on BOTH transports
  (derivation, single-handler + zero non-handler ops, multi-handler, unroutable, discard
  boundary, same-ns strict order, cross-ns interleave completeness), DLQ + replay per
  namespace, PublisherFlipMigrationE2ETests (harness gained per-service WithRouting):
  flip-in-flight / rollback / dual-delivery-identity. O(3N) throughput locks
  (Asb/RabbitMQBrokerOpsThroughputLockTests): 25 commands = 75 broker ops exactly
  (send+deliver+settle), ops/command 3 ≤ 6 bound, topology-derived fan-out, FakeChannel
  gained BasicAck/BasicNack counters. Emulator fixture: Config.json gained spike + wbtopo
  entities; container reuse now hash-checks the config via a compose label (stale-topology
  containers recreate automatically).
- **Phase 7 — shared-inbox deletion + system broadcast inbox** (#427 migration 3). Retire
  `SharedTopicInboxStrategy`/`DomainTopicInboxStrategy`; broadcast/control types never route
  to per-namespace inboxes (analyzer + runtime test).
  **STATUS: implemented on feature/transport-topology (uncommitted).** As LIBRARY code
  retirement is an explicit OPT-IN completing the migration, not type deletion (unflipped
  delegation still needs SharedTopicOutboxStrategy; mid-migration consumers need the legacy
  strategies): `RoutingOptions.RetireSharedInbox()` + config binding
  (`Whizbang:Routing:RetireSharedInbox`), valid ONLY under the full flip
  (RouteAllCommandNamespacesToInbox / `"*"`) — the retirement guard throws at startup (the
  WithRouting options factory, post-binding) AND in NamespaceInboxStrategy.GetSubscriptions
  (defense in depth), naming the unflipped state. Under retirement the subscription set is
  EXACTLY per-namespace inboxes + `inbox.whizbang` (transitional shared part dropped;
  singular GetSubscription throws rather than resurrect the catch-all);
  UseNamespaceInboxes now binds the parent options so retirement is consulted LIVE. Locks:
  retirement manifest carries ZERO legacy-shared-topic references (TopologyManifestTests) and
  both provisioners perform ZERO management ops/declares on it (existence probes recorded and
  asserted); publish side: System kind + framework-reserved namespaces → `inbox.whizbang`
  even under retirement. Seam cleanup: `ICommandInboxAddressResolver`
  (DefaultCommandInboxAddress + ResolveFlippedCommandInboxAddress) implemented by BOTH
  SharedTopicOutboxStrategy (never flips) and NamespaceOutboxStrategy; TransportPublishStrategy's
  `namespaceRouting` seam retyped to the interface; both transports' SCE publish factories are
  strategy-agnostic (no `is` type tests) — three wiring cases (Shared/Namespace/neither)
  locked byte-identical by registration tests + a publish-shape byte-identity lock.
  Retirement E2Es: recording-double locks on BOTH transports
  (Asb/RabbitMQSharedInboxRetirementE2ELockTests: command → per-ns inbox, system command →
  broadcast, shared topic = ZERO sender/processor/publish/declare/binding/delivery ops),
  broker-tier shapes in both integration suites (ASB emulator has NO `inbox` entity — success
  proves nothing REQUIRES it; RMQ binds a match-all probe queue to a declared legacy `inbox`
  exchange and asserts depth 0), and the harness retirement test
  (PublisherFlipMigrationE2ETests: shared-inbox probe delivered to NOBODY, ns+broadcast land).
  Deprecation doc-notes (NOT `[Obsolete]` — CS0618 escalates): SharedTopicInboxStrategy,
  DomainTopicInboxStrategy, singular GetSubscription unified to "removal in v1.0, superseded
  by NamespaceInboxStrategy/GetSubscriptions". Phase-8 naming forward-compat locked in
  CommandInboxNamingTests: every manifest-emitted entity name is lowercase, dot-separated,
  `[a-z0-9_-]` per segment, ≤ 260−64 chars (class-decoration headroom) — tag→namespace
  routability asserted before traffic classes ship. Legacy shared-inbox test infra
  (MultiServiceHarness.SHARED_TOPIC default) left functional by design.
- **Phase 8 — tag-bound TransportNamespace routing** (#424 incr 2): `TagOptions.RouteNamespace`,
  `Transport.Namespaces` map, per-namespace clients + provisioning, `sys-` validation,
  single-namespace no-op guarantee.
- **Phase 8.5 — non-count-based poison detection (CLOSES A VALIDATED GAP, in-scope by owner
  decision).** The spike (phase 6) established, and a live Standard-namespace probe CONFIRMED:
  on SESSION-enabled entities, lock loss via connection death does NOT increment DeliveryCount
  (explicit abandon and non-session lock loss both do). Command inboxes are session-enabled by
  default, so the broker's MaxDeliveryCount valve — and the transport's MaxDeliveryAttempts
  branch, which reads the same counter — can NEVER fire under consumer-death storms. Messages
  are hostage, not poison, and nothing else bounds the loop. The arc must not ship a topology
  whose per-namespace DLQs are unreachable by the exact failure that motivated it.
  **Where it lives (design decision):** NOT on `ITransport` — that surface is publish/subscribe
  capability, and every implementation (incl. InProcessTransport and test doubles) would be
  forced to carry it. NOT transport-private either — the decision is identical everywhere and
  would drift. Follows the existing `IMessageDiscardPolicy` shape exactly: **decision in Core,
  execution in each transport.**
  - **Core** owns `IPoisonMessageDetector` + default impl: pure
    `PoisonVerdict Evaluate(PoisonEvaluationContext ctx)` over a transport-NEUTRAL context
    `{ MessageId, FirstEnqueuedAt: DateTimeOffset?, BrokerDeliveryCount: int?,
    DurableObservationCount: int?, Now }` → `Proceed` | `Quarantine(reason, detail)`. Threshold
    derivation, killswitch, bindable options, metrics and the health signal all live here. One
    decision, one place, both transports.
  - **Each transport** adapts its native message into the context and executes the verdict with
    its native mechanism: ASB reads `EnqueuedTime`/`DeliveryCount` and quarantines via
    `DeadLetterMessageAsync(reason, description)` — slotting into the existing
    `AsbReceiveDecisionMaker`/`AsbReceiveAction` seam rather than a parallel code path; RabbitMQ
    reads its timestamp/first-seen + `redelivered` and quarantines via
    `BasicNackAsync(requeue: false)` → DLX.
  - **Capability honesty:** age-based detection needs a broker-supplied first-enqueue timestamp.
    ASB always has one; RabbitMQ's is publisher-set and optional. The transport reports whether
    it can supply a trustworthy age; when it cannot, layer 1 degrades to layer 2 (durable
    counting) and says so in the health/log surface — it never goes silently inert.
  - Custom/third-party transports: the detector is an optional injected policy (null ⇒ today's
    behavior), same as the discard policy — no breaking change to `ITransport`.
  Layers:
  1. **Age-based quarantine at the receive boundary.** The broker's first-enqueue timestamp
     survives every redelivery. When `now - FirstEnqueuedAt > PoisonMessageAgeThreshold` the
     transport EXPLICITLY quarantines instead of waiting for a count that never rises. Default
     derived, not guessed: `MaxAutoLockRenewalDuration × MaxDeliveryAttempts` with a documented
     floor, so a legitimately slow-but-progressing message is never quarantined.
  2. **Durable observation counting** for poison that dies mid-processing rather than mid-lock:
     the inbox row already exists per message id (store-side idempotency) — increment a
     delivery-observation counter and quarantine past a bound. Reuses the existing dead-letter
     store + recovery flows; no new table if wh_inbox can carry it.
  Locks: quarantine fires on an aged session message (both transports); does NOT fire on a
  fresh message, on a slow-but-progressing one, or when disabled; the quarantined message lands
  in the per-namespace DLQ and is replayable by the existing recovery flow; the age default is
  derived from the lock/delivery options (property test, not a magic number).

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
