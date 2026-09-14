# Schema initialization: connections, transactions, and the two traps

Read this before changing anything in `DbContextSchemaExtensionTemplate.cs`, `SchemaCommandBoundary`,
`SchemaBoundaryConnections`, or the SQL the perspective pass emits.

Both traps below shipped once. Both passed every local test first. Neither is discoverable by reading
the code, which is why they are written down here.

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

## Why the test suite did not catch either of these

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

## Checklist for a change in this area

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
