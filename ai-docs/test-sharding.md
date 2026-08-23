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
2. Balance by test count, not class count. Greedy longest-processing-time bin-packing over per-class
   `[Test]` counts gets within a few tests per shard.
3. **Add the coverage guard** (see below). This is not optional.
4. Add the matrix entries to `ci.yml` and bump `coverage-artifact-count`.

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

`reusable-quality.yml` waits for exactly `coverage-artifact-count` artifacts before running analysis.
**Every shard added must increment it**, and the count must equal the number of shards that actually
produce coverage. Current expectation:

| job | shards |
|---|---|
| Unit | 1 |
| PostgreSQL | 5 (efcore ×4, dapper) |
| InMemory | 1 |
| RabbitMQ | 3 |
| Service Bus | 2 |
| AzureBlob | 1 |
| **total** | **13** |

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
