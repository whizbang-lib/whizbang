# Dapper and EF Core start the same way

One driver-neutral startup pipeline for schema setup, so the Dapper and EF Core drivers run the same
stages in the same order with the same guarantees. Raised from #1253 (the Dapper schema cleanup has no
instance id). Tracking for one pull request into `develop`, with a paired docs-site pull request.

## Why

The EF Core driver initializes in a hosted service at host start. The Dapper driver does all of it
synchronously inside `AddWhizbangPostgres`, at registration, before the container exists. That one
difference produces every defect below:

- **No instance id for the schema cleanup (#1253).** A multi-instance Dapper fleet never drops a
  retired object, permanently: the cleanup writes no declarations, so every running peer looks
  unreported and the fleet gate holds every drop.
- **Configuration never read.** `Whizbang:Schema:Reconcile:*` is ignored on Dapper, so the documented
  `DropAfterFleetConverged=false` workaround does nothing. The cleanup logs nothing.
- **The schema-ready gate never opens.** Only the EF Core hosted service calls
  `ISchemaReadyGate.MarkReady()`. A Dapper-only host with the background workers waits forever.
- **Concurrent DDL.** Dapper takes no advisory lock around its migrations; instances starting together
  race.
- **Registration blocks on the network.** `WaitForConnectionAsync().GetResult()` inside a DI
  registration can hang app startup.
- **No periodic cleanup re-run on Dapper,** and none on EF Core either for a DbContext the consumer
  registered itself, because only the generated turnkey registration emits the keyed manifest.
- **Two migration code paths** (the EF Core template and `PostgresSchemaInitializer`) for one schema.

## Stage by stage

| Stage | EF Core today | Dapper today | Target (both) |
|---|---|---|---|
| When | Hosted service at host start | Inside `AddWhizbangPostgres`, at registration | One hosted service and runner; registration only records what to initialize |
| Blocking | Background by default; inline with `NonBlockingSchemaInit=false` | Always blocks registration | The same option for both |
| Wait for the database | In the hosted service | `.GetResult()` at registration | In the runner, same retry and timeout settings |
| Lock | `pg_try_advisory_xact_lock` around migrations and DDL | None of its own | The same lock, key and transaction boundary |
| Register the instance | First, into `wh_service_instances` | Never | First, in both |
| Migrations and infrastructure DDL | Generated per DbContext from a template | `PostgresSchemaInitializer` | One migration engine both call; a driver supplies only its object list |
| Perspective objects | Generated from the model | Declared by the Dapper registrations | Both produce the same declaration manifest |
| Cleanup instance id | `IServiceInstanceProvider` | `null` | Always the id from the container |
| Cleanup settings | Bound from configuration | Defaults only | The same binding |
| Cleanup logging and failure | Logged; never fatal | Silent; never fatal | Logged; never fatal |
| Readiness probe | In the runner | At registration | In the runner, before the gate opens |
| Schema-ready gate | `MarkReady` after everything | Never opened | Opened by the shared runner |
| Periodic cleanup re-run | Maintenance step; needs the keyed manifest (turnkey only) and `IClaimedEmissionStore` | None | Both register their manifest, including consumer-registered DbContexts |
| Once-per-fleet claim | `IClaimedEmissionStore` | SQL fallback (column-fill step) | One claim helper: the store when present, SQL otherwise |
| Instance provider | From `AddWhizbang` or `AddWhizbangWorkers` | Not registered | The driver registers it when absent |
| Physical column fill | A step per DbContext | `DapperPhysicalColumnFillMaintenanceStep` | One step type over the shared manifest |

What stays driver-specific: how each driver produces its declaration manifest (generated from the EF
Core model, or declared by the Dapper registrations) and how it opens connections. Everything else is
shared code.

## Behavior change

A Dapper app no longer has a migrated schema the moment `AddWhizbangPostgres` returns; anything that
needs the schema waits on the schema-ready gate, exactly as on EF Core. The only registration-time
schema reader today is the readiness probe, which moves into the runner. Pre-1.0, this is a breaking
change recorded in the changelog with the upgrade note.

## Constraints

- **No reflection.** No `Activator`, `GetType().GetProperty`, `MakeGenericType`, `Assembly` scanning
  or `BindConfiguration`. Configuration through the binder source generator with literal sections;
  registrations through generated code or explicit calls. AOT-safe.
- **Strict red/green.** Every behavior lands as a failing test first, in its own commit, then the
  change that turns it green.
- **100% branch coverage** of new and changed code, and no untested hand-written outcome library-wide.
  0 Sonar findings.
- **Parity is a test, not a promise.** One startup scenario suite runs against both drivers:
  multi-instance drops, the fleet gate holding and releasing, the gate opening, settings honored,
  concurrent starts serialized by the lock, the database coming up late. Deterministic: every wait is
  on a signal (the gate, a startup hook), never a delay.
- **Linked.** Every new public member and SQL function carries `<docs>` and `<tests>` tags; every new
  test names its code under test; the docs-site pages link both.
- **SQL** follows the migration rules: new migration files only, lint-clean, the canonical lock order.

## Phases (one pull request, committed in this order)

1. **Shared runner.** Extract the driver-neutral startup pipeline from the EF Core hosted service:
   wait, lock, register the instance, migrate, commit, clean up, probe, open the gate. EF Core moves
   onto it with no behavior change (its existing tests stay green).
2. **Dapper on the runner.** `AddWhizbangPostgres` records what to initialize; the runner does the
   rest. The gate opens on Dapper. The driver registers `IServiceInstanceProvider` when absent.
3. **Cleanup parity.** Instance id, configuration binding and logging on both; the manifest registered
   for the periodic re-run on both drivers and for consumer-registered DbContexts; one once-per-fleet
   claim helper.
4. **One migration engine.** Fold `PostgresSchemaInitializer` and the EF Core template's
   infrastructure DDL into one engine both drivers call, driven by the declaration manifest.
5. **Parity suite.** The shared startup scenarios, run against both drivers.
6. **Docs.** The docs-site pages for driver setup, startup and readiness, and managed schema objects,
   updated for both drivers, with the upgrade note; `ai-docs` updated wherever they describe the old
   Dapper registration-time behavior. Close #1253.

## Status

- **Phase 1, shared runner: done.** `WhizbangDatabaseInitializerService` moved to `Whizbang.Data.Postgres` and
  runs every registered `ISchemaInitializationRunner` in order before it opens the gate;
  `AddWhizbangSchemaInitialization()` registers it once, by factory. EF Core registers its runner through it; its
  existing initializer tests pass unchanged apart from the constructor taking a list.
- **Phase 2, Dapper on the runner: done.** `AddWhizbangPostgres` connects to nothing; it records a
  `DapperSchemaInitializationRunner` (wait with the `PostgresOptions` retry settings, migrate, probe) and calls
  `AddWhizbangSchemaInitialization()`, so the gate opens on Dapper too, and opens at once when the schema is
  provisioned out of band. Migrations run under the schema lock (`SchemaInitializationLock`: the EF Core key, a
  transaction-scoped lock on a connection of its own, try first, report contention to `ISchemaInitializationObserver`,
  then wait on the server). The instance registers itself before the lock is released. The driver registers an
  instance identity when the host has none.
- **Phase 3, cleanup parity: done.** The Dapper reconcile runs with this instance's id, settings read from
  `Whizbang:Schema:Reconcile` (a bad value fails the start, as on EF Core), the host's logger and every registered
  contributor, and any failure is logged and never fatal. `DapperManagedSchemaReconcileStep` re-runs it between
  starts. The generated model registration keys each context's manifest, so a consumer-registered DbContext gets
  the EF Core re-run too. `FleetClaim` is the one once-per-fleet claim (the store when registered, the claim table
  otherwise); the EF Core reconcile and column-fill steps no longer stand down without a store. Shared pieces:
  `ManagedSchemaHostPass` (claim window and key, contributors, instance id).
