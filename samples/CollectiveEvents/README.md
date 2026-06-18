# Collective Events Sample

A minimal walkthrough of the user-facing surface for Whizbang's
`ICollectiveEvent` primitive. Compiles standalone; demonstrates the
shape of a perspective + handler + DI registration that a consuming
service would write.

## What this sample shows

`ICollectiveEvent` is for "uniform mutation across a captured set" — the
producer expresses **scope + matched set + mutation**, and the
projection runner applies it as **one SQL UPDATE per affected projection
table**, not as N per-row events. The canonical case is "archive every
job for tenant T at this moment in time": one event row, one update,
one category-level observer push.

This sample wires up:

| File | Purpose |
|---|---|
| `Models/JobModel.cs` | Tiny perspective model standing in for a consumer's `JobModel`. |
| `Events/ArchiveJobsCollectiveEvent.cs` | Constant-value mutation — the bulk-state-change shape. |
| `Events/BumpJobViewCountCollectiveEvent.cs` | Computed-value mutation (`j => j.ViewCount + 1`). |
| `Handlers/CollectiveSpec.cs` | Local concrete `ICollectiveSpec<TModel>` record. Whizbang ships no built-in helper; projects pick their own wrapping. |
| `Handlers/JobCollectivePerspective.cs` | Two `[CollectiveApplyFor]`-marked methods. The Slice 5 generator emits a static dispatch table from these — runtime is reflection-free. |
| `Wiring/ServiceRegistration.cs` | DI registration: perspective + `TenantCollectiveScopeResolver`. |

## How it executes at runtime (the slices behind it)

Slices 1–9 are landed. The pipeline a collective event flows through:

```
Producer code
   │
   ▼
ArchiveJobsCollectiveEvent { Scope=TenantCollectiveScope("t-1"),
                             MatchedStreamIds=[…snapshot…],
                             OccurredAt=… }
   │
   ▼
Outbox row (is_collective=true)     ← Slice 3 stamps the flag
   │
   ▼
Transport (Service Bus / etc.)
   │
   ▼
Inbox row (is_collective=true)      ← Slice 3 preserves the flag
   │
   ▼
Projection runner sees is_collective=true
   │
   ├─ Resolves ICollectiveScopeResolver by ScopeKind   ← Slice 4 (TenantCollectiveScopeResolver)
   ├─ Looks up handler in CollectiveApplyRegistry      ← Slice 5 (generator-emitted static table)
   ├─ EnterContext(scope) → ambient ScopeContextAccessor
   ├─ Invokes handler.ArchiveJobs(evt) → ICollectiveSpec<JobModel>
   │
   ▼
CollectiveEventApplier<JobModel>     ← Slice 7a (coordinator)
   │
   ▼
EFCoreCollectiveAdapter<JobModel>    ← Slice 6 (LINQ → ExecuteUpdateAsync)
   │
   ▼
ONE SQL UPDATE: wh_per_job
  SET data = jsonb_set(jsonb_set(data, '{Status}', '"Archived"'::jsonb), …),
      last_collective_event_id = @evt_id
  WHERE scope->>'TenantId' = 't-1' AND stream_id = ANY(@matched_ids)
```

Optional opt-in fan-out: a consumer that genuinely needs per-stream side
effects (saga state machine, SignalR per-id push) registers a receptor
for `CollectivePerStreamMarker` and the framework calls
`CollectiveEventExpander.Expand(envelope)` to produce N markers — one
per entry in `MatchedStreamIds`. Slice 8. The default is **no
expansion**: one category-level observed event.

## What's NOT in this sample

- **No host project / no real Postgres.** The sample is contracts +
  handlers only. The applier runs against a real `DbContext` in
  `Whizbang.Data.EFCore.Postgres` integration tests — those are the
  end-to-end verification, not this sample's job. The sample's job is to
  pin the user-facing surface so a consumer can copy it into their own
  service.
- **No production runner wiring.** Hooking the registry into
  `PerspectiveWorker`'s dispatch loop is a follow-up (Slice 7b in the
  plan). When that lands, this sample's `JobCollectivePerspective` runs
  unmodified — the dispatch table is already emitted.
- **No Dapper variant.** The two handlers use the LINQ adapter (EF
  driver). The Dapper adapter (Slice 9) covers constant-value
  `SetProperty` only in the first cut; `BumpJobViewCount`'s computed
  expression would throw `NotSupportedException` under Dapper and fall
  through the raw-SQL escape hatch.

## Verifying the generator output

```bash
dotnet build samples/CollectiveEvents/
```

After build, the generator emits the dispatch table at:

```
samples/CollectiveEvents/obj/Debug/net10.0/generated/Whizbang.Generators/Whizbang.Generators.CollectiveApplyDiscoveryGenerator/CollectiveApplyRegistry.g.cs
```

Inspect that file to confirm two entries:

- `(JobModel, ArchiveJobsCollectiveEvent)` → `JobCollectivePerspective.ArchiveJobs`
- `(JobModel, BumpJobViewCountCollectiveEvent)` → `JobCollectivePerspective.BumpJobViewCount`

…with typed `Invoker` lambdas (no reflection in the runtime call path).

## Further reading

- `fundamentals/messaging/collective-events.md` — the full design page
  (decision tree, AOT story, replay invariants, expander semantics).
- `plans/proud-wibbling-orbit.md` — locked design decisions per slice.
