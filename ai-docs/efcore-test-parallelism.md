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
4. **A test that resets a registry every database test reads runs alone**: `[NotInParallel]` with no key. A key only
   orders the tests that hold it, and every database test reads these registries, so no key is narrow enough.
5. **A database is created and dropped through `PerTestDatabaseFactory`**, which retries the template contention
   (55006) that concurrent `CREATE DATABASE` produces. Never issue your own.
6. **A read of a cluster-wide catalog view names the database** (`pg_locks.database`, `pg_stat_activity.datname`,
   `current_database()`): every test database has the same tables, and a sibling's lock or session otherwise answers
   for yours.

### How TUnit schedules the three kinds

TUnit 1.x runs a session in phases: every unconstrained test first (in parallel, under the limit), then every keyed
test (tests with different keys in parallel, the same key one at a time, still under the limit), then every keyless
`[NotInParallel]` test, one at a time, alone. A keyed test never overlaps an unconstrained one, but it does overlap
keyed tests with other keys; only the keyless attribute guarantees nothing else is running.

## Enforcement

| Guard | Fails when |
|---|---|
| `TestIsolation/ParallelismPolicyGuardTests` | the base class serializes; any test holds the retired key; a `[NotInParallel]` line has no reason (files older than the rule sit on a list that only shrinks); the assembly limit is missing; the live container's `max_connections` cannot hold the limit |
| `TestIsolation/SharedRegistryResetIsolationTests` | a test resets a registry, coalescer, hydrator or test seam without running alone |
| `TestIsolation/ScratchDatabaseLifecycleGuardTests` | a fixture issues its own `CREATE DATABASE` (two deliberate exceptions) |
| `TestIsolation/BackendTerminationScopeTests` | a test ends backends without naming its database |

## Classification (#2069)

584 source files held the blanket key, 199 of them explicitly and 385 by deriving from the base class. Each was
scanned for process-wide statics, registry resets and registrations, `AppContext` switches and environment variables,
listeners (`ActivityListener`, `MeterListener`), cluster-wide SQL (roles, `ALTER SYSTEM`, `pg_stat_reset`,
`pg_stat_activity`, `pg_locks`, fixed advisory keys), use of the shared admin database, raw `CREATE DATABASE`, ports,
files, child processes and wall-clock assertions; every hit was read.

| Outcome | Files | Why |
|---|---|---|
| Parallel, no key | 541 | own database, nothing process-wide that another test can disturb |
| Runs alone (registry reset) | 18 | resets the model registrars, the physical field, hydrator, DbContext registration or initialization registries, the data coalescers, the split-mode hydrators or the stream groups |
| Runs alone (measurement) | 23 | `Benchmark` classes (no shard, never in CI) and one test asserting an unscaled wall-clock latency budget |
| Narrow key | 1 | `BaseUpsertStrategyCoverageIntegrationTests`: two tests register a TTL for the widget model that the others need absent |
| Kept on the blanket key | 0 | |

Shared state that was isolated instead of serialized:

- `ActionTestPerspective` and `RebuildBalancePerspective` counted folds in one process-wide total that tests reset and
  read; they count per stream now.
- Three lock probes counted `pg_locks` or `pg_stat_activity` rows across every database on the server; they are
  scoped to their own.
- 30 fixtures issued their own `CREATE DATABASE`; they use the factory.

Not hazards, checked: `ScopeContextAccessor` and `ProcessingModeAccessor` are `AsyncLocal`; the registries tests add
to (`PerspectivePhysicalFieldRegistry`, `SignalTypeRegistry`, `JsonIndexRegistry`, `PerspectiveTtlRegistry`) are
concurrent collections keyed by test-local types; every meter and activity listener filters by its own instrument,
meter or database; created roles carry a random suffix; `NpgsqlConnection.ClearAllPools` only closes idle pooled
connections.

## Connection math

See `EFCorePostgresParallelism` and `ParallelismPolicyGuardTests.TheContainer_HasTheConnectionsTheLimitNeedsAsync`
for the numbers the build checks.
