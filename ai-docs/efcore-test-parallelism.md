# EF Core Postgres tests: parallel by default

`tests/Whizbang.Data.EFCore.Postgres.Tests` runs its tests in parallel (#2069). Until then every class ran one at a
time under one key, `EFCorePostgresTests`, inherited from `EFCoreTestBase` with the reason "the tests compete for the
shared container's connections", although each test creates a database of its own. An EF Core shard was the last
suite to finish in every CI run.

## The rule

1. **A test is parallel unless it says otherwise.** `EFCoreTestBase` carries no `[NotInParallel]`.
2. **What tests share is the container's capacity**, and the project's limit is sized to it:
   `[assembly: ParallelLimiter<EFCorePostgresParallelism>]` (`AssemblyInfo.cs`) caps how many tests run at once in
   one process. TUnit's own default is eight per processor, which would ask one container for more connections than
   it has.
3. **A test that shares something else serializes on that thing**, with a key named for the resource and the reason
   as a comment on the attribute's line:
   ```csharp
   [NotInParallel("LocalEventStorageTests.LocalEventTracker")] // every test resets and reads the one process-wide event tracker
   ```
4. **A test that resets a registry or switch every database test reads runs alone**: `[NotInParallel]` with no key. A key only
   orders the tests that hold it, and every database test reads these registries, so no key is narrow enough.
5. **A database is created and dropped through `PerTestDatabaseFactory`**, which retries the template contention
   (55006) that concurrent `CREATE DATABASE` produces. Never issue your own.
6. **A test that needs the migrated framework schema copies it** (`EFCoreSchemaTemplate.CreateDatabaseAsync`, which
   `EFCoreTestBase` does for every test) rather than migrating an empty database itself (#2167). See
   [The schema template](#the-schema-template).
7. **A test registers its own models in a shared registry; it does not clear the registry.** The physical field
   registry is keyed by model type, so a class that registers a test-local model touches nothing another test reads
   and runs in parallel. Clearing it is what sends a class to the serial phase.
8. **A read of a cluster-wide catalog view names the database** (`pg_locks.database`, `pg_stat_activity.datname`,
   `current_database()`): every test database has the same tables, and a sibling's lock or session otherwise answers
   for yours.

### How TUnit schedules the three kinds

TUnit 1.x runs a session in phases: every unconstrained test first (in parallel, under the limit), then every keyed
test (tests with different keys in parallel, the same key one at a time, still under the limit), then every keyless
`[NotInParallel]` test, one at a time, alone. A keyed test never overlaps an unconstrained one, but it does overlap
keyed tests with other keys; only the keyless attribute guarantees nothing else is running.

A test gets one `[NotInParallel]`, and when its class has one the class's wins: the method's keys are dropped without
a warning. A class that serializes therefore names on the class every key any of its tests needs (a test running
with the class's TTL key alone swapped the apply-hook registry beside another class restoring it, and failed in the
merge queue on PR #2318). Check what a test really holds with `TestContext.Current.Parallelism.Constraints`.

## Enforcement

| Guard | Fails when |
|---|---|
| `TestIsolation/ParallelismPolicyGuardTests` | the base class serializes; any test holds the retired key; a `[NotInParallel]` line has no reason (files older than the rule sit on a list that only shrinks); a class that serializes has a member that serializes too (TUnit ignores the member's keys); the assembly limit is missing; the live container's `max_connections` cannot hold the limit |
| `TestIsolation/SharedRegistryResetIsolationTests` | a test resets a registry, coalescer, hydrator or test seam without running alone |
| `TestIsolation/ScratchDatabaseLifecycleGuardTests` | a fixture issues its own `CREATE DATABASE` (two deliberate exceptions) |
| `TestIsolation/BackendTerminationScopeTests` | a test ends backends without naming its database |
| `TestIsolation/SchemaTemplateGuardTests` | the base class migrates for itself or stops copying the template; a fixture creates a database and migrates the framework context in it from scratch without being on the reasoned list (which only shrinks); a base-class test's database lacks the current template's marker |
| `ParallelismPolicyGuardTests.TheClassesThatRegisterTheirOwnModels_StayParallelAndNeverClearTheRegistryAsync` | a class that registers its own physical-field models clears the registry or serializes again |

## The schema template

Until #2167 every `EFCoreTestBase` test created an empty database and ran the whole schema pass in it: core tables,
every migration, perspective tables, constraints, associations and the reconcile. That was most of a shard's time.
`EFCoreSchemaTemplate` builds the schema once as a PostgreSQL template database and every test gets
`CREATE DATABASE ... TEMPLATE` of it, a file-level clone with the template's indexes and statistics.

- **Name and rebuild.** The template is `eftmpl_<hash>`, the hash taken over the migration texts as the ledger hashes
  them, the bootstrap scripts (with the core tables) and the physical-column rewrites, so worktrees with different
  migrations sharing a container keep separate templates. The hash cannot see the perspective DDL or the associations
  (private to the generated class), so once per process the schema pass is run on a copy of the template: the pass
  rewrites a ledger row whenever what the code builds differs, so an unchanged ledger is a current template. The
  copy's marker (a comment on the `public` schema) must also match the fingerprint. A stale template is dropped and
  built again. (`IsWhizbangSchemaCurrentAsync` cannot serve: it answers false on a database one pass has just built,
  because the stored-form rewrite runs before the perspective tables exist and settles nothing until a second start.)
- **Per-database values.** A copy shares every row the template has, so a value a migration seeds per database is
  renewed in each copy: `wh_service_config.service_id`, the service identity that rides on outgoing envelopes (an
  origin and a consumer store in one test would otherwise read each other's events as their own). A test migrates
  two databases from scratch and compares every column, date and time columns left out; what differs must be exactly
  the renewed columns plus two random row keys that never leave a database.
- **Concurrency.** One build per process (`TemplateGate`, which does not remember a failure). Across processes, a
  session advisory lock keyed by the template's name, on a connection without pooling. The schema is migrated in a
  scratch database and renamed to the template's name only when complete, so the name never holds a half-built
  template. The template refuses connections (`ALLOW_CONNECTIONS false`), because one open connection makes every copy
  fail with 55006, and the build ends any backend left in the scratch database before the rename.
- **Leftovers.** A failed build drops its scratch database. Every use stamps the template's database comment, and the
  run's other `eftmpl_` templates unused for a day are dropped; both the stamp and the age use the database clock.
- **From scratch on purpose.** A test of the schema pass itself still migrates an empty database; such a fixture is on
  the reasoned list in `SchemaTemplateGuardTests`.

## Classification (#2069)

584 source files held the blanket key, 199 of them explicitly and 385 by deriving from the base class. Each was
scanned for process-wide statics, registry resets and registrations, `AppContext` switches and environment variables,
listeners (`ActivityListener`, `MeterListener`), cluster-wide SQL (roles, `ALTER SYSTEM`, `pg_stat_reset`,
`pg_stat_activity`, `pg_locks`, fixed advisory keys), use of the shared admin database, raw `CREATE DATABASE`, ports,
files, child processes and wall-clock assertions; every hit was read.

| Outcome | Files | Why |
|---|---|---|
| Parallel, no key | 531 | own database, nothing process-wide that another test can disturb |
| Runs alone (registry reset) | 18 | resets the model registrars, the physical field, hydrator, DbContext registration or initialization registries, the data coalescers, the split-mode hydrators or the stream groups |
| Runs alone (containment switch) | 10 | switches `JsonbContainmentSwitch`, the process-wide mode every perspective query compiles under (found by the first parallel run, not the scan: its setters are named `Set` and `SetMode`) |
| Runs alone (measurement) | 24 | 23 `Benchmark` classes (no shard, never in CI) and one test asserting an unscaled wall-clock latency budget |
| Narrow key | 1 | `BaseUpsertStrategyCoverageIntegrationTests`: two tests register a TTL for the widget model that the others need absent |
| Kept on the blanket key | 0 | |

Since #2167, four of the 18 registry-reset classes run in parallel: `FullLinqSupportTests`, `UnifiedQuerySyntaxTests`,
`ComplexTypeJsonMappingTests` and `PhysicalJsonbContainmentRewriterTests` cleared the physical field registry before
registering models of their own, and now only register. The one rewrite test that clears the registry on purpose moved
to `PhysicalJsonbContainmentRegistryResetTests`, which runs alone.

Shared state that was isolated instead of serialized:

- `ActionTestPerspective` and `RebuildBalancePerspective` counted folds in one process-wide total that tests reset and
  read; they count per stream now.
- Three lock probes counted `pg_locks` or `pg_stat_activity` rows across every database on the server; they are
  scoped to their own.
- 30 fixtures issued their own `CREATE DATABASE`; they use the factory.
- Three query-translation classes shared one static `DbContext` across their tests, which a parallel run broke with
  "An attempt was made to use the context instance while it is being configured"; each test has its own now.
- Three connection tests pointed at a loopback port they had probed and released, which anything could bind before
  the attempt (the develop baseline timed out on it); the port is now a socket bound and not listening, held for the
  test.
- The one deliberate raw `CREATE DATABASE` left (a database that comes up late) runs under the factory's retry.

Not hazards, checked: `ScopeContextAccessor` and `ProcessingModeAccessor` are `AsyncLocal`; the registries tests add
to (`PerspectivePhysicalFieldRegistry`, `SignalTypeRegistry`, `JsonIndexRegistry`, `PerspectiveTtlRegistry`) are
concurrent collections keyed by test-local types; every meter and activity listener filters by its own instrument,
meter or database; created roles carry a random suffix; `NpgsqlConnection.ClearAllPools` only closes idle pooled
connections.

## Capacity: connections, and the disk

**Connections.** `pg_stat_activity` was sampled once a second through full runs of every shard on the build servers.
Most test databases hold 2 to 6 connections; a few tests that start a host with every worker reach 65; two shards
sharing one container never held more than 102 between them. The budget is 20 per running test, two processes per
container and 100 spare: 8 x 20 x 2 + 100 = 420 of the container's 500. `EFCorePostgresParallelism` explains the
limit, and `ParallelismPolicyGuardTests.TheContainer_HasTheConnectionsTheLimitNeedsAsync` checks the sum against the
live container, so a container made by hand or before the rule fails loudly instead of throttling the run.

**The disk was the real shared resource.** Every test drops its database, and since PostgreSQL 15 a `DROP DATABASE`
forces an immediate checkpoint and waits for it, so a parallel shard asks for one every second or so. With durability
on, each one flushes and syncs everything every other test wrote. On one build server, whose disk is a shared volume,
those checkpoints took a median 0.8 s and up to 36 s, and three tests that bound a host's startup at 60 s failed there
while the same shards passed on a server whose checkpoints took milliseconds. The container now runs without `fsync`,
synchronous commit or full-page writes (`SharedPostgresContainer`, applied on every initialization so a container kept
between runs gets them too; `TestIsolation/SharedContainerDurabilityTests` holds it). A test database is thrown away
with its container, so durability bought nothing.

**TUnit's `[Timeout]` covers the test's hooks too.** A test's timeout includes `[Before(Test)]`, which creates and
migrates its database, so setup that slows under load eats into the test's own budget.

## Measured (build servers, November 2026)

Each shard run end to end (container ready to last result) on the build servers, two shards sharing a server's
container at a time. Before: develop, one class at a time. Parallel, durable: the limit of 8 with the container still
syncing to disk. After: parallel, durability off, two runs each.

| Shard | Before | Parallel, durable | After |
|---|---|---|---|
| 1 | 53.1 min | 21.0 min | 11.1, 12.2 min |
| 2 | 58.4 min | 24.5 min | 13.7, 11.5 min |
| 3 | 59.0 min | 22.3 min | 13.4, 11.6 min |
| 4 | 61.4 min | 34.2 min (one test out of time) | 16.4, 14.9 min |
| 5 | 51.6 min | 30.5 min (two tests out of time) | 15.2, 13.9 min |

After the first rebalance (three moves), one more run of every shard took 15.6, 13.3, 11.3, 14.5 and 14.2 minutes,
all green, and a second rebalance on the three runs together (two more moves) brings the attributed loads to 12.1 to
12.9 minutes. The shards were rebalanced on the after runs (`scripts/Measure-TestShards.ps1`'s cost model, which since
#2069 shares each moment among the tests running then).
