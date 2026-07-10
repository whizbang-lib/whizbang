# Apply Hooks

**Status**: Implemented — shared marker-matching core, collective path (EF Core **and** Dapper), per-event
path (EF Core **and** Dapper), the `whizbang.timestamps` default hook on both paths.
**Namespace**: `Whizbang.Core.Perspectives.Hooks` (surface + registries), `Whizbang.Data.Postgres` (driver-neutral
planners/interpreter).

## Overview

An **apply hook** is pluggable logic that modifies **what a perspective's `Apply` produced**, gated by the
model's type. There are two hooks over two apply paths, with a deliberately **identical surface**:

| | Collective path | Per-event path |
|---|---|---|
| What it mutates | the set-based SQL `UPDATE` (a whole cohort) | the loaded row instance (one row) |
| Interface | `ICollectiveApplyHook<TMarker>` | `IApplyHook<TMarker>` |
| Registry | `CollectiveApplyHookRegistry` | `ApplyHookRegistry` (via `PerEventApplyHooks.Registry`) |
| Extra verbs | `AndWhere` / `ReplaceWhere` (cohort predicate) | — (single row) |

The motivating case: a collective apply is a set-based SQL `UPDATE` that bypasses all per-event apply
extensibility. Bringing `updated_at`/`version` stamping to the collective path exposed the need for a general
seam — so the stamping itself is now the **`whizbang.timestamps` default hook**, present (and overridable) on
both paths.

## Marker-gated matching (identical on both paths)

- A hook is registered against a type `TMarker` — a **concrete class, a base class, or an interface**. It fires
  for a model `TModel` when `TModel` is **assignable to** `TMarker`
  (`typeof(TMarker).IsAssignableFrom(typeof(TModel))`). So `IAuditable`, a base perspective class, or a
  concrete model all work. `object` matches every model — the default-hook marker.
- **Multiple registrations accumulate.** Matching hooks fire in **registration order** — not by marker
  specificity.
- **Optional `key` = override-in-place.** Registering a `key` that already exists **replaces** the hook at that
  key's slot (keeping its order position); a new key or an unkeyed registration appends. The key is **global**
  (one slot per key across all markers) — re-registering a key with a different marker moves that slot to the
  new marker.
- **Documented default-hook key.** The framework registers its defaults under public keys so you override them
  by re-registering the same key: `WhizbangApplyHookKeys.TIMESTAMPS = "whizbang.timestamps"`.
- **AOT (L19).** The matching hook list is resolved per `TModel` **once** and memoized; the apply hot path does
  no `IsAssignableFrom`. Hooks record a declarative `ApplyHookOp` list (no reflection); only a per-event
  `SetProperty` compiles a cached setter from the hook's compile-time selector metadata (the established
  data-layer tradeoff, suppressed inline).

## Builder vocabulary

A hook does not touch the database or the row directly — it **records** verbs through its builder, and each path
interprets the same op list for its own mechanics:

- `SetProperty(m => m.Prop, value)` — a model data field. Collective → an extra `jsonb_set`; per-event →
  `row.Data.Prop = value`.
- `SetColumn(column, value)` — a physical store column. Collective → `"column" = @param` (any column);
  per-event → the matching `PerspectiveRow` property. **Per-event supports `updated_at` only**; arbitrary
  physical columns are collective-only (a per-event apply mutates a row object, which has no arbitrary column).
- `BumpVersion()` — `version = version + 1` (collective) / `row.Version++` (per-event). A first-class verb
  because it is not a constant assignment.
- `RemoveSetter(m => m.Prop)` — drop a model-field setter an earlier stage added. Collective-focused; a no-op on
  the per-event path (single row, no setter list).
- `AndWhere(m => …)` / `ReplaceWhere(m => …)` — **collective only.** Refine or replace the cohort `WHERE`. The
  mandatory tenant scope envelope is still AND-ed on top (D0 safety), so a hook can reshape the cohort but never
  escapes its scope. The predicate (written over the model marker) is lifted onto `PerspectiveRow<TModel>.Data`.

`ApplyHookContext` carries `ModelType`, the `Event`/`Scope` where available, and one `ApplyTimestamp` per apply
(shared across every keyset batch of a collective event).

## The `whizbang.timestamps` default hook

Registered against `object` under `WhizbangApplyHookKeys.TIMESTAMPS` on both paths:

```csharp
builder.SetColumn(ApplyHookColumns.UPDATED_AT, ctx.ApplyTimestamp).BumpVersion();
```

This formalizes the store-managed stamping both paths always did (a collective `UPDATE` that wrote only `data`
left `updated_at`/`version` stale, breaking change-detection; the per-event upsert already stamped them). Now it
is **overridable**: re-register a hook with the same key to change or suppress it.

## Registering hooks

**Collective** — DI seeds a `CollectiveApplyHookRegistry` with the defaults (`TryAddSingleton`, so you can
register your own first) and injects it into the collective executors:

```csharp
services.AddSingleton(_ =>
  WhizbangApplyHooks.CreateCollectiveWithDefaults()
    .Register<IAuditable>(new StampLastTouchedByHook())          // fires for every IAuditable model
    .Register<object>(new MyStamps(), WhizbangApplyHookKeys.TIMESTAMPS)); // override the default stamp
```

**Per-event** — a process-wide static (mirroring `BaseUpsertStrategy.PathOnePersistenceOptionsProvider`), so the
default applies everywhere with zero wiring. Register custom hooks at startup:

```csharp
PerEventApplyHooks.Registry = WhizbangApplyHooks.CreatePerEventWithDefaults()
  .Register<IAuditable>(new StampLastTouchedByHook());
```

## Both-driver parity

The collective path renders the resolved hook plan into one set-based `UPDATE` on **both** EF Core
(`EFCoreCollectiveAdapter`) and Dapper (`DapperCollectiveEventApplier`) — model-field setters as `jsonb_set`,
store columns as `"col" = @param`, `BumpVersion` as `version = version + 1`, and the composed cohort `WHERE`.
The per-event path applies the plan at all three write sites — EF Core's atomic `INSERT … ON CONFLICT` upsert
and its legacy SELECT-then-update object path, and the Dapper perspective store — with `SetProperty` mutating
the model object before serialization and `updated_at`/version driven by the plan.

## Code & tests

- Shared core: `src/Whizbang.Core/Perspectives/Hooks/` — `ApplyHookContext`, `WhizbangApplyHookKeys`,
  `ApplyHookColumns` + `ApplyHookOp`, `IApplyHookBuilder` / `ICollectiveApplyHookBuilder`, `IApplyHook` /
  `ICollectiveApplyHook`, `ApplyHookBuilders`, `ApplyHookRegistry` (`MarkerHookRegistryBase` +
  `CollectiveApplyHookRegistry` + `ApplyHookRegistry`), `TimestampsApplyHook`, `WhizbangApplyHooks`.
- Collective driver-neutral: `src/Whizbang.Data.Postgres/Collective/CollectiveApplyHookPlan.cs`,
  `CollectiveApplyHookPlanner.cs` (fold + `AndWhere`/`ReplaceWhere` lift + store-column validation).
- Collective EF Core: `src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs`,
  `CollectiveEventApplier.cs`, `CollectiveSettersRewriter.cs` (`FromHookSetters`),
  `EFCoreCollectiveEventExecutor.cs`; DI in `CollectiveEventsEFCoreExtensions.cs`.
- Collective Dapper: `src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveEventApplier.cs`,
  `DapperCollectiveSpecCompiler.cs` (`AddConstant` + `hookSetters`/`removedFields`),
  `DapperCollectiveEventExecutor.cs`; DI in `CollectiveEventsDapperExtensions.cs`.
- Per-event: `src/Whizbang.Data.Postgres/PerEventApplyHooks.cs` (accessor + fold + compiled setter);
  `src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs` (atomic + legacy paths);
  `src/Whizbang.Data.Dapper.Postgres/DapperPostgresPerspectiveStore.cs`.
- Tests: `tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs` (marker match/skip, order,
  keyed override, default override, verb recording, context propagation);
  `tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs`
  (`Hook_*` — SetProperty/SetColumn/RemoveSetter/AndWhere/ReplaceWhere/non-matching, both-driver parity in the
  Dapper twin `tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs`);
  `tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs` (fold + compiled setter);
  `tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperPostgresPerspectiveStoreTests.cs`
  (`DapperUpsert_PerEventHook_*` — end-to-end object mutation + updated_at override).
