# Plan: System Management Surface (full arc, P1 to P5)

**Status:** design reviewed and decided 2026-09-05, drift re-checked 2026-09-18. Execution not
started. This file supersedes the earlier P1-only hand-off.

**Proposal:** docs repo, branch `origin/docs/dlq-integrity-and-meter-subscription`,
`src/assets/docs/proposals/system-management-surface.md` (commit `fadc4f99`). The proposal
still says what it said on 2026-09-05; Cluster J amends it to match this plan.

**Delivery shape (maintainer decision):** ONE library PR on `feat/dlq-system-commands` to
`develop`, built as ordered commit clusters A to J, committing often. Every cluster leaves the
branch green, so the PR can be cut early if priorities shift. The docs repo change is a second
PR (separate repo, targets `main`).

---

## Start here

1. Work in the worktree `.worktrees/probe-budget` (branch `feat/dlq-system-commands`). Never
   switch branches in the main checkout.
2. **Rebase first.** On 2026-09-18 the branch was 2 ahead and 568 behind `origin/develop`.
   Expected conflicts: `IDeadLetterRecoveryService.cs`, `EFCoreDeadLetterRecoveryService.cs`,
   `PostgresDriverExtensions.cs` (develop added `MarkDiscardedAsync` and
   `GetPassedCampaignFingerprintsAsync` to the interface and grew the registration block).
3. Re-verify the drift-prone facts below before relying on them: migration head (163 on
   2026-09-18, so new migrations start at **164**), the registrar block in
   `PostgresDriverExtensions.cs`, and whether anything in the list under "Grounding facts"
   changed on develop.
4. Read the quality bar at the bottom. It is not optional.

---

## Why

Operating a Whizbang fleet today means psql and kubectl for every incident. The framework has
the data and the machinery but no first-class management surface. The proposal defines four
layers: per-service queries (11 domains), system commands, fleet scatter-gather, and
host-flavor adapters. A robustness review against the code found it directionally right but
not robust as written. This plan is the hardened version, plus three additions the maintainer
asked for: a management capacity, a direct management channel, and an embedded telemetry
stack, all packaged so that only management services carry the weight.

---

## Grounding facts (verified in code)

These drove the design. Each one is a place the original proposal was wrong, silent, or
optimistic.

1. **Broadcast reaches one replica per service.** `MessageKind.System` publishes to topic
   `inbox.whizbang`; every service has ONE broker subscription (competing consumers), then
   the `wh_inbox` claim and `wh_message_deduplication` collapse duplicates. A broadcast fleet
   action executes once per service by construction. No extra dedupe is needed.
2. **Directed replies land on ANY replica of the asker.** `IMessageEnvelope.Target` is
   service-scoped and there is no per-instance broker rail (the only per-instance rail is the
   payload-less Postgres NOTIFY signal bus). An in-process `CollectAsync` gather is broken on
   a multi-replica asker, so collection must be database-mediated.
3. **A request to directed-reply precedent exists:** `RequestIntegrityManifest
   { RequesterService, Topic }` answered by `IntegrityManifest` with `Target = RequesterService`,
   sent through `ControlPlaneDestination.For(...)`
   (`src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs`). Reuse it; no new reply
   transport is needed on the fabric.
4. **Discard-unsubscribed drops silently.** `TransportConsumerWorker` drops any type with no
   registered receptor (`HasAnyConsumer`) at Debug. A service missing the fleet receptor
   looks exactly like a dead service. Every receptor must be turnkey-registered AND covered
   by a registration-assertion test (three prior silent DI-strip incidents).
5. **`IControlPlaneMessage` means "accept with no caller context"**
   (`DefaultMessageSecurityContextProvider`). There is no dispatch-side authorization
   anywhere. This was the sharpest gap.
6. **Namespace truth.** The routing constants are `whizbang.core.commands.system` and
   `whizbang.core.messaging` (`SharedTopicInboxStrategy`). The proposal's
   `whizbang.system.commands` is a stale string (also stale in the `SystemCommands.cs` doc
   comment). New fleet records must live in CLR namespace `Whizbang.Core.Messaging`, because
   the subject is derived from the CLR namespace and anything else fails the broker filter
   with no log.
7. **The TTL split is deliberate.** `[SystemControlTag]` (TTL floor 30s, optional
   sessionless and non-durable receive) belongs on stale-worthless queries and responses, and
   deliberately NOT on durable one-shot operator commands. P1's Release and Scan commands
   correctly lack it. Document this; do not "fix" it.
8. **Control-plane types are dropped at the dead-letter boundary** (`DeadLetterDropPolicy`).
   A Release command whose processing dead-letters is logged and metered but not stored. The
   docs must say "re-issue on no effect"; the acknowledgement in Cluster E is the real remedy.
9. **Five of the seven "existing" system commands are vestigial.** `DiagnosticsCommand`,
   `ClearCacheCommand`, `PauseProcessingCommand`, `ResumeProcessingCommand`,
   `CancelPerspectiveRebuildCommand` have no receptor anywhere. Only `RebuildPerspectiveCommand`
   (and `RequestRedeliveryCommand`, which lives in `Messaging/Redelivery.cs`) are real.
10. **The roster problem is real.** `wh_service_instances` is per-service database state:
    each service sees only its own instances, stale rows are deleted rather than marked, and
    there is no fleet-wide directory. `IStartupFleetStatusSource.GetFleetAsync` is the
    per-service read model to build on.
11. **The capability/duty system exists and is coherent, but narrow.** `IDutyElector` /
    `PgDutyElector`, `wh_instance_capabilities` (migration 108; rule: "the lock decides, the
    row reports"), duty-aware startup steps. As of 2026-09-18: `maintainer` gates table
    rewrites; `migrator` is now wired through `MigratorDutyStaging` (see Cluster D);
    `StandbyHandshake` still has no production caller. The code avoids the word "Role"
    because `Whizbang.Core.Security.Role` already exists for access control.
12. **Storm guards exist per message family, none on a fleet layer.** Precedents to copy:
    `MaxDigestsPerManifest = 500`, `MaxManifestPagesPerAudit = 8`, origin-side clamps in
    `RedeliveryPumpOptions`. ASB caps messages at 256KB; body offload starts at 64KB. Two
    control-plane OOM incidents are documented in code comments. No dispatch rate limit
    exists.
13. **AOT-safe polymorphic payloads** exist only for `IMessage`/`IEvent`/`ICommand`, a closed
    `[JsonPolymorphic]` hierarchy (`CollectiveScope` precedent), or `JsonElement` plus a
    kind-keyed `JsonTypeInfo<T>` lookup. `AllowOutOfOrderMetadataProperties` is already set
    globally.
14. **Dependencies are further along than the proposal assumed.** Canary recovery and the
    relational stack layer are merged (`wh_stacks`, `wh_stack_links`, `wh_stack_daily` with
    `total_occurrences` / `last_seen`). StackAnalytics has its data source today. Issue #679
    (perspective hard-wedge: leases renew, nothing completes, no errors) was still open on
    2026-09-05, so the stall detector is detection for a live failure mode.

### P1 state on this branch (commit `5f01230fe`)

- `ReleaseHeldDeadLettersCommand(string? Fingerprint = null, int StaggerMinutes = 30)` and
  `RequestDeadLetterScanCommand(string? Generation = null)` in
  `src/Whizbang.Core/Commands/System/SystemCommands.cs`: `[PinnedId]`, `ICommand`,
  `IControlPlaneMessage`, registered in `ControlPlaneTypeRegistry`, ledger and generator
  cache committed.
- `DeadLetterStatusSummary` / `CampaignStatus` records and `GetStatusSummaryAsync` on
  `IDeadLetterRecoveryService`; EFCore implementation counts Pending/Recovering/Held gated on
  `recovered_at IS NULL`, Recovered as `recovered_at IS NOT NULL`, PermanentlyFailed ungated,
  held cohorts via `list_held_dead_letter_cohorts()`, last 20 campaigns from
  `wh_dlq_probe_campaigns`.
- **The hand-off's "compiles" claim covered `src/` only.** Four test fakes do not implement
  `GetStatusSummaryAsync`, so the test projects do not compile:
  `tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryWorkerTests.cs` (FakeRecoveryService),
  `tests/Whizbang.Core.Tests/Workers/DeadLetterCanaryCampaignTests.cs` (CampaignFake,
  FailingRecoveryFake), `tests/Whizbang.Hosting.AspNet.Tests/DeadLetterOperatorEndpointsTests.cs`
  (FakeRecoveryService).
- Docs ahead of truth: `<tests>` tags point at files that do not exist
  (`DeadLetterOperationsReceptorTests.cs`, `DlqStatusSummarySqlTests.cs`, new
  `SystemCommandsTests` methods), and the `DeadLetterStatusSummary` doc comment claims a
  shipped `GET /whizbang/dlq/status` endpoint that does not exist.
- HTTP today: `MapWhizbangDeadLetterEndpoints` has 7 routes, no authorization anywhere (the
  convention is "caller chains `.RequireAuthorization()`"), no `/status` route, and
  `DeadLetterOperatorJsonContext` lacks the summary records.

---

## Decisions (maintainer, 2026-09-05)

| Topic | Decision |
|---|---|
| System-command authorization | **Uniform scope claim.** Every `Whizbang.Core.Commands.System` command requires a configured sys-admin scope claim on the envelope, checked at receptor admission. Internal control types in `Whizbang.Core.Messaging` (integrity, redelivery) stay infra-trust, so no internal path breaks. |
| Who may stamp the claim | **Management capacity, config-declared per service** (not elected). Every replica of a management service may stamp, so operator HTTP served by any replica can dispatch. Recorded in `wh_instance_capabilities` for visibility only. |
| Packaging | **New NuGet package `Whizbang.Management`.** Installing and registering it IS the management-capacity declaration. Core carries shared contracts plus the thin channel client; the driver carries only responder machinery every service needs. |
| Roster for "who did not answer" | **Learned only** on the fabric path, with staleness marking; "roster empty" is a stated condition, never an empty success. On channel-connected fleets, connection state becomes the authoritative roster. |
| Reply transport (fabric) | Asker's normal inbox with `Target` plus correlation (grounding fact 3). |
| Settings writes | **Per-service only** (`Target` required, no broadcast form); validate against known keys; audit the old value; read back through EffectiveSettings. |
| Lens for dead-letter status | **No** perspective-backed read model. It would duplicate truth the tables hold, add write amplification on the dead-letter path, and lag during exactly the storms it exists to observe. |
| StackAnalytics | **Pulled forward** to Cluster B. |
| Migration ownership | **Wire it fully**: migrator duty (now done on develop) plus a production caller for `StandbyHandshake`. |
| Direct management channel | **Own cluster in this PR.** Instances dial out; WebSocket duplex carrying the existing JSON envelopes (no gRPC, no second serialization world); ephemeral and live traffic only; durable operator commands stay on the fabric. |
| Telemetry | **The management service can host the whole stack itself**: ingest the fleet's OTLP, keep all of it in its own store, serve its own Grafana, and re-broadcast a stripped stream upstream. A stock OTel Collector sidecar stays the documented alternative. |

---

## Package boundaries

| Where | What lives there |
|---|---|
| `Whizbang.Core` | Records and contracts: Layer-1 read records, fleet messages (`FleetQueryRequest` / `FleetQueryResponse`), seams (`IFleetQueryProvider`, `IFleetRosterSource`, `IFleetStatusCollector`, stores), `ManagementOptions`, channel frame contracts, the channel CLIENT worker (every service dials out when an endpoint is configured), scope-claim admission guard. |
| `Whizbang.Data.EFCore.Postgres` | Responder side every service needs: Layer-1 SQL queries, `IFleetQueryProvider` implementations, fleet request receptor and its registrar, system-command receptors, audit writes. |
| `Whizbang.Hosting.AspNet`, `Whizbang.Transports.HotChocolate`, `Whizbang.Transports.FastEndpoints` | A service's LOCAL surface over its own Layer-1 data (policy required). |
| `Whizbang.Management` (new) | Everything only a management service needs: collector and stores, fleet response receptor, learned and connection-backed rosters, channel server, fleet HTTP surface, scope-stamping dispatch helper, capability recording, embedded telemetry ingestion, Grafana store, stripped re-export. Owns its own JSON context. |

---

## Cluster A: finish P1 (dead-letter commands and status)

1. Rebase (see "Start here"), then restore compile: add `GetStatusSummaryAsync` to the four
   fakes. Recommended alternative, decide at implementation: move the read side
   (`GetStatusSummaryAsync`, and Cluster B's stack analytics) to a sibling read interface
   such as `IDeadLetterInsights`, which removes the fakes ripple entirely and stops
   `IDeadLetterRecoveryService` growing past its current ~19 members.
2. RED: `SystemCommandsTests` (create and serialize both commands) and
   `ControlPlaneTypeRegistryTests` (membership).
3. RED: `DeadLetterOperationsReceptorTests` (EFCore.Postgres.Tests, capturing fake): named
   cohort released via `ReleaseHeldCohortAsync(fingerprint, TimeSpan)`; null fingerprint
   fans out over `ListHeldCohortsAsync`; scan calls
   `ResetForGenerationAsync(generation ?? IGenerationProvider.GetGeneration(), 0)`.
4. RED: `DlqStatusSummarySqlTests` [Integration]: seed every status plus campaigns; assert the
   `recovered_at` gating semantics.
5. RED then GREEN: `GET /whizbang/dlq/status` in `DeadLetterOperatorEndpointsTests`, route in
   `DeadLetterOperatorEndpoints.cs`, records added to `DeadLetterOperatorJsonContext`.
6. Implement `DeadLetterOperationsReceptor` and `DeadLetterOperationsReceptorRegistrar` in
   `src/Whizbang.Data.EFCore.Postgres/`, copying `RedeliveryRequestReceptorRegistrar`
   (optional `IReceptorRegistry`, three stages: `LocalImmediateInline`, `PreOutboxInline`,
   `PostInboxInline`). Register next to the sibling registrars in `PostgresDriverExtensions`.
   Add the registration-assertion test (extend
   `PostgresDriverExtensions_DeadLetterRegistrationTests`, which asserts no registrars today).
   Receptor pattern: `ArgumentNullException.ThrowIfNull`, scoped services per invocation via
   `CreateAsyncScope`, `[LoggerMessage]` logging.
7. Boy scout in touched files: stale `whizbang.system.commands` doc strings; the "five routes"
   class doc (seven are mapped); the orphaned duplicate `<summary>` on `CohortReleaseResult`.
8. Library docs: canary-recovery page gains both commands, the status endpoint, and the
   "re-issue on no effect" note (grounding fact 8).

## Cluster B: StackAnalytics query

- RED: SQL tests over `wh_stacks` / `wh_stack_daily` / `wh_stack_links`: top stacks by
  `total_occurrences`, first and last seen, daily trend window, blast radius by frame.
- Records plus the query on the read interface chosen in Cluster A.
- `GET /whizbang/dlq/stacks`, JSON context, endpoint tests.

## Cluster C: management capacity and system-command authorization

Lands before any destructive command (Cluster H) and gates P1's commands in the same PR, so no
released surface is ever open.

**Capacity (config-declared):**
- `ManagementOptions` (turnkey-bound, `Whizbang:Management`): `RequiredScope` (default
  `whizbang:sys-admin`), `Endpoint` (the channel endpoint services dial; setting it on an
  ordinary service turns on the client worker), `TelemetryEndpoint` (Cluster I).
- A `management` capability constant next to `StartupCapabilities.EVERY_INSTANCE`
  (`src/Whizbang.Core/Startup/StartupStepDescriptor.cs`). Declared, not elected.
- The package records it via `record_capability` for visibility. `record_capability` returns
  FALSE for an instance not yet in `wh_service_instances`, so record AFTER the first
  successful heartbeat, and treat a refusal as a logged, non-fatal event (the row only
  reports; the same "no answer is fatal" rule `MigratorDutyStaging` follows). Release on
  graceful shutdown.
- Only a host that registered `Whizbang.Management` can stamp the scope (the helper lives in
  the package), host the channel server, or expose the fleet surface.

**Scope-claim admission (what receivers check):**
- One shared admission guard that every `Commands.System` receptor calls first, or an
  interceptor in the receptor invocation path if `DefaultRequirePermissionInterceptor`'s shape
  fits (decide at implementation). The claim rides the hop `ScopeDelta`.
- Adapters (HTTP, GraphQL, FastEndpoints, channel) stamp the claim only after their own auth
  policy passes.
- **Trust boundary, documented honestly:** a receiver cannot verify the sender's management
  capacity (capability rows live in the sender's database). The capacity governs who may
  STAMP; the claim is what receivers CHECK; on the fabric the claim is asserted, not
  authenticated. It stops accidents and misrouted intent, not a compromised peer. The channel
  (Cluster F) is where identity becomes verifiable.
- Rejection: warning log naming WHO (hop `ServiceInstanceInfo`) and WHAT (command type), meter
  `whizbang.system_commands.rejected{command,reason}`, message completed and not retried.
  Never silent.
- RED first: claim present, absent, wrong; the rejection log asserted (not just "does not
  throw"); option binding; capability recording including the refusal path. Then route
  `RebuildPerspectiveCommand` and P1's receptors through the guard.
- Docs: `operations/startup/capabilities-and-duties.md` gains the management capacity and a
  short taxonomy: elected duties (the lock decides), declared capacities (config decides),
  both reported through `wh_instance_capabilities`.

## Cluster D: migration ownership (StandbyHandshake production caller)

**Already done on develop** (commits `29d9cfb61`, `c0fb16936`): `MigratorDutyStaging`
(`src/Whizbang.Data.Postgres/MigratorDutyStaging.cs`) stages each instance as `Migrator`,
`Waiter` or `Unstaged` after a bootstrap region makes election possible. No elector answer is
fatal: `Unstaged` migrates under the advisory lock exactly as before. Docs:
`operations/infrastructure/migrations#which-instance-migrates`. Traps are recorded in
`ai-docs/schema-initialization-connections.md` (never a session advisory lock on the
schema-init key through a transaction pooler; reassemble `pg_locks` keys from classid/objid;
`Refused` usually means "not heartbeated yet").

**Remaining:**
1. Reconcile docs: `capabilities-and-duties.md` and `startup-pipeline.md` claimed the Migrate
   STEP requires the migrator duty; the real mechanism is staging inside schema
   initialization. Make the docs describe what shipped.
2. Give `StandbyHandshake` its production caller: the `Migrator`-staged instance, when the
   assessment says the migration is breaking, calls `RequestAsync`, then
   `AwaitPeersStandingByAsync` (peers already run the wired `StandbyWatcher` and move to
   `LifecyclePhase.StandingBy`), evicts unresponsive peers with `EvictUnresponsivePeerAsync`,
   applies, then releases. Keyed on the `SchemaStaging.Grant`.
3. Same rule as staging: no handshake outcome may leave the fleet never migrating. Bounded
   waits, eviction for silent peers, and every degraded path logs WHAT happened and the
   CONSEQUENCE.
4. RED: choreography with fake peers driven by completion signals (no timing), the eviction
   path, an `Unstaged` instance never attempting a handshake, and a journey test in the
   `DutyElectionE2ETests` style.
5. This is the highest-blast-radius cluster (startup of every host). The journey and E2E
   suites must pass before moving on.

## Cluster E: fleet scatter-gather on the fabric (P2)

**Messages** (`src/Whizbang.Core/Messaging/FleetQuery.cs`, namespace `Whizbang.Core.Messaging`):

```csharp
[PinnedId("<new>")] [SystemControlTag(Tag = SystemTags.CONTROL, Properties = [])] [Ephemeral]
public sealed record FleetQueryRequest : ICommand, IControlPlaneMessage {
  public required Guid CorrelationId { get; init; }
  public required string QueryKind { get; init; }        // open set; constants in FleetQueryKinds
  public required string RequesterService { get; init; } // becomes the replies' Target
  public required string ReplyTo { get; init; }          // a topic the asker already consumes
  public JsonElement? Filter { get; init; }
  public int? MaxPayloadBytes { get; init; }             // asker may lower, never raise, the cap
}

[PinnedId("<new>")] [SystemControlTag(Tag = SystemTags.CONTROL, Properties = [])] [Ephemeral]
public sealed record FleetQueryResponse : IEvent, IControlPlaneMessage {
  [StreamId] public required Guid CorrelationId { get; init; }
  public required string QueryKind { get; init; }
  public required string ServiceName { get; init; }
  public required Guid InstanceId { get; init; }
  public long? Generation { get; init; }
  public required JsonElement Payload { get; init; }
  public bool Truncated { get; init; }
  public string? Error { get; init; }                    // "alive but cannot answer" is not silence
}
```

Both registered in `ControlPlaneTypeRegistry` and the pinned-type ledger. Payload is
`JsonElement` resolved by kind through `JsonTypeInfo<T>` from the injected options (AOT-safe;
the kind set spans assemblies, so a closed hierarchy cannot name them).

**Corrected public shape** (the proposal's was wrong on three counts):
`Task<FleetView<T>> CollectAsync<T>(string queryKind, FleetQueryOptions? options = null,
CancellationToken ct = default)`; `FleetView<T>` carries `Responses`, `Missing`, and a
tri-state `Completeness { Unknown, Complete, Partial }` (a bool `Partial` is a lie without a
roster). "Every service answers" means one replica per service; `InstanceId` says which.

**Database-mediated collection:** migration `164_FleetQueryResponses.sql` (re-verify the
number): `wh_fleet_collects (correlation_id PK, query_kind, filter, requested_at,
window_closes_at, requested_by_instance)` and `wh_fleet_responses (correlation_id,
service_name, instance_id, query_kind, generation, payload jsonb, truncated, error,
received_at, PK (correlation_id, service_name))`. The upsert on that key absorbs duplicate and
late answers. The tables ride the shared migration chain and sit empty on ordinary services
(revisit package-owned schema at implementation if that is objectionable).

**Collector (package side):**
1. Clamp the window (1s to 30s; never longer than the 30s control TTL floor).
2. Under `pg_advisory_xact_lock` on the kind: join an open collect for the same kind
   (coalescing), or serve the stored aggregate if the newest collect is younger than
   `MinCollectIntervalPerKind`, or insert a new collect and opportunistically delete
   collects older than `CollectRetention` (no new sweeper worker).
3. Broadcast with no `Target`, following the `IntegrityAuditWorker` publish pattern
   (`ControlPlaneHop.Create`, `ControlPlaneDestination.For`). Reuse the existing publish-time
   choice between `SystemBroadcastTopic` and `ControlBroadcastTopic`; do not re-decide it.
4. Subscribe to `FleetResponseRecordedSignal` BEFORE the first read; wake on that doorbell or
   on a `TimeProvider` window timer (the timer is the correctness backstop, the doorbell is
   best-effort).
5. Close early when a roster says everyone answered; otherwise build `FleetView<T>` at window
   close.

**Receptors:** the request receptor (driver, every service) enumerates DI
`IFleetQueryProvider`s, answers through a queueing `SemaphoreSlim(1,1)` (the
`IntegrityManifestRequestReceptor` precedent), returns `Error` for an unknown kind or a
provider failure, truncates to the cap and sets `Truncated`. It must log WHAT and CONSEQUENCE
itself, because non-durable receive swallows failures by design. The response receptor
(package) upserts and rings the doorbell; unknown or expired correlations are dropped at
Debug. Built-in providers: dead-letter status (Cluster A) and stacks (Cluster B).

**Caps (`FleetQueryOptions`):** `CollectWindow` 5s, `MinCollectIntervalPerKind` 5s,
`MaxResponsePayloadBytes` 32KB (responder-side clamp, under the 64KB offload threshold),
`MaxResponsesPerCollect` 500 (stops reply storms reaching the asker's database),
`CollectRetention` 10 min.

**Learned roster:** last-seen responders persisted per kind; `Missing` = seen before, silent
now, marked with staleness age. First collect reports "roster empty" as a stated condition.
Blind spot, documented: a service that never answered is invisible (mitigated by the
registration guard tests, and removed entirely by the channel roster in Cluster F).

**Acknowledged fleet actions:** Release and Scan gain optional `CorrelationId`,
`RequesterService`, `ReplyTo` (safe: unreleased). When present, the receptor publishes a
`FleetQueryResponse` acknowledgement (`{ "released": N }`) on the same rail. The command stays
durable and untagged; the acknowledgement is stale-worthless, so the split is principled. The
collect row is created before the durable command is dispatched.

**Meters:** `whizbang.fleet.{collects,responses,missing,truncated,timeouts}`.

**Tests (no timing tests anywhere):**
- Units: message round-trip through the generated context; the derived subject starts with
  `whizbang.core.messaging.` (locks the broker-filter trap); registry membership;
  `FleetView` aggregation; option clamps; collector with fake stores and `FakeTimeProvider`
  (coalescing publishes once, min-interval publishes zero, window close on time advance,
  early close on doorbell).
- Driver: receptor targeting, truncation, error paths; SQL upsert, advisory-lock single
  winner, retention delete; registration guard for every registrar.
- E2E on the `MultiServiceHarness` (real wire bytes, real `TransportConsumerWorker` per
  service): three services, responses from all, non-askers' inboxes empty (Target discard
  asserted), `Missing` via `SuppressDeliveries`. Note in the test file: the harness models
  neither broker TTL nor namespace filters (covered by the unit tests above).

**Failure modes:**

| Failure | Behavior |
|---|---|
| Request lost or expired | No row for that service: `Missing` (roster) or `Unknown`. No retry; operator re-collects. |
| Response lost | Same, for that service. No durable residue. |
| Asker replica dies mid-collect | Collect and rows persist in the shared database; a retry joins the open collect or reads the stored aggregate. No re-broadcast storm. |
| Service lacks the receptor | Dropped at receive: permanent `Missing`. Guarded by registration tests; the channel roster exposes it. |
| Oversized answer | Truncated with `Truncated = true`; grossly over-cap rows refused. |
| Late answer | Upserted until retention; a joining collect sees it; afterwards dropped at Debug. |
| Duplicate answer | Upsert on `(correlation_id, service_name)`. |
| Reply storm | Row cap, payload cap, coalescing, min-interval, non-durable receive. |
| Alive but cannot answer | `Error = "unsupported:<kind>"`, distinguishable from down. |

## Cluster F: `Whizbang.Management` package and the management channel

New project `src/Whizbang.Management/` plus `tests/Whizbang.Management.Tests/`, packable like
the transports packages, owning its JSON context (module-initializer registration).

1. **Bootstrap:** `AddWhizbangManagement(...)` wires the collector, stores, response receptor
   and registrar, capability recording, and the scope-stamping helper. Registration-assertion
   tests for every piece.
2. **Channel contracts (Core):** a small frame protocol over WebSocket: HELLO (token,
   `ServiceInstanceInfo`, generation), heartbeat, and typed frames carrying existing envelope
   JSON (`FleetQueryRequest` / `FleetQueryResponse` and the Layer-1 records ride unchanged
   through the generated contexts).
3. **Channel client (Core):** on when `Whizbang:Management:Endpoint` is set. Dials out,
   authenticates, answers channel-delivered queries through the same `IFleetQueryProvider`s,
   streams nothing unless asked. It must never affect the service's health: backoff reconnect,
   bounded drop-oldest send buffer, every drop logged and metered. RED with a fake server,
   completion signals, `FakeTimeProvider` for backoff.
4. **Channel server (package):** `MapWhizbangManagementChannel(policy)` (policy required), token
   auth, connection registry keyed by (service, instance). Token identity is verifiable, which
   the fabric claim is not.
5. **Connection-backed roster:** `ConnectionFleetRosterSource : IFleetRosterSource`. Connection
   state IS the roster: per instance, live. The learned roster remains the fallback for
   services not connected. The collector asks connected instances over the channel and falls
   back to the fabric broadcast for the rest; both paths produce the same `FleetView<T>`
   (exact precedence decided at implementation).
6. **Per-instance reach:** channel queries can target one instance, the rail the fabric lacks.
   Durable operator commands still go through the fabric only. Documented split: channel =
   ephemeral and live, fabric = durable intent.
7. Keep the channel deliberately narrow (management frames only). It must never grow into a
   general transport.

## Cluster G: host adapters (P3)

- **Local surface** (a service's own data, existing packages): AspNet
  `MapWhizbangSystemApi(this IEndpointRouteBuilder, string policy, string prefix =
  "/whizbang/system")` with the policy REQUIRED. This is a deliberate break from the three
  existing `Map*` surfaces (which stay as they are, documented): management must never ship
  open. HotChocolate `AddWhizbangSystemGraph(policy)` (`AddWhizbangStartupStatus` and
  `GraphQLMutationBase` patterns); FastEndpoints `AddWhizbangSystemEndpoints(policy)`
  (`WhizbangStartupStatusEndpointBase` pattern). All three bind the same records through one
  shared reporter so they cannot drift in what they disclose.
- **Fleet surface** (`/whizbang/management/fleet/{kind}` plus command dispatch with scope
  stamping): in `Whizbang.Management` only. GraphQL/FastEndpoints fleet adapters only as thin
  package-side bases if wanted; do not drag those dependencies into ordinary services.

## Cluster H: remaining domains, destructive commands, audit (P4)

**Data domains**, one commit each, same pattern as dead-letter status (records, SQL, provider,
local endpoint, tests):
- QueueDepths: inbox and outbox pending, claimable, leased, parked; oldest ages; top-N streams.
- WorkerPosture: claim and drain state.
- IntegrityPosture: frontier lag, stale origins, repair budget.
- PerspectivePosture: cursors, lag, rebuilds, and the **#679 stall detector** (leases renewing,
  zero completions). The detector also emits a meter (`whizbang.perspectives.stalled`) and an
  optional signal: detection that requires someone to ask is not detection.
- InstanceRoster: projection of `IStartupFleetStatusSource.GetFleetAsync`, including held
  capabilities (who holds management, maintainer, migrator).
- CoalescePosture, TemporalPosture.
- EffectiveSettings: `wh_settings` values AND the bound option values in effect, each with a
  source marker (default, configuration, override). Ends "is the flag really on?" forensics.

**Audit first:** migration for `wh_system_audit` (who: claim plus hop identity; what; scope;
old value; rows affected; when). Every system-command receptor writes it, including a
retrofit of Cluster A's.

**Commands** (all behind Cluster C; each with a dry-run count preview, a bounded filter, and an
affected-count acknowledgement):
- `PurgeDeadLettersCommand(filter, reason)`: count preview first; consider a two-phase token
  binding filter and count.
- `ParkReleaseCommand(source, filter)`: release or extend deliberate parks.
- `RequarantineCommand(fingerprint)`: only un-recovered rows; cancelling an in-flight campaign
  is explicit, never implied.
- `UpdateSettingCommand(key, value)`: `Target` required, known-key validation (a typo'd key must
  fail loudly, not no-op), old value audited, read back through EffectiveSettings.
- `ResetAttemptsCommand(filter)`: guarded to `error IS NULL` or abandonment-stamped rows.

**Vestigial commands:** wire or explicitly mark each of Diagnostics, ClearCache, Pause, Resume,
CancelPerspectiveRebuild (the never-ship-unwired rule). Likely: Pause and Resume wire cheaply
to drain mode; Diagnostics may retire in favor of the fleet queries.

## Cluster I: embedded telemetry (the management stack option)

1. **One config block** gives services both endpoints (`Whizbang:Management:Endpoint`,
   `Whizbang:Management:TelemetryEndpoint`), with a helper that points the host's OTLP
   exporter at the management stack.
2. **Ingestion (package):** an OTLP/HTTP receiver. Settle the parsing approach first (OTLP
   protobuf versus OTLP/JSON) in this STJ/AOT codebase. Metrics and logs first; traces as
   budget allows. Persist to package-owned tables (`wh_mgmt_otel_*`) with rolling retention
   (the `prune_stack_history` pattern). The management service sees all of it.
3. **Grafana on the management store:** Postgres datasource plus provisioning samples in the
   sample compose. The data and its ownership live in the service. A Prometheus-compatible
   endpoint for current values is a stretch goal.
4. **Stripped re-broadcast:** configurable OTLP/HTTP forward upstream with strip rules (drop
   by metric prefix, drop attributes, sampling). One egress that sees everything and forwards
   less. Rules are options-bound and RED-tested; forward failures are logged with WHAT and
   CONSEQUENCE and never back-pressure ingestion.
5. **Documented alternative:** an OTel Collector gateway sidecar with the same strip rules, for
   hosts that prefer stock infrastructure.

## Cluster J: docs and sample (P5)

- Library: `<docs>` and `<tests>` tags on every new public type; configuration reference for
  `ManagementOptions`, `FleetQueryOptions`, telemetry options.
- Docs repo PR (worktree off `origin/main`; its develop is stale): amend the proposal (namespace
  fix, database-mediated collector, the decisions above, honesty about vestigial commands, the
  failure-modes table, the channel and telemetry additions) and add an "Operating Whizbang"
  section with the daily-operations walkthrough as its acceptance test. Regenerate the
  code-docs and code-tests maps.
- Sample management service in `samples/` using `Whizbang.Management` (channel, fleet surface,
  embedded telemetry) with a compose file: Grafana with datasource and dashboard provisioning
  against the management store, plus two ordinary services pointed at the stack. The local
  shell sets `DOTNET_ENVIRONMENT=Development`, so `ValidateOnBuild` runs locally; sample
  fixtures may need `ServiceRegistrationCallbacks.Dispatcher = null` before `AddWhizbang`.

---

## Acceptance: the daily-operations walkthrough

The surface is done when this needs zero psql:

1. The held-dead-letters alert fires. The operator opens the management service: fleet
   dead-letter status shows which services, which cohorts, which verdicts.
2. Drill into a cohort: stack analytics and search show the failure shape, trend, sample errors.
3. Fix deployed? Release the cohort fleet-wide and watch the per-service acknowledgements and the
   recovered meter. Junk? Purge with a reason, after the count preview.
4. Weekly review: the stack trend shows whether last week's fix ended that failure shape.
5. Throughout, the management service's own Grafana shows the fleet's telemetry, and the roster
   shows every connected instance, including which ones hold the management, maintainer and
   migrator capacities.

## Verification

- Per cluster: each RED observed failing before GREEN (revert-prove where code preceded the
  test); `dotnet format`.
- Cluster boundaries: `pwsh scripts/Run-Tests.ps1 -Mode Ai` with coverage; migration lint for
  every SQL cluster; the startup journey suites after Cluster D.
- End to end: the harness fleet test (Cluster E), a channel client-to-server round trip
  (Cluster F), and a sample management-service smoke run of the walkthrough (Cluster J).
- PR: `pwsh scripts/Run-PR.ps1` (draft first), monitor CI to green, verify origin advanced,
  `Run-Sonar.ps1 -Mode Ai` before ready-for-review.

## Deferred, with rationale

- **A dynamic fleet directory service:** the channel roster covers connected fleets and the
  learned roster covers the rest; a third, cross-service directory is new liveness semantics
  nothing needs yet.
- **Per-instance answers on the fabric:** there is no per-instance broker rail; the channel
  provides per-instance reach instead.
- **Response chunking and paging:** chunks without an assembly protocol cannot be certified
  complete. Cap plus `Truncated`; a kind that outgrows 32KB narrows its filter.
- **A global control-plane rate limiter:** this arc bounds its own family (coalescing,
  min-interval, caps); a cross-family limiter touches every worker and is its own
  storm-hardening arc.
- **Live push of fleet views to browsers (SignalR or similar):** the channel is
  service-to-management; browser push is an adapter concern for after the surface exists.

## Risks

- ONE PR carrying all of this is a mega-arc (estimated 15k to 25k lines with tests). The
  maintainer chose it; keep commits small, clusters green, and be ready to cut the PR early.
- Cluster D changes startup for every host. The advisory lock remains the safety net;
  degraded paths must never end in "never migrates".
- Cluster I is the least-precedented piece (OTLP parsing in an AOT codebase). Scope valve:
  metrics and logs first.
- ASB behaviors (TTL, subscription filters) are asserted through subject and tag unit tests;
  the harness does not model them.
- Develop moves fast (568 commits in 13 days): rebase at every cluster boundary, re-verify
  migration numbers and the registrar block each time.

## Quality bar (standing rules)

RED observed before GREEN, no exceptions; zero reflection and AOT-safe (source-generated JSON,
no generic `Bind`, no `JsonNode`); no `Task.Delay` or polling in tests (completion signals,
`FakeTimeProvider`); every non-rethrowing catch logs what failed and the consequence, and the
test asserts the log; registration-assertion tests for every turnkey registrar; en-US; never
name a consumer, environment or person; `dotnet format`; migration lint; PR to develop, never
push develop; commit per logical step.
