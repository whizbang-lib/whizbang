# The canonical temporal backfill blocks readiness and cannot always resume

> **Corrected.** An earlier version of this file said the backfill was what stopped two services
> coming up during a consumer upgrade. That was wrong, and the measurements below are what disproved
> it. The hazard described here is real but **latent**: it has not yet been observed to cause a
> failure. The original text asserted a cause from a plausible fit rather than from evidence, which
> is the mistake worth remembering as much as the finding.

## What actually happened

A consumer upgrading from the release before the canonical temporal form to the one after it
deployed fifteen services. Twelve came up. Three did not, and none of them was this.

- One failed because a computed temporal property was configured as if Entity Framework could map
  it, which fails model validation and takes the process down. Fixed; that is a real defect.
- One failed once and came up on the next attempt with `Assess` completing in 112ms, so whatever
  stopped it was not in this path. Its cause was never established, and saying so is better than
  picking the nearest theory.
- One is blocked by a nested record in a complex collection whose constructor Entity Framework
  cannot bind. Unrelated to storage form.

## The hazard, stated as what it is

The backfill that rewrites a stored rendering into the canonical number runs inside the `Migrate`
startup step, deliberately ahead of serving traffic: a column holding both forms cannot be indexed or
range-scanned correctly, so no query may observe a half-converted column.

Each statement is a single unqualified `UPDATE` over the whole table, one per temporal property:

```sql
UPDATE <table> SET data = ... WHERE jsonb_typeof(data -> '<key>') = 'string';
```

Correct and idempotent, and also unbounded.

**Measured.** The `Migrate` step took 1.8s, 3.5s, 3.5s, 18.4s, 19.0s and 30.6s across the services on
that upgrade, scaling with row count. The container's startup probe allows
`initialDelaySeconds 5 + failureThreshold 60 x periodSeconds 3`, so **185 seconds**, inside a 300
second rollout deadline.

So the largest observed conversion used about a sixth of the budget. There is headroom today. The
concern is that nothing connects the two numbers: the budget is set by a probe that knows nothing
about how many rows there are, so the margin is unmeasured and shrinks as data grows.

**And partial progress is not guaranteed to survive.** A kill landing mid-statement rolls the whole
statement back, so that table's conversion restarts from zero on the next attempt. A kill landing
after a commit keeps that property's work. Which of those you get depends on where in the sequence
the probe's patience runs out, and a table big enough to exceed the budget inside a single statement
can never finish, however many times the deploy retries.

## What is wrong with it

Not the ordering. Serving traffic against a half-converted column is worse than being slow to start.

The problem is that an unbounded data rewrite is attached to process startup, where the budget is set
by a probe with no knowledge of the work, and is written so that partial progress may be impossible.

## Directions

**Make progress survivable.** Convert in batches that each commit, keyed on the same predicate that
already makes the statement idempotent, so a killed pod resumes where it stopped.

There is a constraint here that the single statement satisfies by accident and a batched one does
not. A rolling update keeps the previous release serving until the new pod is ready, and the previous
release reads the rendering. One transaction is invisible to those readers until it commits, so they
keep working throughout; a batched conversion commits as it goes, and they would start reading
converted rows they cannot parse. Anything batched therefore needs the old readers to tolerate both
forms, which is a compatibility question about the release before it rather than a detail of the
rewrite. That is the real reason this is not a small change.

**Say what is happening.** The step should report rows remaining, so an operator reading a readiness
timeout can tell a large backfill from a hang. Right now they would look identical, which is exactly
why this file originally blamed the backfill for two failures it had nothing to do with.

**Let the operator choose the moment.** A way to run the conversion out of band and have the startup
step find nothing to do. The predicate already supports it: once converted, the statement selects no
rows.

**Consider whether the gate has to be the whole table.** The requirement is that no query observes a
half-converted column, which is about a column being consistent, not about the entire table being
converted before anything serves.
