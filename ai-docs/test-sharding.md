# Test sharding

CI splits slow test projects across parallel runners. This is the pattern every test project
follows going forward.

## Why

The test jobs already run in parallel with each other, so CI wall-clock is set by the **slowest
single job**, not the total. Before sharding, PostgreSQL was that job at 23m34s — roughly double
the next — and inside it one project (`Whizbang.Data.EFCore.Postgres.Tests`, ~2,300 tests) was
~87% of the time.

Integration projects also run *sequentially* inside a job because they share containers, and most
of their tests are serialized further by `[NotInParallel]` constraint keys guarding one database
server. Sharding across runners is how parallelism is recovered safely: each runner is its own
process with its own containers, so a constraint key only serializes within a shard.

## Two levels

**Project-level** — one runner per tagged project. Use when a job runs several projects of
comparable size. Service Bus and RabbitMQ work this way; the shard passes `-ProjectFilter`.

**Category-level** — slice a single large project across runners. Use when one project dominates
its job. Each test class declares exactly one `Shard*` category, and the shard passes
`-TestFilter '[Category=ShardN]'`.

Both are driven from `ci.yml` via a `strategy.matrix`, calling the same reusable workflow with
`project-filter` / `test-filter` / `shard-name` inputs.

## Adding a shard category to a project

1. Give every test class exactly one `[Category("ShardN")]`. It composes with existing categories —
   a class can carry `[Category("Integration")]` and `[Category("Shard2")]` together.
2. Balance by **measured cost**, not test count or class count. Run `scripts/Measure-TestShards.ps1`
   (see [Keeping shards balanced](#keeping-shards-balanced)); test counts mislead badly here, because a
   2,259-case matrix that needs no database finishes in under a minute while a 16-test class that
   provisions one per test takes 90 seconds.
3. **Add the coverage guard** (see below). This is not optional.
4. Add the matrix entries to `ci.yml`. `.github/scripts/tests/Measure-TestShards.Tests.ps1` fails if a
   shard category has no matrix leg, or a leg has no classes.

## The guard is the whole safety story

A class with no shard category matches no filter, so it runs in **no shard**. Every job stays green
while those tests silently stop executing — the worst possible failure mode, because nothing
reports it. A class with two shard categories runs twice, wasting a runner.

Every sharded project therefore carries a guard test asserting the whole assembly:
`ShardCoverageGuardTests.EveryTestClass_DeclaresExactlyOneShardCategoryAsync`. Copy it when
sharding a new project.

This is not theoretical. When the EFCore project was first tagged, the guard immediately caught 11
classes the tagging script had missed (it tagged only the first class per file), and later a further
5. Without it, CI would have gone green having quietly stopped running them.

## Artifact naming and the coverage gate

Shards run concurrently and upload artifacts, so names must be unique or they clobber each other.
The reusable workflows suffix both TRX and coverage artifacts with `shard-name`.

`reusable-quality.yml` waits for one coverage artifact per suite leg. The count is **derived** from
the run's own jobs (every leg is a job named `Test · …`), so adding a shard needs no count to bump.

A shard that runs zero tests emits no coverage file, so the gate waits for an artifact that never
arrives and the job times out. `Whizbang.Soak.Tests` is Postgres-tagged but is not an
integration-mode project — it has never run in that job, so it deliberately gets **no shard**.
Confirm a candidate project actually runs before giving it one.

## What the gain looks like

Sharding only pays once a job is dominated by test execution rather than fixed setup. That
condition did not hold until coverage instrumentation moved into the build job — before that,
~11 minutes of every test job was instrumentation (70-75%), each shard re-paid it, and sharding
the RabbitMQ job made it **slower** (14m29 -> 17m55).

After that change the PostgreSQL job is **92.4% test execution** (917s of 992s, ~56s of
downloads and setup), which is what makes splitting it worthwhile:

| | |
|---|---|
| unsharded | 16m36 |
| one shard (631 tests) | 4m58 |

Verify a split with the coverage guard rather than by summing shard totals: the guard proves every
test class carries exactly one shard category, so the shards' union is the whole suite by
construction.

**Check the arithmetic before sharding a job.** Measure its step breakdown first. A job whose time
is mostly artifact download, container startup or instrumentation gets slower when split, because
every shard re-pays that cost while only the test portion divides. RabbitMQ is deliberately left
unsharded for this reason: at 4m52 it is no longer the critical path, and three shards would buy
~2.6 minutes for three extra runners.

## Keeping shards balanced

Shards drift as tests are added, so balance is a routine, not a one-off. The 2026-10 review found
efcore-1 and efcore-3 the slowest suite in 23 of 25 runs, 9 minutes apart from the lightest shard, and,
behind that, an unneeded solution rebuild costing every suite about 8 minutes (#1159).

### The routine

`.github/workflows/test-shard-report.yml` runs on the 1st of each month and posts a report on the
**Test shard balance (monthly report)** issue. Run it by hand any time:

```bash
pwsh scripts/Measure-TestShards.ps1                       # report only
pwsh scripts/Measure-TestShards.ps1 -ShardCount 6 -Apply  # rebalance across six shards and re-tag classes
```

The report has two tables:

- **Suites:** median, p90, max and coefficient of variation of every test job over the last 25
  full-matrix runs, its test-step time, and how often it was the slowest. The slowest suite sets how
  long every pull request waits.
- **Shards:** each shard's measured test window, its load by attributed class cost, the proposed load,
  and the class moves that get there.

### When to act

Act on the report when **either** holds:

1. A sharded suite is the slowest in most runs **and** its shards are more than **2 minutes** apart.
   Rebalance: `-Apply` at the current count, open a PR.
2. Even balanced, the shards' proposed load still leaves them the slowest suite by more than
   **3 minutes**. Add a shard: `-ShardCount <n+1> -Apply`, plus the matrix leg in `ci.yml`.

Also check **job time minus test-step time, and test-step time minus the shard's test window**. Those
are setup: downloads, containers, and anything the runner does before the first test. If it grows,
fix it before resharding, since every shard pays it again. The 8-minute rebuild was found exactly this
way: a test step of 23 minutes around a test window of 15.

After merging a rebalance, the next report should show the shards within tolerance. If it doesn't,
the cost model has drifted from reality; re-check it before moving more classes.

### Why cost is measured, not summed

A shard's time is not the sum of its test durations. In the EFCore shards the test bodies account for
only 17-47% of the window. Each test that needs a database creates one by copying a template, Postgres
serializes those copies on the template's lock, and each shard's container therefore takes roughly
one such test per second while its CPU sits mostly idle.

So the script charges each test the gap from its start to the next test's start. Those gaps sum
exactly to the shard's test window, and a class's cost is the median of its sums over the last five
runs. Rebalancing starts from the current assignment and moves the class that best closes the gap
between the heaviest and lightest shard, never one larger than half of it, so each rebalance is a
small diff of `[Category("ShardN")]` changes rather than a reshuffle.

The serialized database copies are the real ceiling, and sharding only works around them. A pool of
templates, or reusing databases between tests, would raise every shard's throughput and is the next
lever after balance.

