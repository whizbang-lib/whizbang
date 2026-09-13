# The canonical temporal backfill blocks readiness and cannot resume

## What happened

A consumer upgrading from the release before the canonical temporal form to the one after it
deployed fifteen services. Twelve came up. Three did not, for two unrelated reasons, and only one of
them was a code defect.

The code defect was a computed temporal property failing model validation, fixed separately.

The other two services failed their rollout with `context deadline exceeded`, having never become
ready. They are the two holding the most rows.

## Why

The backfill that rewrites a stored rendering into the canonical number runs inside the `Migrate`
startup step, which is deliberately ahead of serving traffic: a column holding both forms cannot be
indexed or range-scanned correctly, so no query may observe a half-converted column.

Each statement is a single unqualified `UPDATE` over the whole table, one per temporal property:

```sql
UPDATE <table> SET data = ... WHERE jsonb_typeof(data -> '<key>') = 'string';
```

That is correct and idempotent, and it is also unbounded. Measured on the deployment above, the
`Migrate` step took 1.8s, 3.5s, 3.5s, 18.4s and 19.0s on the services that succeeded, scaling with
row count as you would expect.

The container's startup probe allows `initialDelaySeconds 5 + failureThreshold 60 x periodSeconds 3`,
so **185 seconds**, inside a 300 second rollout deadline. A table large enough to need longer than
that is killed mid-statement.

**And then it cannot make progress.** The `UPDATE` is one transaction, so being killed rolls it back
entirely. The next attempt starts from zero and is killed at the same point. The deploy retries three
times, rolls back, and the service stays on the old release permanently. No amount of retrying
converges, and nothing in the failure says why: the operator sees a readiness timeout.

## What is wrong with it

Not the ordering. Serving traffic against a half-converted column is worse than being slow to start.

The problem is that an unbounded data rewrite is attached to process startup, where the budget is set
by a probe that knows nothing about how many rows there are, and is written so that partial progress
is impossible. Those three together turn "this upgrade takes a while on a big table" into "this
service can never be upgraded".

## Directions

**Make progress survivable.** Convert in batches that each commit, keyed on the same predicate that
already makes the statement idempotent, so a killed pod resumes where it stopped rather than
restarting. This alone turns a permanent failure into a slow one.

**Say what is happening.** The step should report rows remaining, so an operator reading a readiness
timeout can tell a large backfill from a hang. Right now they look identical.

**Let the operator choose the moment.** A way to run the conversion out of band and have the startup
step find nothing to do. The predicate already supports this: once converted, the statement selects
no rows.

**Consider whether the gate has to be the whole table.** The requirement is that no query observes a
half-converted column, which is about a column being consistent, not about the entire table being
converted before anything serves.

## What was done in the meantime

Nothing automatic. The two services stayed on the previous release; the rest of the fleet moved. That
mixed state is itself worth a note: each service owns its own database, so the stored form of one
does not reach another, but this has not been verified for anything that crosses a service boundary.
