# Collective Events

**Status**: Implemented (consume side) — flags-driven routing, `__collective__` sink, PerspectiveWorker dispatch seam, EF Core **and** Dapper apply paths, per-perspective `Where` projection. Generator-emitted turnkey registration is the remaining follow-up (see [Driver support](#driver-support)).
**Namespace**: `Whizbang.Core.Messaging` (contracts), `Whizbang.Core.Perspectives` (apply + dispatch)
**Design / plan**: [`plans/collective-events-consume-wiring.md`](../../../plans/collective-events-consume-wiring.md)

## Overview

A **collective event** is one persisted event meaning *"apply this uniform mutation to every row in
scope-X."* Where a [composite event](composite-events.md) bundles many **distinct** events into one
transport hop (1:N at the receiver), a collective event is the complement: **one** event applied
**collectively** across a cohort, persisted as-is. A bulk operation that would emit N identical per-row
events ("archive every job in this tenant") instead emits **one** scoped event, and the projection runner
composes a **single SQL `UPDATE` per affected projection table** whose `WHERE` is the scope predicate —
optionally refined per-perspective by the handler (see [Per-perspective projection](#per-perspective-projection-where)).

Pick **collective** when the mutation is uniform across a scope and you do **not** need a per-entity event
for it. Pick **composite** when each stream gets a distinct payload. Pick neither (stay individual) when a
per-entity event is consumed downstream (notification, acknowledgment, audit, a receptor keyed off the
per-entity event).

```csharp
[PinnedId("…")]
public sealed record ArchiveJobsCollectiveEvent : CollectiveEventBase {
  public required DateTimeOffset OccurredAt { get; init; }
}

// Publish once — the framework mints the event's own stream id, persists it, routes + applies it:
await dispatcher.PublishAsync(new ArchiveJobsCollectiveEvent {
  Scope = new TenantCollectiveScope(tenantId),
  OccurredAt = DateTimeOffset.UtcNow,
});
```

## Authoring a collective event

Derive from **`CollectiveEventBase`** (`Whizbang.Core.Messaging`). The base carries the event's own
`[StreamId] [GenerateStreamId]` stream id — **each collective event is its own single-event stream**, minted
at dispatch — and the `Scope`. Add a `[PinnedId]` and any mutation-payload fields.

The mutation lives on a **perspective handler**, not the event. Mark a method `[CollectiveApplyFor]`; it
returns the SET clauses as an `ICollectiveSpec<TModel>`, and the framework composes the `WHERE` from the
scope (optionally refined by the handler — see [Per-perspective projection](#per-perspective-projection-where)):

```csharp
public sealed class JobCollectivePerspective {
  [CollectiveApplyFor]
  public ICollectiveSpec<JobModel> Archive(ArchiveJobsCollectiveEvent e) =>
    new CollectiveSpec<JobModel>(s => s
      .SetProperty(j => j.Status, "Archived")
      .SetProperty(j => j.ArchivedAt, e.OccurredAt));
}
```

The generator (`CollectiveApplyDiscoveryGenerator`) discovers these methods into a reflection-free
`CollectiveApplyRegistry.Entries` table (AOT-clean, one typed `Invoker` lambda per entry).

### Per-perspective projection (`Where`)

The **same persisted collective event projects independently into every perspective that handles it** —
across models, and across services. `CollectiveApplyRegistry` is generated **per assembly**, so each
service declares its own `[CollectiveApplyFor]` handler for its own `TModel`; the one routed event fans out
to all of them. There is no per-row event replication — each perspective interprets the collective intent
in **its own** columns.

Each handler projects two things onto its model:

- **the SET clauses** — already per-model via `ICollectiveSpec<TModel>.Setters`;
- **the `WHERE`** — via the optional `ICollectiveSpec<TModel>.Where` (an
  `Expression<Func<PerspectiveRow<TModel>, bool>>?`, default `null`). The handler — which *knows its model* —
  shapes the cohort onto its own columns, e.g. `r => r.Data.Status == "Draft"`. This is what lets a model
  that keeps a field on a sibling read model project the cohort differently than one that carries it inline.

How `Where` composes with the resolver's scope filter is governed by `[CollectiveApplyFor(ScopeHandling = …)]`
([`CollectiveWhereComposer`](#reference)):

| `ScopeHandling` | Effective `WHERE` | Use when |
|---|---|---|
| **`Framework`** (default) | `resolverScopeFilter AND spec.Where` (or the scope filter alone when `Where` is null) | The scope envelope (e.g. tenant) must always bind; the handler only *refines* within it and can't over-mutate. |
| **`Custom`** | `resolverScopeFilter AND spec.Where` (a non-null `Where` is **required**) | The handler owns the **cohort** predicate — a multi-field/cross-table cohort the model-agnostic resolver can't express — but the scope envelope **still binds**. A null `Where` here is a misconfiguration and throws. |

> **The scope envelope always binds (0.795, D0).** Both modes AND the resolver's scope filter into the SQL
> `WHERE`; a `Custom` handler refines *within* its scope and can never escape it. (Before 0.795 the `Custom`
> path composed `spec.Where` *alone* and skipped the scope filter — a cross-tenant data-safety hole on shared
> multi-tenant tables. The only remaining difference between the modes is that `Framework` permits a null
> `Where` — scope alone — while `Custom` requires the handler to supply the cohort predicate.)

```csharp
// Refine within the tenant envelope — only Draft jobs in the event's tenant:
[CollectiveApplyFor]                                    // ScopeHandling = Framework (default)
public ICollectiveSpec<OrderModel> ApplyTemplate(TemplateAppliedCollectiveEvent e) =>
  new CollectiveSpec<OrderModel>(
    Setters: s => s.SetProperty(j => j.JobTemplateId, e.TemplateId),
    Where:   r => r.Data.OverlayId == null);

// Own the cohort predicate by the handler's own columns — the resolver scope is STILL AND-ed in (D0):
[CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
public ICollectiveSpec<OrderModel> ClearOverlay(OverlayClearedCollectiveEvent e) =>
  new CollectiveSpec<OrderModel>(
    Setters: s => s.SetProperty(j => j.OverlayId, (Guid?)null),
    Where:   r => r.Data.OverlayId == e.OverlayId);
```

`CollectiveSpec<TModel>` is a tiny consumer-owned record (Whizbang ships none) — give it both a `Setters` and
a nullable `Where` member. The default-interface-member `Where => null` keeps existing Setters-only specs
working unchanged.

### Cross-perspective cohorts (`ICollectiveQuery`)

A `Where` over `row.Data` only sees the table being mutated. When the cohort is defined by a field on a
**sibling** read model — e.g. JobService's `OrderModel` carries no status (it lives on the sibling
`OrderStatusModel`, same id) — the handler's `Apply` receives an **`ICollectiveQuery`** and reaches the
sibling through it:

```csharp
[CollectiveApplyFor]                                  // ScopeHandling = Framework (tenant envelope AND this cohort)
public ICollectiveSpec<OrderModel> ApplyTemplate(TemplateAppliedCollectiveEvent e, ICollectiveQuery q) =>
  new CollectiveSpec<OrderModel>(
    Setters: s => s.SetProperty(j => j.JobTemplateId, e.TemplateId),
    Where:   r => q.Of<OrderStatusModel>()
                   .Any(st => st.Id == r.Id && Eligible.Contains(st.Data.Status)));
```

`ICollectiveQuery.Of<TOther>()` returns a queryable over the sibling perspective's rows. Both drivers
translate the resulting `.Any(...)` to a **correlated `EXISTS`** in the same single `UPDATE`:

- **EF Core** — `Of<TOther>()` is the live `DbContext.Set<PerspectiveRow<TOther>>()`; EF funcletizes the
  `q.Of()` call and emits `EXISTS (SELECT 1 FROM <sibling> s WHERE s.id = d.id AND …)`.
- **Dapper** — the filter compiler reads the `q.Of<TOther>()` node, resolves the sibling table (registered
  via `AddCollectiveTableDapper<TOther>` / `AddCollectiveExecutorDapper`), and emits the same `EXISTS` SQL;
  `.Any` → `EXISTS`, `Contains` → `IN`.

Supported inside the `.Any(...)`: an `Id`-correlation (`st.Id == r.Id`) plus equality / `Contains` leaf
predicates over the sibling's `Data`/`Scope`. Richer shapes (non-equality, nested `EXISTS`) throw a clear
`NotSupportedException`. Handlers that don't need a sibling simply ignore the `ICollectiveQuery` parameter.

### Scope

`Scope` is a **`CollectiveScope`** — an abstract polymorphic base (not a bare interface) so the event
round-trips through the AOT-strict, source-generated message serializer via a `$scopeKind` discriminator.
`CollectiveScope` implements `ICollectiveScope` (the behavioral contract the resolvers use). Built-in:
`TenantCollectiveScope(TenantId)` (kind `"tenant"`). The `ScopeKind` string selects the
`ICollectiveScopeResolver` that owns the `WHERE`-predicate composition for that scope family.

> **Why an abstract class, not an interface.** The AOT serializability analyzer (WHIZ062) rejects a bare
> non-generic interface property on an event — there is no concrete shape to source-generate a serializer
> for. A single polymorphic value on a serializable type uses an abstract base class with `[JsonDerivedType]`
> discriminators (the same pattern as `AbstractFieldSettings`).

## Persistence, routing, and dispatch

A collective event is a **first-class persisted `IEvent`** (`ICollectiveEvent : IEvent`), so it flows
through the normal produce → persist → project pipeline, with one branch at the apply seam:

1. **Persist.** Published like any event; the producer stamps `EventFlags.Collective` (`flags & 1`) on the
   outbox/inbox row. `_emit_event_store_chain` (migration **061**) carries `flags` into `wh_event_store` and
   stores the event on its own stream.
2. **Route.** For each stored event with `(flags & 1) = 1`, migration 061 creates **exactly one**
   `wh_perspective_events` row with `perspective_name = '__collective__'` (the
   `CollectiveRouting.SINK_PERSPECTIVE_NAME` sink) — driven purely by the flag, **no association lookup**.
   One sink row per event regardless of how many model handlers subscribe.
3. **Dispatch.** `PerspectiveWorker` special-cases the `__collective__` sink (both channel and drain paths):
   it loads the collective event(s) on the sink stream, resolves `ICollectiveDispatcher` + the projection
   session (via `ICollectiveSessionAccessor`), calls `DispatchAsync` **exactly once** per event, advances the
   sink cursor, and **skips the per-stream runner** (a collective event has no single target stream). The
   dispatcher fans out internally to every matching `TModel` handler, each running the bounded, scoped apply
   below.
4. **Complete the sink row (§8).** On a successful dispatch the sink completes its own `__collective__`
   `wh_perspective_events` work rows **by `event_work_id`** (the `complete_perspective_events` DELETE path
   every standard perspective uses), so `claim_orphaned_perspective_events` can't re-lease them. The cursor
   advance in step 3 only moves the cursor + marks `processed_at`; it does **not** delete the row. Omitting
   the by-`event_work_id` completion left every applied sink row with `processed_at = NULL`, so it was
   re-leased and the whole-cohort `UPDATE` re-dispatched every tick — the self-sustaining re-dispatch loop
   behind the production table bloat. A leased sink row whose event the cursor already passed is completed
   too (a stale re-lease), so it can't spin a no-op loop.
5. **Terminal lifecycle after the apply completes.** A collective event has no per-stream runner, so it never
   reaches the normal `PostAllPerspectives` gate — but the set-based apply *finishing* **is** its
   "all-perspectives-complete" moment. On the success path (only), the sink runs each applied event through the
   `PostAllPerspectivesDetached → PostAllPerspectivesInline → PostLifecycleDetached → PostLifecycleInline`
   stages via `IReceptorInvoker` (`_fireCollectivePostApplyLifecycleAsync`). This is what lets a
   **`[FireAt(PostAllPerspectivesInline)]` receptor** and any **`[NotificationTag]`** fire *after* the apply is
   durably done — e.g. a completion receptor that publishes the tag-bearing "orchestration completed" event a
   UI's progress toast waits on. Per-event failures in this stage are isolated + logged (the apply already
   committed; a throwing completion receptor must not undo it). A **failed** apply returns before this step, so
   a completion signal is never emitted for an apply that did not happen.

### Apply execution — scoped, bounded, indexed (0.795)

Each handler's apply is **one predicate `UPDATE` per projection table**, hardened so a large cohort can never
convoy locks or run away (the mechanics that stopped the production spiral):

- **Predicate `UPDATE`, no id-gather (D1).** The composed `WHERE` (scope envelope AND the handler cohort) is
  compiled straight to SQL by the shared `CollectivePredicateSqlCompiler` — no `SELECT id … ToList` of the
  whole cohort. One code path serves both drivers.
- **Scope always binds (D0).** The resolver's scope predicate (e.g. `scope->>'t' = @tenant`) is always
  AND-ed in, even under `Custom` — see the [ScopeHandling table](#per-perspective-projection-where).
- **Keyset batching (§4).** The cohort is applied in `CollectiveApplyOptions.BatchSize` chunks
  (`… WHERE <pred> AND id > @cursor ORDER BY id LIMIT n` → `UPDATE … WHERE id = ANY`), each its own short
  transaction — bounded lock holds, never materializes the whole cohort.
- **Server-side `statement_timeout` (§3).** `SET LOCAL statement_timeout` per batch (the only form that
  survives PgBouncer transaction pooling) so a runaway batch is cancelled by Postgres, never left a zombie.
- **Per-(table, scope) exclusive advisory lock (§5a).** Each batch takes `pg_advisory_xact_lock(hash(table,
  scope))` — DB-global, so it serializes same-scope collective applies **across pods** while disjoint scopes
  (e.g. different tenants) run concurrently. Opt out with `SerializeApplies = false`.
- **Startup expression index (§7).** The btree `((scope->>'t'))` expression index the tenant-scope filter
  needs (`gin(scope)` can't serve `->>` equality) is created **at service startup** by the schema generator
  (`EFCoreServiceRegistrationGenerator._appendStandardIndexes`, alongside the `gin` indexes), **never in the
  apply path**. Index creation takes a `SHARE` lock, so doing it in a live apply — as an earlier apply-time
  `EnsureIndexes`/`CollectiveIndexEnsurer` design did — is unacceptable and was removed. Handler cohort
  filters correlate the sibling perspective by **PK** (`st.Id == r.Id`), so they need no extra index; the
  compiler still records `ReferencedJsonPaths` as the compile-time basis for any future per-property startup
  index.
- **Per-handler knobs (§6).** `[CollectiveApplyFor(BatchSize = …, StatementTimeoutSeconds = …)]` override the
  global `CollectiveApplyOptions` defaults for a heavy or light handler (`0` = inherit).
- **Observability (§9).** Transient (`40P01`/`40001`) retries log via `PostgresDeadlockRetry`, and an
  apply-completion log carries the collective event id + affected rows + batch count.

### Observability — traces + metrics

A collective event's fan-out and apply are **traced** so a single slow event is investigable by type/namespace
(not just an aggregate metric):

- **`Collective Dispatch` span** (`ActivitySource` `Whizbang.Tracing`, from `CollectiveDispatcher`) wraps the
  whole fan-out. Tags: `whizbang.collective.event_type`, `whizbang.collective.event_namespace`,
  `whizbang.collective.scope_kind`, `whizbang.collective.event_id`, `whizbang.collective.handler_count`,
  `whizbang.collective.affected_rows`. A failed apply sets the span status to `Error`. This is the span to
  filter/sort on when "one event type is taking longer" — the namespace tag lets you scope a trace search to a
  contract area, exactly like other Whizbang spans.
- **`Collective Apply` span** (child, from `EFCoreCollectiveAdapter`) wraps the keyset-batched `UPDATE` loop.
  Tags: `whizbang.collective.model_type`, `whizbang.collective.table`, `whizbang.collective.event_id`,
  `whizbang.collective.batch_size`, `whizbang.collective.affected_rows`, `whizbang.collective.batches`. It
  nests under the dispatch span (via `Activity.Current`), so a slow event drills down to *which table / how
  many batches* consumed the time. Register the source with `.AddSource("Whizbang.Tracing")` in your OTel
  tracing pipeline.
- **Metrics** (`EventCategoryMetrics`, meter `Whizbang.EventCategories`) carry the same `event_type` /
  `event_namespace` dimensions: `event_category.dispatched`, `event_category.fanout`,
  `event_category.dispatch.duration`, `event_category.errors` — so dashboards and traces line up on the same
  tag keys.

### Determinism & replay

Determinism is **at scope level, not stream level**. The event carries no enumerated set of matched
streams — only its scope. On replay the predicate is re-evaluated against the projection state at the
event's log position; because event-sourcing fully determines projection state from the log up to that
point, the result is deterministic and reflects the logically-correct cohort (self-healing against
out-of-order original delivery). Re-applying is idempotent — the same constant SET values — and `id > @cursor`
keyset progress means a partial/resumed run never skips or double-applies a row. The collective event id is
carried in the apply-completion telemetry (which event mutated how many rows), not a per-row audit column.

## DI wiring (EF Core)

```csharp
services
  .AddCollectiveEventsEFCore<MyPerspectiveDbContext>()   // dispatcher + tenant resolver + session accessor
  .AddCollectiveExecutorEFCore<JobModel>();              // one per perspective model with a [CollectiveApplyFor]
// Custom scope kinds: also register your ICollectiveScopeResolver.
```

`AddCollectiveExecutorEFCore<TModel>` is an explicit compile-time generic call (no `MakeGenericType`) to
stay AOT-clean — a source generator can emit these per-model calls for full turnkey registration (follow-up).

## Driver support

| Driver | SET → SQL | WHERE → SQL | Apply | Status |
|---|---|---|---|---|
| **EF Core** (`Whizbang.Data.EFCore.Postgres`) | `CollectiveSettersRewriter` → nested `jsonb_set` | shared `CollectivePredicateSqlCompiler` | `EFCoreCollectiveEventExecutor<TModel>` — keyset-batched predicate `UPDATE` + advisory lock + `statement_timeout` (tenant-scope index emitted at startup, §7) | **Complete** |
| **Dapper** (`Whizbang.Data.Dapper.Postgres`) | `DapperCollectiveSpecCompiler` → `jsonb_set` | shared `CollectivePredicateSqlCompiler` | `DapperCollectiveEventExecutor<TModel>` (+ applier) — keyset-batched + advisory lock + `statement_timeout` | **Parity** (no completion-log yet) |

Both drivers share one WHERE compiler (`CollectivePredicateSqlCompiler`, in `Whizbang.Data.Postgres`) and the
same keyset-batched apply shape. Dapper DI mirrors EF Core: `AddCollectiveEventsDapper(entries)` +
`AddCollectiveExecutorDapper<TModel>(tableName)` (Dapper supplies the `wh_per_*` table name since it has no
entity model to derive it from), plus `AddCollectiveTableDapper<TOther>(tableName)` for any **query-only
sibling** a handler reaches via `q.Of<TOther>()`. The shared compiler translates equality over a **scope** field
(`row.Scope.Prop == value` → `scope->>'Prop'`) **or a data** field (`row.Data.Prop == value` →
`data->>'Prop'`); `&&`-chains mixing both; `Contains` (→ `IN`); and `q.Of<TOther>().Any(...)`
cross-perspective cohorts (→ a correlated `EXISTS`). It throws for richer predicates (non-equality,
disjunctions, arbitrary top-level columns, nested `EXISTS`) — use a raw-SQL form.

Both compilers support scalar top-level `SetProperty(j => j.Prop, constant)` with constant/captured-value
sources, plus chained setters. **Computed-arithmetic** setters (`j => j.X + 1`) and nested paths are
deferred to `[CollectiveApplyFor(SpecKind = RawSql)]` in v1 (both compilers throw `NotSupportedException`).

## Open follow-ups

- **§5b — standard-apply shared lock** (deferred): the standard single-row apply taking a *shared* advisory
  lock so it coordinates with the collective *exclusive* lock (§5a). Collective-vs-collective is already
  serialized cross-pod by §5a; §5b is the collective-vs-standard refinement (ordering, not correctness).
  Reservations / pros-cons / how-to-vet: [`plans/collective-5b-standard-apply-shared-lock-deferred.md`](../../../plans/collective-5b-standard-apply-shared-lock-deferred.md).
- **§8 — perspective failure / DLQ backoff plumbing** (deferred): a triple identifier mismatch on the core
  *failure* path means a perspective/collective apply **failure** is not recorded (Failed flag / backoff).
  Note the **success** side of §8 — the sink completing its own `__collective__` rows by `event_work_id` so
  they're deleted and never re-leased — **is fixed** (that was the production re-dispatch loop; see
  [Persistence, routing, and dispatch](#persistence-routing-and-dispatch) step 4). What remains deferred is
  the *failure* attribution/backoff; not spiral-critical (the apply hardening makes applies *succeed*, and a
  genuinely poison sink row still dead-letters via `claim_orphaned`'s attempt increment + the pre-apply DLQ
  filter). Precise root cause + fix plan:
  [`plans/collective-8-perspective-failure-plumbing-deferred.md`](../../../plans/collective-8-perspective-failure-plumbing-deferred.md).
- **Dapper completion-log parity** — Dapper has the keyset/lock/knob/logger parity but not yet the
  apply-completion telemetry (no current Dapper consumer). The `((scope->>'t'))` startup index is emitted by
  the EF Core schema generator; the Dapper schema snippet already emits its own `((scope->>'t'))` btree.
- **Generator-emitted executor registration** — auto-emit `AddCollectiveExecutor{EFCore,Dapper}<TModel>()`
  per model (and the Dapper table name) for full turnkey registration.
- **Open-set custom-scope serialization** — `[JsonDerivedType]` is a closed list; custom scopes need the
  cross-assembly `RegisterDerivedType` registry that `IMessage` polymorphism uses.
- **Dapper computed/raw setter parity** — both compilers defer computed-arithmetic setters to `RawSql`.

## Reference

- Contracts: `src/Whizbang.Core/Messaging/ICollectiveEvent.cs`, `CollectiveEventBase.cs`,
  `CollectiveScope.cs`, `TenantCollectiveScope.cs`.
- Apply + dispatch: `src/Whizbang.Core/Perspectives/ICollectiveApplyFor.cs`, `ICollectiveSpec.cs`
  (the `Where` projection), `ICollectiveQuery.cs` (cross-perspective cohorts),
  `CollectiveWhereComposer.cs` (scope/`Where` composition), `CollectiveDispatcher.cs`,
  `ICollectiveSessionAccessor.cs`, `CollectiveRouting.cs`, `TenantCollectiveScopeResolver.cs`.
- Shared (both drivers): `src/Whizbang.Data.Postgres/Collective/` — `CollectivePredicateSqlCompiler` (WHERE →
  SQL + `ReferencedJsonPath`s, the compile-time basis for index decisions), `CollectiveApplyLockKey`
  (advisory-lock key), `CollectiveApplyOptions` (`Whizbang.Core.Perspectives`, batch/timeout/serialize knobs).
- EF Core: `src/Whizbang.Data.EFCore.Postgres/Collective/` (`EFCoreCollectiveEventExecutor`,
  `CollectiveEventApplier`, `EFCoreCollectiveAdapter` (keyset batch + lock + timeout), `CollectiveSettersRewriter`,
  `EFCoreCollectiveQuery`, `EFCoreCollectiveSessionAccessor`), `CollectiveEventsEFCoreExtensions.cs`.
- Startup index (§7): the tenant-scope btree `((scope->>'t'))` is emitted per perspective table by
  `src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs`
  (`_appendStandardIndexes` / `_generatePerspectiveIndexSql`) — never at apply time.
- Dapper: `src/Whizbang.Data.Dapper.Postgres/Collective/` (`DapperCollectiveEventExecutor`,
  `DapperCollectiveEventApplier` (keyset batch + lock + timeout), `DapperCollectiveSpecCompiler` (SET),
  `DapperCollectiveQuery`, `DapperCollectiveTableRegistry`), `CollectiveEventsDapperExtensions.cs`.
- Deadlock/retry: `src/Whizbang.Data.Postgres/PostgresDeadlockRetry.cs`.
- Routing migration: `src/Whizbang.Data.Postgres/Migrations/061_CollectiveEventRouting.sql`.
- Worker seam: `src/Whizbang.Core/Workers/PerspectiveWorker.cs` (`_processCollectiveSinkAsync` +
  `_completeCollectiveSinkWorkRows` — the §8 by-`event_work_id` completion).
- Tests: `tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs`,
  `tests/Whizbang.Core.Tests/Perspectives/CollectiveWhereComposerTests.cs`,
  `tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.cs`
  (`CollectiveSink_SuccessfulDispatch_CompletesSinkWorkRowByEventWorkId_*`, drain twin, and the
  no-collective-event stale-lease case — the §8 re-dispatch-loop regression lock-in),
  `tests/Whizbang.Data.EFCore.Postgres.Tests/EmitEventStoreChainCollectiveSqlTests.cs`,
  `tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs`
  (`DispatchAsync_FrameworkWithHandlerWhere_*`, `DispatchAsync_CustomHandlerWhere_*`,
  `DispatchAsync_NeverCreatesIndexesInTheApplyHotPath_*` — §7),
  `tests/Whizbang.Generators.Tests/EFCoreServiceRegistrationGeneratorTests.cs`
  (`Generator_SchemaExtensions_IncludesBtreeExpressionIndexForTenantScope_*` — §7 startup index),
  `tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs`.
