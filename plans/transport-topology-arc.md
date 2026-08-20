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
  STATUS 2026-08-20: DONE. Composed at the ITransport seam rather than making each transport
  multi-client internally: one transport instance per TransportNamespace behind a
  broker-agnostic `NamespaceRoutingTransport` in Core — so RabbitMQ got FULL parity (publish
  routing + consume mirroring + per-namespace provisioning), not the publish-only fallback the
  brief allowed. Each peer owns its senders/admin/acceptors, so per-namespace ops-rate
  governors structurally cannot count each other's slots (health reports the WORST namespace,
  never the sum — the threshold is a per-namespace budget). Single-namespace guarantee locked
  at descriptor level; unknown namespace key degrades to default, never drops. Config:
  Whizbang:Tags:RouteNamespace:<tag> and Whizbang:Transports:<T>:Namespaces:<key>, values
  naming ConnectionStrings entries. Defect found+fixed en route: two connections per RabbitMQ
  namespace (transport + provisioner) now share one. Known gaps (deliberate): readiness gates
  the default namespace only (peer gating risked startup deadlock); no emulator-backed
  multi-namespace integration test (the fixture exposes one namespace) — real two-namespace
  validation belongs with phases 9/10.

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
  **STATUS: implemented on feature/transport-topology (uncommitted).** Core owns the decision:
  `IPoisonMessageDetector` + `PoisonMessageDetector` over the transport-neutral
  `PoisonEvaluationContext { MessageId, FirstEnqueuedAt?, BrokerDeliveryCount?,
  DurableObservationCount?, Now }` → `PoisonVerdict` (`Proceed()` / `Quarantine(reason, detail)`),
  shaped on `IMessageDiscardPolicy` — optional injected policy, null ⇒ pre-8.5 behavior, NO new
  `ITransport` member. `PoisonMessageOptions` (killswitch `Enabled`, `AgeThreshold?`,
  `LockRenewalDuration?`, `MaxDeliveryAttempts?`, `AgeThresholdFloor`, `MaxDurableObservations`)
  bound from `Whizbang:Routing:PoisonMessages` via an explicit-key AOT binder; the derivation
  `max(floor, renewal x attempts)` is a pure static, matrix-property-locked (5x7x4 combos + the
  non-positive and overflow degenerates), and each transport post-configures its OWN knobs into
  it (ASB: MaxAutoLockRenewalDuration + MaxDeliveryAttempts; RMQ: delivery cap only — no
  per-delivery lock exists, so it supplies no renewal term). BROKER DELIVERY COUNT IS CARRIED BUT
  DELIBERATELY NOT ACTED ON by the default detector — it is the counter this phase stopped
  trusting. Layer 1 (age) executes at each transport's receive boundary: ASB reads `EnqueuedTime`
  and returns `AsbReceiveAction.DeadLetter` with reason `PoisonQuarantine` through the EXISTING
  `AsbReceiveDecisionMaker` seam (poison gate runs FIRST — pure broker metadata, and a hostage
  message may be perfectly well-formed); RMQ reads `BasicProperties.Timestamp` and
  `BasicNack(requeue: false)`s to the DLX. RMQ now STAMPS `Timestamp` on both publish paths — the
  broker sets none, so without it the signal would not exist on that transport at all. Layer 2
  (durable observations) executes in Core at the inbox store gate: `wh_message_deduplication`
  gained `observation_count` (mig **121** — the dedup write flips `ON CONFLICT DO NOTHING` to
  `DO UPDATE … + 1`; `store_inbox_messages`' SIGNATURE AND RETURNED ROWSET ARE UNCHANGED, still
  one row per newly-stored message, so `StoreInboxMessagesSqlTests`' dedup no-op lock still holds
  — an earlier attempt that returned duplicate rows broke it and was reverted). The counts are
  read from the dedup table as a SECOND statement in the SAME command (one round trip; the dedup
  row is written on every delivery anyway), surfaced by the
  `IWorkCoordinator.StoreInboxMessagesWithObservationsAsync` DIM (defaults to
  store-and-report-nothing) implemented on both Postgres coordinators over one shared SQL
  fragment, consumed by `TransportConsumerWorker` → `IDeadLetterStore.MoveAsync` (INBOX, new
  `MessageFailureReason.PoisonRedeliveryLoop`) = the EXISTING recovery flow.
  Capability honesty: `PoisonDetectionCapabilityState` + `PoisonDetectionHealthSource`
  ("poison-detection", Degraded) + a one-shot WARNING per surface; reported on CHANGE only so the
  hot path pays one lock-free lookup. Locks: Core matrix/derivation/killswitch/capability tests;
  transport-level aged-quarantines / fresh-proceeds / slow-but-progressing-proceeds /
  disabled-proceeds / no-detector-proceeds on BOTH transports; broker-tier integration on BOTH
  (ASB: aged SESSION message → entity DLQ with `DeliveryCount == 1`, i.e. quarantined where every
  count-based valve is provably inert; RMQ: aged → real `.dlq`, plus a foreign publish with no
  timestamp that must NOT quarantine and MUST degrade visibly).

- **Phase 9 — control class semantics** (#424 incr 3): `sys-control` tag, TTL≈2×cadence
  minting via `mint.Checkpoints`, sessionless subscriptions, non-durable receive path.
  Decide the `whizbang.core.commands.system` vs `whizbang.core.messaging` split here.
  **STATUS: implemented on feature/transport-topology (uncommitted).**
  THE SPLIT (the open decision, now locked): `IControlPlaneMessage` is a SECURITY + no-DLQ marker,
  NOT a traffic class — the two sets deliberately differ. `whizbang.core.commands.system`
  (durable system commands: run-control, killswitches, rebuild/reseed) stays on the phase-7
  BROADCAST inbox with sessions and no TTL: it is one-shot operator intent a lifetime would
  silently discard. `whizbang.core.minting` (composite envelopes) also stays — wire-only wrappers
  around DURABLE payload. `whizbang.core.messaging` (integrity checkpoints/manifests/gap+divergence
  reports, redelivery + manifest requests) IS the class: every member is re-derived on the next
  cadence. `RebuildPerspectiveCommand` is the trap the locks name explicitly — it carries the
  marker and must NOT join the class. Signal probes are out of scope by construction: they ride
  `ISignalTransport` (Postgres NOTIFY / in-memory), never the broker.
  Membership carrier: `SystemControlTagAttribute : MessageTagAttribute` applied
  `[SystemControlTag(Tag = SystemTags.CONTROL, Properties = [])]` (the SystemAuditTagAttribute
  idiom — explicit syntactic tag, no hook payload), `SystemTags.CONTROL = "sys-control"` +
  `IsFrameworkTag`, so `RouteNamespace("sys-control", …)` now passes reserved-prefix validation.
  TTL: `ControlClassOptions` (bindable `Whizbang:Routing:ControlClass`) with the pure static
  `DeriveTimeToLive(cadence, multiplier, floor) = max(floor, cadence × multiplier)` — matrix-locked
  (7 combos + non-positive cadence/multiplier/floor + overflow-saturates), defaults 2× and a 30s
  floor, per-call and per-options overrides. `ICheckpointMint.Mint` (the phase-4 placeholder's
  first real implementation) applies it at construction; `ControlMessageTtl` (shaped exactly on
  `TransportNamespaces`) is the destination rail; each transport LIFTS the key into
  `ServiceBusMessage.TimeToLive` / `BasicProperties.Expiration` and REMOVES it from the metadata
  bag (unlifted it would land in ApplicationProperties/Headers as inert decoration). First
  consumer: `IntegrityCheckpointWorker`, cadence = `CheckpointIntervalSeconds`, stamped AFTER
  `ControlPlaneDestination.WithSession` (which replaces the metadata bag wholesale) so the session
  key survives.
  SESSIONLESS (opt-in `ControlClassOptions.SessionlessSubscriptions`): the broadcast subscription
  SPLITS — `inbox.whizbang.control` carries `whizbang.core.messaging.#` and is marked
  `ControlClassSubscription`; `inbox.whizbang` keeps system-command + minting patterns. The two
  pattern sets PARTITION the original (completeness + no-overlap locked). Provisioners read the
  marker: ASB `requiresSession: false` (can only ever REMOVE sessions), RMQ omits
  `x-single-active-consumer` while keeping the DLX. PHASE-8.5 INTERACTION, asserted: a sessionless
  entity's DeliveryCount DOES rise under lock loss, so the broker's own MaxDeliveryCount valve
  works for this class — the age-based detector is its BACKSTOP, not its only defence.
  NON-DURABLE RECEIVE (opt-in `NonDurableReceive`): `TransportConsumerWorker` gates per MESSAGE on
  `ControlClassResolver` (name-keyed over the tag registry, the `TransportNamespaceResolver` idiom;
  unresolvable ⇒ NOT control, fail-safe toward durability) and runs receive → compare → discard:
  the class's receptors fire inline at `PostInboxInline` (the SAME stage the durable path fires, so
  control receptors are unchanged), no inbox row, no completion bookkeeping, and a failed
  comparison is swallowed — rethrowing would abandon the broker message, i.e. redelivery.
  Both migration steps are OPT-IN like phases 5/6/7; TTL minting is ON by default (its only members
  have a successor already scheduled). Killswitch yields the pre-phase-9 wire shape exactly.
- **Phase 10 — backlog-age duty + OTel** (#424 incr 4b+5).
  **STATUS: implemented on feature/transport-topology (uncommitted).**
  DUTY: no scheduling abstraction existed to reuse (`IDutyElector` is leader ELECTION, not
  scheduling), so `BacklogAgeWorker` follows `TableStatisticsCollector` — periodic
  `BackgroundService` refreshing gauge caches with a public `PeekOnceAsync` as the deterministic
  test seam — and is per-instance by design (each instance observes what IT consumes from).
  `IBacklogPeek` is optional+injected (the `IMessageDiscardPolicy` / `IPoisonMessageDetector`
  shape — nothing added to `ITransport`). `BacklogAgeOptions` (Enabled, Interval 1m, AgeThreshold
  15m) → `BacklogAgeState` → `BacklogAgeHealthSource` ("backlog", Degraded, ENTITY NAMED).
  Findings are REPLACED per tick so the signal goes DOWN on heal. AGE, not depth, is the
  discriminator: the incident's 16,642-message backlog was hostage, not poison. ASB supplies depth
  (admin plane) + age (one head peek, which neither locks nor settles nor counts against delivery),
  walking the liveness watchdog's live-entity registry and fanning over namespace peers; RMQ
  supplies depth (passive declare, dedicated channel per probe) and reports NO age — AMQP cannot
  read the head timestamp without a get-and-requeue that would mark messages redelivered every
  minute, corrupting the counters the poison detector reads. That gap is surfaced
  (`HasUnknownAgeSurface`), never silently inert — the phase-8.5 capability-honesty rule.
  OTEL: new Core meter `Whizbang.TrafficClasses` (registered in `WhizbangMeters`, drift-locked)
  with `whizbang.traffic_class.backlog_depth`, `whizbang.traffic_class.backlog_age_seconds`,
  `whizbang.traffic_class.ops_rate` — ObservableGauges over caches, every instrument tagged
  `transport` / `transport_namespace` / `traffic_class` (+ `entity` for backlog). Ops-rate is fed
  by `ITrafficClassOpsRateSource` (ASB publishes its existing idle projection per namespace, never
  summed — each pool is its own budget). Throttle counters: the EXISTING
  `whizbang.transport.outbox.publish_throttled` gained a `transport_namespace` tag at both publish
  sites, so an operator can name WHICH credit pool is exhausted.
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
