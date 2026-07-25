# Plan: collective events — consume-side delivery wiring (turnkey)

## Goal

Make `ICollectiveEvent` a **turnkey, end-to-end** Whizbang feature. The apply chain
(`ICollectiveDispatcher` → `ICollectiveEventExecutor` → `CollectiveEventApplier<TModel>` → one scoped SQL
`UPDATE`) is already built and proven by `CollectiveDispatcherEFCoreIntegrationTests` (hand-built dispatcher,
DbContext passed directly). **What is missing is the entire delivery path**: nothing routes a persisted
collective event to that chain, and nothing invokes it from a live worker. This plan adds that path —
turnkey, AOT-clean, and on **both** Postgres drivers (EF Core **and** Dapper).

> Same spirit as `composite-events-turnkey.md`: a collective event is an **ordinary persisted event**
> everywhere except **one seam — the apply**, where instead of the per-stream `Apply` path it routes to the
> scope-predicate `UPDATE`. That seam sits **inside the durable perspective-work / cursor envelope**, not
> bolted outside it.

## The precise gap (why nothing happens today)

`wh_perspective_events` work rows — the only thing that makes an event reach `PerspectiveWorker` — are
created in `029_ProcessWorkBatch.sql:492` (`_emit_event_store_chain`) by:

```sql
INNER JOIN wh_message_associations ma
  ON es.event_type = ma.normalized_message_type
  AND ma.association_type = c_source_perspective      -- 'perspective'
... uses ma.target_name AS perspective_name
```

Those `'perspective'` associations are emitted only for `IPerspectiveFor<TModel,TEvent>` registrations
(`EFCorePerspectiveAssociationGenerator`, `PerspectiveRegistrationsTemplate`). Collective handlers are
discovered on a **separate** path — `[CollectiveApplyFor]` → `CollectiveApplyDiscoveryGenerator` →
`CollectiveApplyRegistry.g.cs` — which emits **no association rows**. So a persisted collective event matches
**zero** associations → **zero** `wh_perspective_events` rows → it never reaches any worker. The producer
already stamps `EventFlags.Collective` (`Dispatcher.cs:3841,4963`, `TransportConsumerWorker.cs:951`); that
flag is currently read by nobody.

## Foundational prerequisite (discovered during implementation) — collective events must persist as events

The original design assumed a published collective event already lands in the event store. It does not.
Two foundational gaps had to be closed first (the real reason the feature stalled — the apply chain was
built, but nothing produced/persisted a collective event):

1. **`ICollectiveEvent : IEvent`** (was `: IMessage`). The producer sets `IsEvent = payload is IEvent`
   (`Dispatcher.cs:3839`), and `_emit_event_store_chain` only copies `is_event = true` rows into
   `wh_event_store`. As `IMessage`, a collective event got `is_event = false` and never reached the event
   store — so routing/dispatch could never fire. **Done** (RED→GREEN contract test; `Whizbang.Core` builds).

2. **Scope must be AOT-serializable.** Making it an `IEvent` brought the polymorphic `ICollectiveScope Scope`
   under the AOT serializability analyzer (WHIZ062), which rejects a bare non-generic interface property.
   Resolved with the established Whizbang pattern (the `AbstractFieldSettings`/composite-`$type` approach):
   a new **`abstract record CollectiveScope : ICollectiveScope`** serialization base with
   `[JsonPolymorphic(TypeDiscriminatorPropertyName = "$scopeKind")]` + `[JsonDerivedType]` for built-ins;
   `ICollectiveEvent.Scope` is retyped to `CollectiveScope`. The interface stays as the behavioral contract
   (resolver signatures unchanged). **Done in core** (`Whizbang.Core` builds, WHIZ062 cleared); downstream
   consumers (sample + ~7 collective test files: concrete scope records `: ICollectiveScope` →
   `: CollectiveScope`, event `Scope` properties → `CollectiveScope`) still need the mechanical update to
   compile. Open follow-up: **open-set custom-scope serialization** — `[JsonDerivedType]` is a closed list;
   custom scopes need the same open cross-assembly registry `IMessage` uses (`RegisterDerivedType`), a
   generator extension. v1 covers the built-in tenant scope (a consumer's collective candidates are tenant/status).

3. **Each collective event carries `[GenerateStreamId]`** (its own single-event stream) — confirmed design
   intent; demonstrated in the sample + worker-path test.

## Design — three parts

### 1. Routing: flags-driven `__collective__` sink work row (pure SQL, driver-agnostic, once-only)

**Revised from the association approach after deeper investigation.** Routing is done entirely in SQL off the
already-stamped `flags` column — **no generator change, no association rows** (the association registration
path is EF-only via `RegisterPerspectiveAssociationsAsync`, and two registration calls would orphan-DELETE
each other's rows; flags routing sidesteps both problems and is shared by both Postgres drivers).

Two SQL facts make this clean:
- `wh_outbox.flags` (and `wh_inbox.flags`) already carry `EventFlags.Collective` (`= 1`), stamped by the
  producer (`Dispatcher.cs:3841`, `TransportConsumerWorker.cs:951`).
- **But `_emit_event_store_chain` drops it:** its `outbox_events` CTE doesn't select `o.flags` and the
  `INSERT INTO wh_event_store` omits the column, so `wh_event_store.flags` is stuck at `0` — the schema's own
  documented `WHERE (flags & 1) = 1` pattern (`EventStoreSchema.cs:30`) can never match today.

New additive migration (next number) does two things, in **both** the outbox chain (`_emit_event_store_chain`)
and the inbox chain (`_emit_event_store_chain_for_inbox`):

1. **Carry the flag through**: add `o.flags` / `i.flags` to the source CTE and `flags` to the
   `INSERT INTO wh_event_store (… , flags)` column list + `SELECT`. (Fixes the latent gap regardless.)
2. **Create one sink work row per collective event**: alongside the existing association-join INSERT into
   `wh_perspective_events`, add a branch that, for stored events where `(es.flags & 1) = 1`, inserts **exactly
   one** row with `perspective_name = '__collective__'` (literal, no association lookup). Same partition /
   owner / lease / dedupe (`NOT EXISTS` + `ON CONFLICT uq_perspective_event DO NOTHING`) logic as the normal
   branch.

**Why one literal sink and not per-model perspectives:** the dispatcher already fans out internally to *every*
matching `TModel` handler. Keying work rows to each model perspective → K models → K work rows → K
`DispatchAsync` calls, each re-applying **all** models = K× duplicate UPDATEs. One literal sink row → one
`DispatchAsync` → once-only. Cross-pod/intra-pod single-execution is already guaranteed by the existing
`(stream_id, perspective_name)` lease/affinity gate — the collective event rides its **own** single-event
stream, pinned to one owner.

### 2. Worker seam: detect → DispatchAsync once → report completion → skip per-stream runner

In `PerspectiveWorker`, special-case the collective sink **before** runner resolution. Two touch points
(mirror each other), both already holding a per-stream `groupScope`:

- Channel path: `ProcessChannelBatchAsync` group body (`PerspectiveWorker.cs:880`), before
  `_resolveDependenciesAndLoadEventsAsync` / `_executePerspectiveRunnerAsync` (`:885`/`:912`).
- Drain twin: `_runDrainModePerspectiveAsync` (`:1602`), before `runner.RunWithEventsAsync` (`:1746`).

When `perspectiveName == "__collective__"` (or, defensively, the loaded event's `Payload is ICollectiveEvent`
/ `Flags & EventFlags.Collective`):

```csharp
var dispatcher = groupScope.ServiceProvider.GetRequiredService<ICollectiveDispatcher>();
var session    = <driver-agnostic projection session>;       // see open question Q1
await dispatcher.DispatchAsync(collectiveEvt, envelope.MessageId.Value, session, ct);
// report completion through groupWorkCoordinator so the cursor advances + work row is marked processed
// then return — DO NOT call the per-stream runner (a collective event has no single target stream)
```

Structural template to copy: the payload-type branch at `InboxDispatchWorker.cs:386`
(`if (typedEnvelope?.Payload is ICompositeEvent composite) { …; return; }`) — same "detect marker, handle,
return early" shape, different call.

**Completion reporting must reuse the existing path** (`_reportCompletionAndSignalSyncAsync` /
`groupWorkCoordinator`) so the cursor advances and the work row is deleted/marked exactly as a normal
perspective apply — otherwise the row re-leases forever.

### 3. DI registration — both drivers (turnkey, zero per-app wiring)

Today collective components are registered **nowhere** except the sample. Add to the framework registration
extensions so `AddWhizbang…` wires them automatically:

- Core: `ICollectiveDispatcher` singleton — the recipe in `CollectiveDispatcher.cs:23-28`
  (`new CollectiveDispatcher(sp, CollectiveApplyRegistry.Entries, sp.GetServices<ICollectiveScopeResolver>(),
  sp.GetServices<ICollectiveEventExecutor>(), sp.GetService<EventCategoryMetrics>())`), plus the built-in
  `TenantCollectiveScopeResolver` (apps add custom resolvers for custom scope kinds).
- EF Core (`Whizbang.Data.EFCore.Postgres/EFCoreExtensions.cs`): one `EFCoreCollectiveEventExecutor<TModel>`
  per `TModel` that has a `[CollectiveApplyFor]` handler — enumerated from the generated registry so it stays
  zero-config and AOT-clean.
- Dapper (`Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs`): the **symmetric Dapper executor**
  per `TModel`.

> **Driver expression-conversion is already done and at parity** — not part of this plan. Both
> `CollectiveSettersRewriter` (EF) and `DapperCollectiveSpecCompiler` (Dapper) already convert the carried
> `SetProperty` expression into `jsonb_set` SQL for scalar top-level constant / captured-value setters +
> chained setters. Both deliberately defer **computed-arithmetic** setters (`j => j.X + 1`) and nested paths
> to the `[CollectiveApplyFor(SpecKind = RawSql)]` escape hatch (identical `NotSupportedException`, intended
> v1.0 matrix). A consumer's collective candidates use constant values, so computed support is **optional and
> out of scope** here unless explicitly pulled in. This plan adds only the **DI registration** for both
> drivers, not new compiler capability.

## Open questions for review (resolve before coding)

- **Q1 — driver-agnostic projection session** *(decided: option (a))*. `DispatchAsync(…, object
  dbContextOrSession, …)` needs the EF `DbContext` (or Dapper `NpgsqlConnection`) that holds the perspective
  tables. The worker is driver-agnostic and does not resolve a DbContext today. **Decision: a small
  `ICollectiveSessionAccessor` each driver registers, resolved from `groupScope`** — keeps the worker free of
  driver types; EF returns its `DbContext`, Dapper its `NpgsqlConnection`.
- **Q2 — sink name.** Confirm `'__collective__'` doesn't collide with any real perspective name; confirm the
  `uq_perspective_event` constraint and `wh_active_streams` owner logic behave for a sink that isn't a real
  perspective cursor. (Routing no longer needs an `association_type='collective'` — flags-driven.)
- **Q3 — checkpoint/cursor semantics for the sink.** A real perspective advances a per-stream cursor. The
  sink "perspective" advances on the collective event's own single-event stream. Confirm
  `complete_perspective_cursor_work` / checkpoint creation tolerates a sink perspective with no
  `IPerspectiveRunner` registered (it must, since we short-circuit before runner resolution).
- **Q4 — replay.** On rebuild, the collective event's work row is recreated and `DispatchAsync` re-applies
  against **current** projection state (the determinism guarantee in `ICollectiveEvent` docs). Re-apply is
  idempotent (same SET values; audit-pointer column re-stamped). Confirm no ordering hazard vs. the per-stream
  events the scope touches (collective applies at its log position; rows created later are picked up by the
  predicate on replay — the intended self-healing behaviour, already covered by
  `DispatchAsync_RowsMaterializedAfterEventEmitted_AreIncluded`).

## Determinism, idempotency, AOT

- **Determinism at scope level** (per `ICollectiveEvent` contract): no captured stream set; predicate
  re-evaluated at apply time. The single-sink routing preserves this — nothing enumerates streams.
- **Idempotency**: re-running the scoped `UPDATE` is safe (constant/computed SET to a deterministic value;
  `collectiveEventId` audit pointer identifies the last collective writer). The sink work row is dedupe-keyed
  `(stream_id, '__collective__', event_id)`.
- **AOT (L19)**: dispatch routes through the generated `CollectiveApplyRegistry.Entries` lambda invokers and
  typed DI — **no reflection**. The new association generation is compile-time. Executor enumeration for DI
  comes from the generated registry, not a runtime `Type` scan. Verify zero new AOT/analyzer warnings.

## Test plan (TDD, RED first, 100% diff coverage, real Postgres)

- **Routing (RED→GREEN)**: persist a collective event → assert exactly one `wh_perspective_events` row with
  `perspective_name='__collective__'` is created (generator emits the association; `029` join picks it up).
- **Worker path, per driver** (EF Core **and** Dapper, Testcontainers): publish a collective event → the live
  `PerspectiveWorker` resolves `ICollectiveDispatcher` + session from DI (not `_buildDispatcher()`), the
  scoped `UPDATE` fires, the audit-pointer column is written, the cursor/work row completes, and the
  per-stream runner is **not** invoked. Use a constant-value setter (the supported, common case);
  computed-arithmetic setters are out of scope (both compilers already defer them to `RawSql`).
- **Once-only**: two `[CollectiveApplyFor]` handlers (two models) for one event type → exactly one work row →
  one `DispatchAsync` → both models updated once (no duplicate UPDATEs).
- **Replay determinism**: rebuild → re-apply hits the logically-correct cohort (mirror
  `DispatchDispatcher…RowsMaterializedAfterEventEmitted`), through the worker path.
- **DI**: `AddWhizbang…` alone wires dispatcher + executors + resolver (no manual registration).

## Slices (each its own RED→GREEN PR into develop)

1. **Migration — flags routing**: additive migration carrying `flags` through both event-store chains
   (`_emit_event_store_chain` + `_emit_event_store_chain_for_inbox`) and creating one `__collective__`
   `wh_perspective_events` row per stored event with `(flags & 1) = 1`. Integration test: collective event →
   exactly one sink work row, `wh_event_store.flags` populated. (No generator/association work — flags-driven.)
2. **Worker seam**: detect sink (`perspectiveName == '__collective__'` / `payload is ICollectiveEvent`) →
   `DispatchAsync` once → report completion via the normal cursor path → skip runner. The Q1
   `ICollectiveSessionAccessor` (option a) lands here, with EF + Dapper implementations.
4. **DI registration both drivers** (`ICollectiveDispatcher` + per-`TModel` EF/Dapper executors + resolver,
   auto-wired from the generated registry). Driver expression-conversion is already done; computed-arithmetic
   setters stay out of scope (RawSql escape hatch).
5. **Docs**: write `docs/fundamentals/messaging/collective-events.md` (mirror `composite-events.md`); close
   the docs↔code↔tests graph; bump `last_verified`.
6. **Package + bump into a consumer** (`.local-whizbang-packages/`, `Directory.Packages.props`, `dotnet restore`) —
   gates the consumer's collective track.

## Non-goals

- A consumer's collective conversions (Template Apply / Overlay Apply) — Phase 2 of that rollout, after this ships.
- Changing the composite path. Collective and composite stay complementary (`EventFlags` category bits).
- Multi-table transactional atomicity across models beyond what one `UPDATE`-per-table already provides.
