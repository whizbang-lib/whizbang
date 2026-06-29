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
| **`Custom`** | `spec.Where` **alone** (the resolver scope filter is not even computed) | The handler owns the entire predicate — a multi-field/cross-table cohort the model-agnostic resolver can't express. A null `Where` here is a misconfiguration and throws. |

```csharp
// Refine within the tenant envelope — only Draft jobs in the event's tenant:
[CollectiveApplyFor]                                    // ScopeHandling = Framework (default)
public ICollectiveSpec<OrderModel> ApplyTemplate(TemplateAppliedCollectiveEvent e) =>
  new CollectiveSpec<OrderModel>(
    Setters: s => s.SetProperty(j => j.JobTemplateId, e.TemplateId),
    Where:   r => r.Data.OverlayId == null);

// Own the whole WHERE — the handler scopes by its own columns, resolver scope ignored:
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
   dispatcher fans out internally to every matching `TModel` handler, each running one
   `ExecuteUpdateAsync`/`UPDATE … WHERE <scope>`.

### Determinism & replay

Determinism is **at scope level, not stream level**. The event carries no enumerated set of matched
streams — only its scope. On replay the predicate is re-evaluated against the projection state at the
event's log position; because event-sourcing fully determines projection state from the log up to that
point, the result is deterministic and reflects the logically-correct cohort (self-healing against
out-of-order original delivery). Re-applying is idempotent — the same SET values, and the
`collectiveEventId` audit-pointer column identifies the last collective writer.

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

| Driver | Expression → SQL | Apply executor | Status |
|---|---|---|---|
| **EF Core** (`Whizbang.Data.EFCore.Postgres`) | `CollectiveSettersRewriter` → `EF.Functions.JsonbSet` → `ExecuteUpdateAsync` | `EFCoreCollectiveEventExecutor<TModel>` | **Complete** |
| **Dapper** (`Whizbang.Data.Dapper.Postgres`) | `DapperCollectiveSpecCompiler` (SET) + `DapperCollectiveScopeFilterCompiler` (WHERE) → one `UPDATE` | `DapperCollectiveEventExecutor<TModel>` (+ `DapperCollectiveEventApplier`) | **Complete** |

Dapper DI mirrors EF Core: `AddCollectiveEventsDapper(entries)` + `AddCollectiveExecutorDapper<TModel>(tableName)`
(Dapper supplies the `wh_per_*` table name since it has no entity model to derive it from), plus
`AddCollectiveTableDapper<TOther>(tableName)` for any **query-only sibling** a handler reaches via
`q.Of<TOther>()`. The Dapper scope-filter compiler translates equality over a **scope** field
(`row.Scope.Prop == value` → `scope->>'Prop'`) **or a data** field (`row.Data.Prop == value` →
`data->>'Prop'`); `&&`-chains mixing both; `Contains` (→ `IN`); and `q.Of<TOther>().Any(...)`
cross-perspective cohorts (→ a correlated `EXISTS`). It throws for richer predicates (non-equality,
disjunctions, arbitrary top-level columns, nested `EXISTS`) — use a raw-SQL form.

Both compilers support scalar top-level `SetProperty(j => j.Prop, constant)` with constant/captured-value
sources, plus chained setters. **Computed-arithmetic** setters (`j => j.X + 1`) and nested paths are
deferred to `[CollectiveApplyFor(SpecKind = RawSql)]` in v1 (both compilers throw `NotSupportedException`).

## Open follow-ups

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
- EF Core: `src/Whizbang.Data.EFCore.Postgres/Collective/` (`EFCoreCollectiveEventExecutor`,
  `CollectiveEventApplier`, `EFCoreCollectiveAdapter`, `CollectiveSettersRewriter`, `EFCoreCollectiveQuery`,
  `EFCoreCollectiveSessionAccessor`), `CollectiveEventsEFCoreExtensions.cs`.
- Dapper: `src/Whizbang.Data.Dapper.Postgres/Collective/` (`DapperCollectiveEventExecutor`,
  `DapperCollectiveEventApplier`, `DapperCollectiveScopeFilterCompiler`, `DapperCollectiveQuery`,
  `DapperCollectiveTableRegistry`), `CollectiveEventsDapperExtensions.cs`.
- Routing migration: `src/Whizbang.Data.Postgres/Migrations/061_CollectiveEventRouting.sql`.
- Worker seam: `src/Whizbang.Core/Workers/PerspectiveWorker.cs` (`_processCollectiveSinkAsync`).
- Tests: `tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs`,
  `tests/Whizbang.Core.Tests/Perspectives/CollectiveWhereComposerTests.cs`,
  `tests/Whizbang.Data.EFCore.Postgres.Tests/EmitEventStoreChainCollectiveSqlTests.cs`,
  `tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs`
  (`DispatchAsync_FrameworkWithHandlerWhere_*`, `DispatchAsync_CustomHandlerWhere_*`),
  `tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs`.
