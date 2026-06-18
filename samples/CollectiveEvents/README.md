# Collective Events Sample

A minimal walkthrough of the user-facing surface for Whizbang's
`ICollectiveEvent` primitive. Compiles standalone; demonstrates the
shape of a perspective + handler + DI registration that a consuming
service would write.

## What this sample shows

`ICollectiveEvent` describes "apply this mutation to everything in
scope-X at this point in the event sequence." The producer expresses
**scope + mutation**, and the projection runner applies it as **one SQL
UPDATE per affected projection table** whose `WHERE` clause is exactly
the resolver's scope predicate — no per-row enumeration, no per-row
tracking, no per-row tax. The canonical case is "archive every job for
tenant T at this point in the log": one event row, one update, one
category-level observer push.

## Determinism is at scope level

The event carries only its scope. Replay re-evaluates the predicate
against the projection state at the moment the collective event is
being processed. Because event-sourcing guarantees the projection state
at any point in a replay is fully determined by the event sequence up
to that point, the result is deterministic — and reflects the
*logically correct* state.

If a stream's CREATE event arrived late in real time but logically
belongs *before* the collective event in the log, replay processes the
events in log order: the create comes first, the row exists when the
collective applies, the row is included. The projection self-heals on
replay. See the docs page for the canonical 11-stream replay example.

## Sample file layout

| File | Purpose |
|---|---|
| `Models/JobModel.cs` | Tiny perspective model. |
| `Events/ArchiveJobsCollectiveEvent.cs` | Constant-value mutation — `[PinnedId]`, `Scope` only. |
| `Events/BumpJobViewCountCollectiveEvent.cs` | Computed-value mutation (`j => j.ViewCount + 1`), `Scope` only. |
| `Handlers/CollectiveSpec.cs` | Local concrete `ICollectiveSpec<TModel>` record. Whizbang ships no built-in helper; projects pick their own wrapping. |
| `Handlers/JobCollectivePerspective.cs` | Two `[CollectiveApplyFor]`-marked methods. The Slice 5 generator emits a static dispatch table from these — runtime is reflection-free. |
| `Wiring/ServiceRegistration.cs` | DI: perspective + `TenantCollectiveScopeResolver`. |

## How it executes at runtime

```
Producer code
   │
   ▼
ArchiveJobsCollectiveEvent { Scope = TenantCollectiveScope("t-1"),
                             OccurredAt = … }
   │
   ▼
Outbox row (flags |= EventFlags.Collective)
   │
   ▼
Transport (Service Bus / etc.)
   │
   ▼
Inbox row (flags preserved)
   │
   ▼
PerspectiveRunner sees flags & Collective != 0
   │
   ├─ Resolves ICollectiveScopeResolver by Scope.ScopeKind
   ├─ Looks up handlers in CollectiveApplyRegistry
   ├─ EnterContext(scope) → ambient ScopeContextAccessor
   ├─ Invokes handler.ArchiveJobs(evt) → ICollectiveSpec<JobModel>
   │
   ▼
CollectiveEventApplier<JobModel>
   │
   ▼
EFCoreCollectiveAdapter<JobModel>
   │
   ▼
ONE SQL UPDATE: wh_per_job
  SET data = jsonb_set(jsonb_set(data, '{Status}', '"Archived"'::jsonb), …)
  WHERE scope->>'TenantId' = 't-1'
```

That's the entire query. No matched-id `IN(...)` clause. No
`SET last_collective_event_id = …`. The event carries only scope; the
runner does only scope filtering.

## What's NOT in this sample

- **No host project / no real Postgres.** The sample is contracts +
  handlers only. The end-to-end SQL behavior is verified by
  `CollectiveDispatcherEFCoreIntegrationTests` against a Postgres
  testcontainer.
- **No expander.** Earlier iterations of this design had a
  `CollectiveEventExpander` that fanned out per-stream markers; the
  scope-determinism model has no use for it (no captured matched-set to
  enumerate). Consumers wanting per-stream side effects subscribe at
  the existing projection-write hook.
- **No Dapper variant.** The two handlers use the LINQ adapter (EF
  driver). The Dapper compiler covers constant-value `SetProperty` only
  in v1.0; `BumpJobViewCount`'s computed expression throws
  `NotSupportedException` under Dapper and needs the raw-SQL escape
  hatch.

## Verifying the generator output

```bash
dotnet build samples/CollectiveEvents/
```

After build, the generator emits the dispatch table at:

```
samples/CollectiveEvents/obj/Debug/net10.0/generated/Whizbang.Generators/Whizbang.Generators.CollectiveApplyDiscoveryGenerator/CollectiveApplyRegistry.g.cs
```

Inspect to confirm two entries:

- `(JobModel, ArchiveJobsCollectiveEvent)` → `JobCollectivePerspective.ArchiveJobs`
- `(JobModel, BumpJobViewCountCollectiveEvent)` → `JobCollectivePerspective.BumpJobViewCount`

…with typed `Invoker` lambdas (no reflection in the runtime call path).

## Further reading

- `fundamentals/messaging/collective-events.md` — the full design page
  (scope-vs-stream-level determinism, the 11-stream replay example,
  EventFlags, AOT story, replay invariants).
- `plans/proud-wibbling-orbit.md` — locked design decisions per slice.
