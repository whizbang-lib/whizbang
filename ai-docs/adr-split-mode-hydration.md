# ADR: Split-Mode Physical Field Hydration via ChangeTracker.Tracked

**Status**: Accepted  
**Date**: 2026-04-03  
**Deciders**: Phil Carbone, Claude

## Context

Split-mode physical fields (`[PerspectiveStorage(FieldStorageMode.Split)]`) store selected fields in dedicated PostgreSQL columns instead of JSONB. This enables SQL-native WHERE, ORDER BY, JOIN, GROUP BY, and pgvector operations.

When EF Core materializes a `PerspectiveRow<TModel>`, the `Data` property is populated exclusively from JSONB via `ComplexProperty().ToJson()`. In Split mode, physical fields are stripped from JSONB at write time, so they come back as null/default after materialization.

We needed a way to hydrate physical field values from shadow properties (which hold the physical column values) into the `Data` model after materialization.

## Decision

Use EF Core's `ChangeTracker.Tracked` event to hydrate Split-mode physical fields after entity materialization completes. Entities are immediately detached after hydration to eliminate tracking overhead.

### Implementation

- **`SplitModeChangeTrackerHydrator`**: Static class with `ConcurrentDictionary<Type, Action<EntityEntry>>` keyed by closed generic type (`typeof(PerspectiveRow<TModel>)`). Generated code registers hydrators at startup.
- **Scoped access classes**: Conditionally skip `AsNoTracking()` for Split models and call `SplitModeChangeTrackerHydrator.EnsureHooked(context)`.
- **`ChangeTracker.Tracked` handler**: Performs zero-reflection dictionary lookup by `entity.GetType()`, invokes hydrator, which reads shadow properties and immediately detaches.
- **Generator**: Emits `SplitModeChangeTrackerHydrator.Register(typeof(PerspectiveRow<T>), ...)` calls.

### Key files

| File | Role |
|------|------|
| `Whizbang.Data.EFCore.Postgres/SplitModeChangeTrackerHydrator.cs` | Event handler + hydrator registry |
| `Whizbang.Data.EFCore.Postgres/EFCorePostgresLensQuery.cs` | Conditional tracking in scoped access |
| `Whizbang.Data.EFCore.Postgres/EFCoreFilterableLensQuery.cs` | Same for filterable queries |
| `Whizbang.Data.EFCore.Postgres/MultiModelScopedAccess.cs` | Same for multi-model queries |
| `Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs` | Code generation |

## Alternatives Considered

### 1. IMaterializationInterceptor (InitializedInstance)

**Rejected.** `InitializedInstance` fires during EF Core's materialization pipeline, BEFORE `ComplexProperty().ToJson()` populates the `Data` property. With `row.Data == null`, the interceptor can't write physical field values. Confirmed empirically.

The existing `PhysicalFieldMaterializationInterceptor` is kept as a harmless fallback (with `if (row.Data is null) return;` guard) in case a future EF Core version changes the materialization order.

### 2. Custom IQueryable wrapper

**Rejected.** Wrapping `IQueryable<PerspectiveRow<TModel>>` to intercept `ToListAsync()` and post-process results is extremely fragile. EF Core's `ToListAsync()` extension methods check for `IAsyncQueryProvider` and may bypass custom providers.

### 3. Manual hydration helper

**Rejected.** Providing a `HydratePhysicalFieldsAsync()` method that users call explicitly after `ToListAsync()` requires user code changes and breaks the transparent API. Users would need to know which models are Split and always remember to call the helper.

### 4. Dual storage (JSONB + columns)

**Rejected.** Storing physical fields in both JSONB and physical columns eliminates the hydration problem but introduces data duplication and consistency risks.

## Consequences

### Positive

- **Transparent**: No user API changes. `ILensQuery<TModel>` works identically for Split and non-Split models.
- **Zero reflection, AOT-safe**: Dictionary keyed by closed generic type. `entity.GetType()` is a CLR vtable intrinsic.
- **Minimal overhead**: Entities tracked only for microseconds (immediate detach in handler).
- **Full LINQ support**: WHERE, ORDER BY, GROUP BY, JOIN, COUNT, ANY, SKIP/TAKE all work through the expression visitor.

### Negative

- **Vector Select projections**: `.Select(r => r.Data.Embeddings)` still reads from JSONB because shadow property type (`Vector`) differs from model type (`float[]`). Users must use `EF.Property<Vector?>(r, "embeddings")` for vector Select projections. Full entity materialization hydrates vectors correctly.
- **Tracked queries for Split models**: Split model queries momentarily use tracking (before immediate detach). This is transparent but differs from the default `AsNoTracking()` behavior.

## Triggers for Re-evaluation

| Future Change | What It Enables |
|---------------|----------------|
| EF Core adds a post-JSON materialization hook (e.g., `FinalizedInstance`) | Move hydration to interceptor, remove tracking requirement |
| EF Core adds result-set post-processors for `IQueryable` | Transparent post-processing without tracking |
| `ComplexProperty().ToJson()` changes materialization order | Existing interceptor fallback becomes primary path |
| Shadow property type unified to `float[]` for vectors | Vector Select projections work through expression visitor |

## Test Matrix

54 integration tests validate this behavior:

- **Gate tests** (4): ChangeTracker.Tracked fires after Data populated, shadow properties readable, hydrate+detach works, bulk results all hydrated
- **LensQuery integration** (16): GetByIdAsync, Query.ToListAsync, WHERE/ORDER BY/GROUP BY/Count/Any/Skip-Take, Select projections, non-Split regression, detach verification, 50-row stress test
- **Production pattern tests** (30): Full entity materialization, vector operations, SQL verification for every LINQ operator, join tests
- **Split mode integration** (4): Write path, scope, LINQ queries
