# Schema initialization: connections, transactions, and who migrates

Read this before changing anything in `DbContextSchemaExtensionTemplate.cs`, `SchemaCommandBoundary`,
`SchemaBoundaryConnections`, `SchemaMigrationDeferral`, `AdvisoryLockProbe`, or the SQL the
perspective pass emits.

Traps 1 and 2 each shipped once, and each passed every local test first. Trap 3 is the one that
looks solved and is not: the lock has always excluded correctly, so nothing fails, and the cost is
paid quietly by every replica on every startup.

None of the three is discoverable by reading the code, which is why they are written down here.

---

## Trap 1: ordering statements inside one transaction is not enough

An index over an expression is built by evaluating that expression on **every heap tuple that is not
yet dead**. A row version superseded by an *uncommitted* `UPDATE` is still live, because other
transactions can still see it. So an index built in the transaction that rewrote the column it
indexes is built over the values as they were **before** the rewrite:

```
22P02: invalid input syntax for type bigint: "2026-04-21T22:38:17.357886+00:00"
```

The rewrite is correct. Its ordering is correct. The index still fails.

**Why it is worse than a failed startup.** The rollback undoes the rewrite along with the index, so
every later attempt begins from the state that failed and fails identically. The retry loop never
makes progress, and the service reports only `schema=Migrating` forever. A one-off failure would
have healed on the next deployment; this cannot heal at all.

**The fix.** The generator emits `SchemaCommandBoundary.MARKER` after a rewrite, and the initializer
applies each piece on its own connection, committing before the next begins. A rewrite that has
succeeded then survives a later failure in the same pass, which is what lets a retry get further
than the attempt before it.

### Measured, so nobody has to re-derive it

| Execution shape | Result |
|---|---|
| One multi-statement command (implicit transaction) | **fails** `22P02`, rows unconverted |
| Two commands, autocommit each | passes |
| Two commands, one explicit transaction | **fails** `22P02` |
| Two commands, commit between | passes |

### Two ways a test lies about this

- **`psql -f script.sql` autocommits each statement.** A file-based reproduction passes while the
  application fails. Reproduce through the client the runtime actually uses.
- **A narrow row is updated in place (HOT).** The superseded version never becomes separately
  visible, so the index sees only the rewritten value. A fixture seeded with one small row passes
  while the real table fails. `SchemaCommandBoundaryTests` sets `fillfactor = 100` and seeds
  documents of a realistic size for exactly this reason. Do not "simplify" that away.

---

## Trap 2: there is almost never a connection string to be had

Schema SQL carrying a commit boundary needs a connection it can open **independently** of the one
its transaction is running on. The obvious source is a connection string, and it is usually absent:

- Npgsql redacts the password from every `ConnectionString` surface once a connection has opened
  (`Persist Security Info` defaults to `false`). `dbContext.Database.GetConnectionString()` reports
  the live connection's string, so a second connection opened from it fails with
  `No password has been provided but the backend requires one (in SASL/SCRAM-SHA-256)`.
- Capturing it "early" does not help: a context handed to the initializer has usually been used.
- The turnkey registration configures the context with `UseNpgsql(NpgsqlDataSource)`, so for most
  deployments **there was never a string to redact**.

### And a side connection sees only committed work

The connection is independent, which cuts both ways. It cannot see anything the initializer's own
transaction has done and not committed, including **the schema itself**:

```
3F000: schema "inventory" does not exist
```

The schema is created by the initializer's transaction, so the first statement a side connection
sends into a non-default schema fails. It is created in its own committed statement **before** that
transaction opens, deliberately before the advisory lock is taken: this instance then holds nothing,
so waiting on another instance's in-flight creation is a wait rather than a deadlock. `IF NOT
EXISTS` is not atomic, so `42P06` is tolerated as the expected shape of that race.

**Every test of mine ran against `public`, which always exists, so none of them could fail.** The
sample apps use non-default schemas and caught it in 135 tests across three suites.

**The data source is the thing that still holds the credentials.** `SchemaBoundaryConnections.Resolve`
is the single answer, in this order:

1. the initialization connection string, when the host supplied one, because it addresses PostgreSQL
   directly rather than through a pooler
2. the **context's own** data source, read from `NpgsqlOptionsExtension.DataSource`
3. an `NpgsqlDataSource` in the scope
4. a configured connection string
5. nothing, which the caller reports and then applies the script whole

Step 2 is before step 3 deliberately: a caller that passed no scope still gets a connection, and a
container holding a data source for a different database cannot be used to open the wrong one.

The `VACUUM` path in the maintenance pass had this right long before the boundary existed and now
shares the same function. **If you need an out-of-band connection, call `Resolve`. Do not write a
fourth copy of this decision.**

---

## Why the test suite did not catch traps 1 and 2

The fallback is quiet by construction: applying the script whole succeeds on every database that has
**nothing to rewrite**, which is every database a test creates, and fails only on the databases the
boundary exists for. So:

- a green suite says nothing about whether the boundary works;
- a test that seeds no old-format rows cannot fail;
- and for a while `EFCoreTestBase` passed no scope at all, so the whole project exercised the
  fallback and nothing exercised the path a deployment takes.

What actually guards this now:

| Guard | What it catches |
|---|---|
| `SchemaCommandBoundaryTests.OneTransactionCannotBuildAnIndexOverAValueItJustRewroteAsync` | the characterization, including that the rewrite is undone with it |
| `SchemaCommandBoundaryTests.WithoutTheBoundaryTheSameScriptStillFailsAsync` | the marker becoming decorative |
| `SchemaBoundaryConnectionsTests.TheContextsOwnDataSourceIsUsedWithNoScopeAsync` | the redaction trap, on the shape every deployment uses |
| `CanonicalTemporalBackfillGenerationTests.ACommitBoundarySeparatesTheRewriteFromTheIndexAsync` | the generator dropping the boundary |

---

## Trap 3: the lock says "not yours", not "wait" or "take over"

`pg_try_advisory_xact_lock` elects the migrator: exactly one instance can win it, and the winner does
the DDL. The losers get a single bit back, and that bit does **not** distinguish the two situations
they care about.

| Situation | Right move | Wrong move costs |
|---|---|---|
| The winner is alive and migrating | wait for its result | two instances inside the same DDL |
| The winner's pod died holding it | take the work over | the schema never advances; nothing in the fleet starts |

`pg_locks` separates them, and it does so with **no deadline to tune**: a lock vanishes when its
session ends, cleanly or not. So the loser polls two lock-free questions in this order:

1. **Is the schema current yet?** If so the winner committed, and there is nothing to apply.
2. **Does anything still hold the lock?** Held means alive and working, so keep waiting however long
   that takes. Released, with the schema still behind, means the migrator died.

`SchemaMigrationDeferral.DeferAsync` is that loop; `AdvisoryLockProbe.IsHeldElsewhereAsync` is
question 2.

### The order of the two questions is the whole design

A commit releases the lock **and** marks the schema current in the same instant. Ask about the lock
first and the poll that lands there reads "released", concludes the migrator died, and goes off to
redo a migration with nothing in it, on every replica, on every startup. It is not incorrect, which
is exactly why it would never be noticed.

### The stored-form rewrite waits for the key; it never skips

The rewrite phase (`CanonicalTemporalRewritePhase`) takes the same schema-init key before the DDL
transaction opens, and it is the one place where a lost `pg_try_advisory_xact_lock` means **wait**,
not defer and not take over. The reasoning is the reverse of Trap 3's: here the instance already
knows it is the one responsible (it won the migrator duty, could not be staged, or is a waiter whose
migrator died), and the holder of the key is not necessarily doing the rewrite. A sibling's
bootstrap holds it for the length of its bootstrap transaction and converts nothing; a sibling's DDL
holds it and converts nothing; and a sibling staged as a waiter never rewrites at all. "Skip, the
holder will do it" was therefore a fleet-wide no-op whenever two instances started together, and it
said so only at debug level.

So the phase polls the try-lock with backoff for up to the schema command timeout, in a transaction
of its own (transaction scope for the reason the next section gives), logs once at Information that
it is waiting, and gives up with a warning naming the key. Giving up is safe: every reader tolerates
the older forms, and the next start tries again. The two generated call sites (the migrator path and
the waiter's takeover path) share one body, `rewriteStoredFormsAsync`, so they cannot drift.
`CanonicalTemporalRewritePhaseTests.AnInstanceWaitsForTheLockAndAppliesOnceItIsReleasedAsync` and
`CanonicalTemporalRewriteWiringTests.AWaiterThatTakesOverRunsTheRewriteBeforeTheDdlAsync` are the
guards.

The wait covers the race, not the queue. The budget is the schema command timeout, ten minutes, and
an instance that could not be staged and started while a migrator held the key for a long migration
sat inside the rewrite for the length of it: it logged nothing about deferring and never reached the
deferral that watches the key and reports it, and the three `SchemaInitializationConcurrencyTests`
deferral cases timed out on exactly that. So the generated initializer probes the key before it
rewrites (`AdvisoryLockProbe.IsHeldElsewhereAsync`), and an instance that finds it held goes into the
same `SchemaMigrationDeferral.DeferAsync` a waiter uses, watching the schema key instead of the duty
key. When that wait ends it rewrites either way: a settled no-op under a schema someone else brought
up to date, and the fast path exits; the real thing over a schema whose holder released it still
behind, and the loop then contends for the DDL lock as it always did. The phase's own wait still
absorbs the race where a sibling takes the key between the probe and the rewrite.
`CanonicalTemporalRewriteWiringTests.AnInstanceThatWouldRewriteBehindAHeldSchemaLockWaitsOnTheLockFirstAsync`
pins the order; the deferral cases are the behavior.

### Reassembling the key is not optional

PostgreSQL splits a single-bigint advisory key across `classid` (high 32 bits) and `objid` (low 32).
Every key this framework computes is a full 64-bit FNV-1a hash, so:

```sql
-- WRONG: finds nothing for any negative key, and aliases any two keys sharing a low half
WHERE l.objid::bigint = $1
-- RIGHT
WHERE ((l.classid::bigint << 32) | (l.objid::bigint & 4294967295)) = $1
```

`SchemaInitializationLockKey.Compute("public")` is negative, so the wrong form reports **every**
working migrator as dead on the default schema. The shift is deliberately unchecked; Postgres bit
shifts do not raise on overflow, which is what reproduces the sign bit. Also require
`objsubid = 1` (the two-int32 key shape shares the representation) and the current `database`
(advisory locks are database-local, `pg_locks` is not).

---

## Trap 4: electing a migrator is a cycle, and it bites in two different ways

`StartupDuties.MIGRATOR` is the right mechanism and cannot simply be called. `PgDutyElector`
records a win through `record_capability`, which

- **does not exist** on a fresh database (migration `108_InstanceCapabilities.sql` creates it), so
  `TryAcquireAsync` throws `42883`; and
- returns **false** for an instance that is not in `wh_service_instances`, which at schema-init time
  is every instance, because the heartbeat worker starts *after* the schema is ready. The elector
  reports that as `DutyRefusal.Refused`.

**Treating `Refused` as fatal is a fleet-wide outage.** It reads like "this instance is evicted and
must not do exclusive work", and on an established database it actually means "nobody has
heartbeated yet", which is every instance of every service. That shipped once as a throw.

### The bootstrap, and the three properties that make it safe

`SchemaBootstrapPhase` applies a marked subset first: the core tables, then the regions marked
`-- @whizbang:bootstrap-begin` / `-- @whizbang:bootstrap-end`. `MigratorDutyStaging` then registers
the instance and elects. Three properties, each of which is a test:

1. **It writes no ledger rows.** Making objects exist and claiming to have migrated them are
   different things. If the bootstrap wrote hashes, the ordinary pass would read them, conclude
   those migrations were applied, and skip work the bootstrap only partly did.
2. **It does not assume its own success.** Having applied the scripts it asks the database whether
   an election is now possible (`CanElectAsync`) and reports *that*. An incomplete closure
   therefore costs the election, not the startup.
3. **Nothing about staging is fatal.** A refusal, a missing function, a failed registration, no
   elector registered, an elector that throws: every path ends `Unstaged`, migrating under the
   advisory lock exactly as before. Never migrating needs a human to clear; duplicated work does
   not.

### The bootstrap lock must be transaction scoped, not session scoped

This one is worth stating on its own, because the session-scoped version looks fine and is a
permanent hang waiting to happen.

The bootstrap needs the schema-init key, so an instance bootstrapping and an instance creating
tables exclude each other. Taken with `pg_try_advisory_lock` (session scope) it does not survive a
transaction-pooling front end: each standalone statement is its own transaction, so the lock and the
unlock land on **different server connections**, the unlock misses, and the lock sits on a backend
until that backend resets.

Both advisory scopes share one lock space. So the leaked session lock then blocks the DDL phase's
`pg_try_advisory_xact_lock` on the same key, on every instance, for ever. Nothing migrates the
schema again, and every instance reports only that it is waiting.

`pg_try_advisory_xact_lock` inside one transaction has none of that: a pooler pins the backend for
the transaction's duration, and the server releases the lock on commit **and** on rollback, so there
is no path that leaks it. It also makes the bootstrap atomic, which costs nothing here because every
statement in it is idempotent DDL, none of it needs to run outside a transaction, and a partly
applied bootstrap would be reported as not-ready anyway.

The consumer that would have hit this is one with no `-init` connection configured, where
`SchemaBoundaryConnections.Resolve` falls through to the context's own data source and that points
at the pooler. `AFailedBootstrapLeavesTheLockFreeForTheDdlPhaseAsync` is the guard.

### A current closure is applied nowhere, because idempotent DDL still locks

Every instance start used to apply the closure. Every statement in it is idempotent, and idempotent
is not free: `CREATE INDEX IF NOT EXISTS` on an index that exists takes a share lock on its table
before it finds nothing to do, and the core-tables script carries dozens of them over the hot
tables. An instance an autoscaler started under a bulk load ran that against tables the running
instances were writing and deadlocked with the maintenance sweep and the poll sources inside two
seconds (`40P01`, four of them).

So the closure is recorded. The transaction that applies it also writes the SHA-256 of the scripts it
ran (names and text) to `wh_bootstrap_closure`, a table migration 000's bootstrap region creates, and
an instance starting later computes the same hash over the closure it carries, finds it recorded, and
returns without a statement, a lock, or a wait. A changed closure has a different hash and runs in
full once. The three properties above hold: the ledger is still untouched (`wh_bootstrap_closure` is
not the ledger), the election is still probed rather than assumed, and a record that cannot be read
falls through to applying, never to skipping. `ACurrentClosureIsNotAppliedAgainAsync` and
`AChangedClosureIsAppliedAsync` are the guards.

### The closure is not what the migration headers say

Three of the four headers are wrong or incomplete, so derive it from the SQL and never the comments:

| Needed | Where it actually comes from | What the headers say |
|---|---|---|
| `wh_service_instances` | `PostgresSchemaBuilder.BuildInfrastructureSchema`, **no migration at all** | 010 "requires wh_service_instances", without saying who creates it |
| `drop_all_overloads` | `000_MigrationTracking.sql` | 010 "Dependencies: 001-009" |
| `wh_instance_evictions` | `106_InstanceEvictionFencing.sql` | 108 "106", correctly |
| `record_capability` | `108_InstanceCapabilities.sql` | 106 points *forward* at 029 |

`106` is in the subset for its **table only**. It also redefines `cleanup_stale_instances` and
`record_heartbeat`, which reach objects the bootstrap deliberately does not create, and that is why
bootstrap is a marked *region* rather than a marked file.

**The only honest test of a closure is an empty database.**
`SchemaBootstrapPhaseTests.AnEmptyDatabaseCanElectAfterTheBootstrapAsync` creates its own database,
runs only the bootstrap, then registers an instance and calls `record_capability` for real. It takes
the script list from the generated initializer rather than rebuilding it, so the markers, the order
and the schema transform under test are the ones that ship. Removing any single marker turns it red.

### What a non-holder watches

The **duty** lock key, not the schema lock key. Watching the schema key would report the migrator as
gone the moment it finished its own bootstrap and before it started migrating.
`Staged_ANonHolderWaitsForTheDutyHolderAsync` is also the only thing that proves the two sides agree
on that key: the elector derives it from the notification connection's search path and the
initializer derives it from the DbContext schema, and nothing but a test makes those the same.

### What guards this

| Guard | What it catches |
|---|---|
| `AdvisoryLockProbeTests.TheRealSchemaKeyIsFoundEvenThoughItIsNegativeAsync` | the half-key predicate, on the key every default deployment uses |
| `AdvisoryLockProbeTests.KeysSharingALowHalfDoNotAliasAsync` | the aliasing half of the same bug |
| `AdvisoryLockProbeTests.ATransactionScopedLockReadsAsHeldAsync` | a probe that only sees session locks |
| `SchemaMigrationDeferralTests.ACommittedMigratorIsNotMistakenForADeadOneAsync` | the two questions asked in the wrong order |
| `SchemaMigrationDeferralTests.AMigratorThatDiesMidWaitIsNoticedAsync` | detection that stops working once the backoff settles |
| `SchemaMigrationDeferralTests.AFailedLockProbeContendsForTheLockAsync` | waiting on a condition that can no longer be observed |
| `SchemaInitializationConcurrencyTests.Deferral_WhenTheMigratorCommits_AppliesNothingItselfAsync` | the deferral falling through and migrating anyway |
| `SchemaInitializationConcurrencyTests.Deferral_WhenTheMigratorIsKilled_TakesTheWorkOverAsync` | a stranded fleet after an OOMKill |
| `SchemaMigratorDeferralGenerationTests` | the generator not emitting the call at all |
| `SchemaBootstrapPhaseTests.AnEmptyDatabaseCanElectAfterTheBootstrapAsync` | an incomplete bootstrap closure, from an empty schema |
| `SchemaBootstrapPhaseTests.WithoutTheBootstrapThereIsNothingToElectWithAsync` | the cycle itself, so the bootstrap is not solving a problem nobody had |
| `SchemaBootstrapPhaseTests.AnUnregisteredInstanceIsRefusedACapabilityAsync` | registration ordering, against the real function |
| `SchemaBootstrapPhaseTests.ATombstonedInstanceIsStillRefusedAsync` | the eviction fence surviving being reached this early |
| `SchemaBootstrapPhaseTests.TheBootstrapRecordsNothingInTheLedgerAsync` | the bootstrap claiming to have migrated what it only created |
| `SchemaBootstrapPhaseTests.AFailedScriptReportsNotReadyRatherThanThrowingAsync` | a bootstrap failure becoming a startup failure |
| `SchemaBootstrapPhaseTests.AFailedBootstrapLeavesTheLockFreeForTheDdlPhaseAsync` | a leaked bootstrap lock blocking every migration on the schema for ever |
| `SchemaBootstrapPhaseTests.AFailedScriptRollsBackTheWholeBootstrapAsync` | a half-applied bootstrap left behind to confuse the next instance |
| `MigratorDutyStagingTests.ARefusedInstanceMigratesUnderTheLockRatherThanThrowingAsync` | the fleet-wide outage that shipped once |
| `MigrationBootstrapRegionsTests.TheEvictionMigrationContributesItsTableAndNotItsFunctionsAsync` | bootstrap growing from a region into a whole file |
| `MigrationBootstrapRegionsTests.EveryShippedMigrationHasBalancedMarkersAsync` | a mistyped marker silently resizing the bootstrap |
| `Staged_ANonHolderWaitsForTheDutyHolderAsync` | the elector and the waiter disagreeing about the duty lock key |
| `Staged_AKilledDutyHolderIsTakenOverAsync` | a fleet stranded on a duty holder that no longer exists |
| `Staged_TheMigratorRegistersAndThenReleasesTheDutyAsync` | a leaked duty, which would stall the next deployment |

The integration witness is `wh_schema_migrations.updated_at`. Re-applying a migration stamps it
`NOW()`, so a sentinel timestamp surviving the run is proof the deferring instance applied nothing,
which no row count and no absence of an exception can establish.

---

## Checklist for a change in this area

0. **Does more than one instance reach it at startup?** The advisory lock elects one migrator; the
   losers must defer to it, not re-contend. Never key a decision on the lock's absence alone -
   ask whether the schema is current first.
0b. **Does it decide who migrates?** Nothing in that decision may be fatal. Every failure path ends
   with this instance migrating under the advisory lock, because never migrating needs a human to
   clear and duplicated work does not. `DutyRefusal.Refused` in particular means "not in the
   registry yet" far more often than it means "evicted".
0c. **Did a migration gain a dependency?** If anything in a bootstrap region now references a new
   object, that object joins the closure. `AnEmptyDatabaseCanElectAfterTheBootstrapAsync` is what
   catches it; the migration headers are not reliable and three of them are already wrong.
0d. **Does it run before the DDL transaction and need the schema key?** Then a lost try-lock means
   wait, not skip: the holder may be a sibling's bootstrap or DDL, which does not do your work, and a
   sibling staged as a waiter never will. Test it with two instances, one holding the key in an open
   transaction, and assert something above debug level is logged on the path that did not run.
1. **Does it need a connection of its own?** Call `SchemaBoundaryConnections.Resolve`. Never open one
   from `GetConnectionString()`.
2. **Does one statement depend on another's committed effect?** Ordering is not enough. Emit a
   boundary.
3. **Does the test seed data in the old shape, wide enough to defeat an in-place update?** If not, it
   proves nothing.
4. **Does anything exercise a non-default schema?** `public` always exists, so a test against it
   cannot see a side connection that is unable to address the schema. The ECommerce sample suites
   (`InMemory`, `RabbitMQ`, `ServiceBus`) are the only coverage of that, and they are the only
   reason this was found before the next deployment.
5. **Run the whole `Whizbang.Data.EFCore.Postgres.Tests` project, not a filter.** The first version of
   the connection fix passed 6 targeted tests and failed 1780 in the full suite.
6. **Look for the existing pattern before adding one.** Both traps already had a correct answer
   elsewhere in the repo (the maintenance `VACUUM` path, and the notification workers' "borrow the
   data source" comment) when they were solved wrongly a second time.
