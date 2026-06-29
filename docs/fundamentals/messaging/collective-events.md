# Collective Events

**Status**: Implemented (consume side) — flags-driven routing, `__collective__` sink, PerspectiveWorker dispatch seam, EF Core apply path. Dapper executor and generator-emitted turnkey registration are follow-ups (see [Driver support](#driver-support)).
**Namespace**: `Whizbang.Core.Messaging` (contracts), `Whizbang.Core.Perspectives` (apply + dispatch)
**Design / plan**: [`plans/collective-events-consume-wiring.md`](../../../plans/collective-events-consume-wiring.md)

## Overview

A **collective event** is one persisted event meaning *"apply this uniform mutation to every row in
scope-X."* Where a [composite event](composite-events.md) bundles many **distinct** events into one
transport hop (1:N at the receiver), a collective event is the complement: **one** event applied
**collectively** across a cohort, persisted as-is. A bulk operation that would emit N identical per-row
events ("archive every job in this tenant") instead emits **one** scoped event, and the projection runner
composes a **single SQL `UPDATE` per affected projection table** whose `WHERE` is the scope predicate.

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
scope:

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
(Dapper supplies the `wh_per_*` table name since it has no entity model to derive it from). The Dapper
scope-filter compiler supports the built-in-resolver shape — equality over a scope field
(`row.Scope.Prop == value`) and `&&`-chains — and throws for richer predicates (use a raw-SQL scope form).

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
- Apply + dispatch: `src/Whizbang.Core/Perspectives/ICollectiveApplyFor.cs`, `ICollectiveSpec.cs`,
  `CollectiveDispatcher.cs`, `ICollectiveSessionAccessor.cs`, `CollectiveRouting.cs`,
  `TenantCollectiveScopeResolver.cs`.
- EF Core: `src/Whizbang.Data.EFCore.Postgres/Collective/` (`EFCoreCollectiveEventExecutor`,
  `CollectiveEventApplier`, `EFCoreCollectiveAdapter`, `CollectiveSettersRewriter`,
  `EFCoreCollectiveSessionAccessor`), `CollectiveEventsEFCoreExtensions.cs`.
- Routing migration: `src/Whizbang.Data.Postgres/Migrations/061_CollectiveEventRouting.sql`.
- Worker seam: `src/Whizbang.Core/Workers/PerspectiveWorker.cs` (`_processCollectiveSinkAsync`).
- Tests: `tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs`,
  `tests/Whizbang.Data.EFCore.Postgres.Tests/EmitEventStoreChainCollectiveSqlTests.cs`,
  `tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs`.
