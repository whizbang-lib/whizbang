# MC/DC-Style Coverage Program (SQLite Model)

**Status**: Planned (not started)
**Researched**: 2026-09-05; facts re-verified against `origin/develop` on 2026-09-18
**Owner**: TBD

## Context

Inspired by Richard Hipp's talk "Reliability Lessons From SQLite" (SSW 2026). SQLite claims 100% MC/DC without an MC/DC tool: it measures 100% branch coverage of compiled code and uses `ALWAYS()`/`NEVER()` macros for defensive branches. The same argument holds for .NET. C# `&&` and `||` compile to separate IL branch instructions, so 100% IL branch coverage gives condition/decision coverage for every subterm, and with short-circuit masking it approximates masking MC/DC. Stryker's logical mutators (`&&` to `||`, condition removal) supply the "each condition independently affects the outcome" check that branch coverage alone cannot.

### Verified baseline

- Local merged report (`coverage-report/Summary.txt`, 2026-09-05): Line 97.1%, **Branch 84.9% (41,392 of 48,731)**, Method 97.3%, Full-method 90.6%.
- The collector (Microsoft.Testing.Extensions.CodeCoverage 18.3.2, settings in `codecoverage.config`) already emits per-line `condition-coverage` into cobertura, and reportgenerator already prints branch totals. No new collector is needed.
- The only enforced CI gate is **line coverage >= 80** (`.github/workflows/reusable-quality.yml`, gate step ~:271-288). Branch coverage is claimed in older plan docs but never measured or gated.
- Stryker.NET 4.14.0 is installed but inert: one config (`tests/Whizbang.Core.Tests/stryker-config.json`, `break: 0`), manual `workflow_dispatch` only, not in `ci.yml`.
- Sonar (`sonar.config:73`) excludes all six generator projects from coverage, while the CI gate does not. The two gates measure different denominators.
- Per-assembly branch gaps (edges): Core 3,343; EFCore.Postgres 920; Generators 858; EFCore.Postgres.Generators 361; Data.Postgres 326; Transports.AzureServiceBus 232; Dapper.Postgres 195; RabbitMQ 143. Src-gated total is about 6,680 edges.

### Decision census (src/, hand-written code)

- About 1,819 compound decisions (2,219 `&&`/`||` operators): 84% are 2-term, 209 are 3-term, 78 are 4 or more terms.
- Concentrated in Whizbang.Core (837 boolean lines) and Whizbang.Generators (529 boolean lines, of which about 49 sit inside emitted-code string literals in `src/Whizbang.Generators/Templates/` and never execute as library IL).
- 14 `catch ... when (...)` exception filters with compound conditions.
- SQL: 23,155 lines of plpgsql across 129 migrations in `src/Whizbang.Data.Postgres/Migrations/`; 210 functions, 371 `IF` (73 compound), 31 `ELSIF`, 115 `CASE WHEN`, about 2,600 `AND`/`OR` tokens inside query predicates.

## Decisions (locked)

1. **End state**: ratchet branch coverage to 100% across all shipped `src/` projects (the full SQLite program).
2. **Wave 1**: build the machinery and write truth-table tests for the decision hotspots.
3. **Stryker**: both a PR changed-code lane (`--since` the PR base ref) and scheduled full runs with break thresholds.
4. **SQL**: fully included; phased, pilot first.
5. **Test runner**: full rewrite of `scripts/Run-Tests.ps1` (2,892 lines) into a C# tool. TUnit stays as the test framework.
6. **New libraries**: CsCheck, SharpFuzz, Verify.SourceGenerators now; Coyote evaluated later.
7. **Untestable paths**: defensive branches become anomaly-handling paths (report, log, metric), so most become testable and production gains detection of unforeseen states. Only provably undrivable branches are excluded, with justification.

## Limits of the metric (document these; they bound the claim)

- Bitwise `&` and `|` on bools emit no branch and are invisible to branch coverage. Convention: never use them to dodge coverage (grep check in the gate).
- EF Core `IQueryable` lambdas never execute as IL, so branch coverage is blind there. Behavioral coverage for those belongs to the SQL/integration side; each affected file documents its carve-out.
- Pattern matching (`is not T`, property patterns, switch expressions) compiles to real branches and is measured normally.
- Async state-machine and iterator branch noise may inflate the denominator. A timeboxed spike quantifies it; per-assembly floors absorb it until a policy is set.

## Pillar 1: Whizbang.TestOps (C# tool; full runner rewrite)

New `tools/Whizbang.TestOps` (net10 console, following `tools/Whizbang.CLI` conventions) with its own test project `tests/Whizbang.TestOps.Tests` (strict TDD; tools/ sits outside the src coverage gate but is tested to repo standards). Subcommands:

- `testops run`: full port of Run-Tests.ps1 (modes All/Ai/AiUnit/AiIntegrations/Unit/Integration, `--treenode-filter` passthrough, unit-parallel and integration-sequential ordering, fail-fast semantics including the force-off under coverage, project and test filters, history and JSON output, sharding).
- `testops coverage gate|report|ratchet`: parses reportgenerator `Summary.json` and raw cobertura; enforces floors; prints a per-class **branch** worklist sorted by absolute gap (not percent, so a 50%-of-2 class does not outrank a 90%-of-400 class); `ratchet` rewrites the floors file deterministically (raise only; `--force` to lower, with a justification in the PR).
- `testops mutate`: wraps dotnet-stryker and replaces `scripts/mutation/run-mutation-tests.ps1` (`--since <ref>`, `--break-at <n>`, config discovery via `tests/*/stryker-config.json`). After every run, verify a clean `git diff` on source; a timed-out run can leave a mutant in place.
- `testops sqlcov report|gate`: aggregates the SQL coverage dumps (Pillar 5), maps function-body line numbers back to migration files (the last redefinition of a function wins; `__SCHEMA__` substitution is intra-line so line counts are preserved), renders markdown, and enforces the SQL ratchet manifest.
- `testops fuzz`: SharpFuzz lane orchestration (Pillar 6).

Migration path: Run-Tests.ps1 becomes a thin shim that delegates to `dotnet run --project tools/Whizbang.TestOps -- run ...`, so CI and existing habits keep working. Before deleting the shim, run old and new side by side on identical modes and filters and compare the selected test sets and exit codes.

Every subcommand lands wired into its caller (CI or the shim) in the same PR. No subcommand ships with tests and no caller.

## Pillar 2: Ratcheted branch gate (one denominator)

- **Denominator**: the merged reportgenerator report over shipped `src/` assemblies, hand-written code only. Use identical file filters in CI and locally: `-*.g.cs;-*.Generated.cs;-**/.whizbang-generated/*;-**/.whizbang/cache/*;-*/tests/*;-*/tools/*`. Generator projects are **included** (shipped, unit-testable, 86.2% branch today). Remove the six generator entries from `sonar.coverage.exclusions` in `sonar.config:73`. tools/ stays outside the gate.
- **Floors file**: new repo-root `quality-floors.json` holding `global { line, branch }` and per-assembly `{ branch, defensiveBranches }` for **all** gated src assemblies (prevents a regression in one project hiding behind headroom in Core). Seed at measured minus 0.2 points. Advance manually with `testops coverage ratchet` in the PR that improves coverage (review-visible, no auto-bump). The gate prints a "floor can be raised" notice when measured exceeds a floor by more than 0.5 points.
  - **Name trap**: do not call it `coverage-floors.json`. `.gitignore:173` has `coverage*.json` and `.gitignore:432` has `coverage[0-9\-a-zA-Z]*`, both matching by name anywhere in the tree, so a `coverage-*` file would be silently ignored and never committed. Run `git check-ignore -v` on every new file this program adds.
- **CI** (`reusable-quality.yml`): align `FILE_FILTERS` (~:243); add `JsonSummary` to the `-reporttypes:TextSummary` invocation (~:265); replace the grep-and-`THRESHOLD=80` step (~:271-288) with `testops coverage gate --enforce --github-annotations`. An assembly missing from a full-run report means a test job died, which fails the gate. `ci.yml` is untouched (`coverage-artifact-count: 11` stays).
- **Local**: `testops run --coverage` prints the branch summary and the per-class branch worklist, enforces floors only on full runs, and is advisory on filtered or partial runs.

## Pillar 3: Invariant and anomaly seam (SQLite ALWAYS/NEVER, turned into detection)

New `src/Whizbang.Core/Diagnostics/Invariant.cs`, `InvariantViolationException`, and an `IAnomalySink`, all test-first.

- `Invariant.Require(bool condition, string justification)`, marked `[DoesNotReturnIf(false)]`, `[DebuggerHidden]`, `[StackTraceHidden]`, `[ExcludeFromCodeCoverage]`: replaces existing guard-then-throw sites. The branch moves into the excluded helper, so the caller's denominator genuinely shrinks. Behavior is unchanged because the original also threw.
- `Invariant.Never(bool condition, string justification)` for defensive fallbacks that must keep their production behavior. It is not silent: when the "impossible" happens in a release build it reports through `IAnomalySink` (default sink: a structured log naming what failed and the consequence, plus an OpenTelemetry counter via Whizbang.Observability; registered by `AddWhizbang`). Test fixtures register a throwing sink, and DEBUG builds throw. This makes most defensive branches **drivable** in tests (inject the anomalous state, assert the report), so they get covered instead of excluded.
- **Accounting**: every `Never` call site carries a `// BRANCH-NEVER: <reason>` comment. `testops coverage gate` checks that the marker count per project equals `defensiveBranches` in `quality-floors.json`; drift fails the gate. The terminal target per assembly is `(total - defensiveBranches) / total`, and the gate prints both raw and adjusted percentages.
- No `codecoverage.config` change is needed: `DebuggerHiddenAttribute` and `ExcludeFromCodeCoverageAttribute` are already attribute-excluded. Verify in the pilot that the Invariant class is absent from `Summary.txt`.
- netstandard2.0 generator projects cannot reference Core. Add an internal copy in `Whizbang.Generators.Shared` only when a generator first needs it.
- Misuse prevention in wave 1: a mandatory constant justification string, the review rule added to `ai-docs/coverage-exclusions.md`, and the drift check. A small Roslyn analyzer is a later option.
- Deferred experiment (documented, not wave 1): a `WHIZBANG_COVERAGE` symbol that const-folds `Never` out of instrumented builds, which is SQLite's literal mechanism. Rejected for now because the coverage build must be the same IL that ships.
- The helper lands with real call sites: 2 or 3 exemplar conversions in Core in the same PR.

## Pillar 4: Truth-table tests and Stryker independence

**Pattern**: a sibling `<Class>McDcTests.cs` in the class's existing test project, `[Category("McDc")]`, one `[Test]` per decision and one `[Arguments(...)]` row per masked-MC/DC vector. The last two arguments are always the expected outcome and a `vectorId` string that names the independence pair. For an N-term `&&` chain that is N+1 vectors: all-true, plus one first-false vector per position. The strategy doc gives the recipe for AND-of-OR shapes.

**RED protocol for coverage tests** (keeps strict TDD honest when the behavior is already correct): before writing vectors, run `testops mutate --project <P> --mutate "**/<File>.cs"` and record the surviving condition mutants. Every vector must kill a survivor or cover a previously uncovered branch edge; before and after counts go in the commit message. Ask of every vector what would have to be true for it to fail. A vector that genuinely fails is a real bug and goes through normal RED/GREEN.

**Worked example** (`src/Whizbang.Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs:184`): a 6-condition short-circuit AND (A filter wired, B payload present, C not a body-claim payload, D not a composite event, E not handled locally, F not an absorbed namespace). True means ack-and-drop with reason `NO_LOCAL_CONSUMER`; false means process. Seven vectors: all-true, then A=F, B=F, C=F, D=F, E=F, F=F each with all earlier conditions true. `AsbReceiveAction` is an enum, so it is a legal attribute argument; the class is internal and the ASB test project already has access.

**Wave 1 hotspots** (about 90 to 100 vectors in 15 to 20 test methods):

| File | Decision(s) |
|---|---|
| `Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs` (pilot) | 6-term AND at :184; 3-term AND at :117; `_isAbsorbedNamespace` |
| `Core/Security/ScopeDelta.cs` | 7-condition guard :139; 4-condition equality :386; `HasChanges` :83 |
| `Core/Workers/ClaimWorker.cs:436` | 5 conditions; the `?? false` counts as a condition |
| `Generators/CompileTimeMessageClassification.cs:148` | 6-term OR inside a LINQ `All` lambda (measurable IL); add an empty-collection vector |
| `Data.EFCore.Postgres/BaseUpsertStrategy.cs:482-487` and mirror `EFCoreWorkCoordinator.cs:1101` | nested parenthesized groups |
| `Core/Tags/TagOptions.cs:158,183` | AND-of-OR |
| `Core/Workers/IntegrityAuditWorker.cs:160` | OR over a 3-term AND |
| `Data.Postgres/PostgresDeadlockRetry.cs:55-87` and Core `catch ... when` filters | throw matching and non-matching exceptions in the right state |
| `Core/Versioning/SemanticVersion.cs:134` | chained ternary with a compound first condition |

**Stryker rollout**:

- New configs: `tests/Whizbang.Transports.AzureServiceBus.Tests/stryker-config.json` and `tests/Whizbang.Generators.Tests/stryker-config.json`. Verify Stryker behaves on the netstandard2.0 generator project first; if it does not, keep wave 1 to Core and ASB and file a follow-up. `coverage-analysis: "off"` and `concurrency: 1` stay (documented TUnit/MTP limitation in `ai-docs/mutation-testing.md`).
- Thresholds: the first full run per project sets the baseline, then `break = floor(baseline) - 2`, `low = break + 10`, `high = 85`. Never set a break value before a baseline exists.
- Scheduled lane: `mutation.yml` gains a weekly `schedule` (Monday 03:00 UTC) and a matrix over configured projects; full runs, config break enforced.
- PR lane: new `mutation-pr.yml`, path-filtered to the configured projects, running `testops mutate --since origin/${{ github.base_ref }} --break-at 80` (always the PR base ref, never a hardcoded `develop`; checkout with `fetch-depth: 0`). 45-minute timeout. `continue-on-error` for the first week, then blocking. Escape hatch for equivalent mutants: a config-scoped `ignore-mutations` entry with a comment explaining why.

## Pillar 5: SQL and plpgsql

**Where the line is**: "If it changes which plpgsql statement runs next, it is a decision: vector it and measure it. If it changes which rows a statement touches, it is a partition: seed one row per boundary and assert the row set." The AND/OR tokens inside declarative WHERE and JOIN predicates are not MC/DC targets. Hot DML filters get partition tests instead: one row satisfying every conjunct plus one row violating exactly one conjunct, for each conjunct.

**Measurement**: the `plpgsql_check` extension (2.10.x). `plpgsql_coverage_branches(regprocedure)` and `plpgsql_coverage_statements(regprocedure)` give the numbers; `plpgsql_profiler_function_statements_tb()` gives the missed-branch drill-down. It needs `shared_preload_libraries=plpgsql_check` (Npgsql pooling spans sessions), and `plpgsql_check.profiler = on` is set with `ALTER DATABASE` on coverage databases only. Every other test database and production are untouched, and shipped migration SQL does not change at all.

**Infrastructure**:

- `docker/test-postgres/Dockerfile`: `FROM pgvector/pgvector:pg17` plus `apt-get install postgresql-17-plpgsql-check`. Published to GHCR by a new `docker-test-images.yml` workflow and pinned by digest; a local build fallback for offline work.
- `src/Whizbang.Testing/Containers/SharedPostgresContainer.cs`: make the image overridable (`WHIZBANG_POSTGRES_IMAGE`; today it is the constant `pgvector/pgvector:pg17` at :38), add the preload flag when the new image is selected, and fix the stale-container trap (the container is reused by name only, so add an image check and recreate on mismatch).
- New `tests/Whizbang.Data.Dapper.Postgres.Tests/SqlCoverage/`: `SqlCoverageTestBase` uses a per-class database, because profiles are keyed by function OID and disappear when the database is dropped. It creates the extension, sets the setting, and dumps per-function JSON to `TestResults/sql-coverage/` in `[After(Class)]` before teardown.
- `src/Whizbang.Testing/Sql/McdcSuite.cs`: a structural check that a declared vector set satisfies masking MC/DC over the achievable truth table. plpgsql NULL-guard pairs such as `x IS NOT NULL AND f(x) > 0` are coupled, so classical unique-cause MC/DC is impossible; the achievable vectors are encoded explicitly.
- One test runs `plpgsql_check_function()` static analysis over all 210 functions. It catches type errors and bad references in branches no test has ever executed, and is likely to find real bugs on day one.
- SQL vector tests follow the repo's SQL standards: the same per-function docs and tests tags as C# code, and the migration rules in `src/Whizbang.Data.Postgres/Migrations/README.md`.

**Gate**: per-function branch coverage over functions listed in a tighten-only manifest (`scripts/sql-coverage-baseline.json`), enforced by `testops sqlcov gate` in a new `[Category=SqlCoverage]` shard of `reusable-test-postgres.yml` with the image override set.

**Pilot vectors**: `flush_completions` in `029_ProcessWorkBatch.sql:1301`, shaped `(A AND B) OR (C AND D)` with NULL-guard pairs, giving 5 achievable vectors: cursors populated with ids null (true); cursors empty (false); both null (false); ids seeded (true); ids empty (false, because `array_length` of an empty array is NULL). Each vector asserts the outcome and the side effects (row stamped and cursor advanced, or untouched). Companions: `044_PurgeOrphanInbox.sql` (3 vectors on the line-26 guard plus a 4-row partition test on the 3-conjunct DELETE filter), the `commit_handler_result` guards in 029, and the `claim_work` empty guard.

**Phases**: 0 infrastructure and report-only pilot; 1 gate the pilot functions at 100%; 2 hotspot files one per PR (all functions in 029, then 126, 087, 115, 092, 072); 3 the long tail by decision count, plus a boy-scout rule that any PR touching a migration function brings it into the gate; 4 optional whole-suite profile aggregation, report-only permanently.

## Pillar 6: Complementary libraries

- **CsCheck** (wave 1): property-based tests on pure decision logic, starting with `ScopeDelta` merge and equality, `SemanticVersion` ordering, and `EventTypeMatchingHelper`. Pinned in `Directory.Packages.props`.
- **Verify.SourceGenerators** (wave 1): snapshot suites in `Whizbang.Generators.Tests`, which pin the emitted code including the `Templates/` string-literal blind spot.
- **SharpFuzz** (wave 1, separate lane, not in the unit suite): targets envelope JSON deserialization, `SemanticVersion` parsing, the body-claim wire headers, and type-name formatting inputs. Run manually or on a schedule via `testops fuzz`.
- **Coyote** (deferred): evaluation spike for `ClaimWorker` and `PerspectiveWorker` concurrency, noted in the strategy doc.
- **Bogus** (existing): generates anomalous payloads for anomaly-path and partition tests. MC/DC vectors stay hand-picked exact values.

## PR sequence

Every PR branches from `origin/develop`, goes through review, and has its CI monitored. Nothing is pushed to `develop` directly.

- [ ] **PR 1** `feature/testops-coverage-gate`: TestOps skeleton, `coverage gate|report|ratchet` (test-first), `quality-floors.json` seeded from `Summary.json`, `reusable-quality.yml` edits, the `sonar.config` denominator fix; Run-Tests.ps1 prints branch coverage and calls the tool to gate. Includes the async-noise spike.
- [ ] **PR 2** `feature/invariant-anomaly`: `Invariant`, `InvariantViolationException`, `IAnomalySink` (RED first), 2 or 3 exemplar conversions in Core proving the accounting loop end to end, `coverage-exclusions.md` cross-link.
- [ ] **PR 3** `feature/mcdc-hotspots`: AsbReceiveDecisionMaker pilot, then the remaining hotspots, one commit per file with before and after mutant counts; first ratchet of the floors in the same PR.
- [ ] **PR 4** `feature/stryker-lanes`: `testops mutate`, the two new Stryker configs, the scheduled matrix, `mutation-pr.yml`; record baselines, then set break values.
- [ ] **PR 5** `feature/sqlcov-pilot`: Docker image and GHCR workflow, `SharedPostgresContainer` changes including the stale-container fix, `SqlCoverageTestBase` and collector, `McdcSuite`, `testops sqlcov`, the all-functions static-analysis test, pilot vectors; report-only first, then gate the pilot functions.
- [ ] **PR 6** `feature/testing-libs`: CsCheck, Verify.SourceGenerators, and the SharpFuzz lane, each with one exemplar suite.
- [ ] **PR 7** `feature/testops-run-port`: full `testops run` port; Run-Tests.ps1 becomes a delegating shim; parity check; CI switched; shim removed after burn-in.
- [ ] **PR 8** `feature/mcdc-docs`: `ai-docs/branch-coverage-mcdc.md` (theory, limits, conventions, recipes), the SQL discipline section beside `Migrations/README.md`, registration in `ai-docs/README.md` and `CLAUDE.md`, `mutation-testing.md` updates. Doc fragments can land earlier with their PRs; this PR consolidates.

## Later waves (burn-down order)

- **Wave 2**: bring the small-gap assemblies to 100% and freeze their floors there (Data.Schema 1, SignalR 1, Observability 3, Transports.FastEndpoints 1, Transports.Mutations 2, Generators.CodeFixes 5, Hosting.AspNet 13, Data.Dapper.Custom 12, Transports.HotChocolate 16, Sagas 22, Generators.Shared 27, Data.Dapper.Sqlite 28); settle the async-noise policy; SQL phase 2.
- **Wave 3**: mid-size, decision-heavy assemblies (Transports.AzureServiceBus 232, Transports.RabbitMQ 143, Data.Postgres 326, Data.Dapper.Postgres 195, the generator family about 1,390); Stryker configs added as each reaches about 95%; SQL phase 3.
- **Wave 4**: Whizbang.Core (3,343) by namespace slice; Data.EFCore.Postgres (920) with expression-tree carve-outs documented per file.
- **Wave 5**: mutation `high` to 90 or more; floors at 100 minus defensive branches everywhere; the optional const-fold experiment, Invariant analyzer, and Coyote evaluation.

## Sizing

- **Wave 1**: about 90 to 100 MC/DC vectors plus the Invariant, TestOps, and SQL-pilot code. The full Run-Tests.ps1 port is the largest single item. Roughly 8 to 12 focused sessions across the 8 PRs.
- **Full C# burn-down**: about 6,680 gated edges at 3 to 4 edges per test, minus a 5 to 10% defensive share, gives about 1,500 to 2,000 new test cases. Core is about half.
- **Full SQL program**: pilot about 9 to 12 days; about 950 vector cases in total, discounted by existing coverage, is about 30 to 40 days at one file per PR.

## Verification (wave 1)

1. **Gate fires**: `testops coverage gate --enforce` against a scratch floors file with one floor set above measured exits 1 with a worst-first table; real floors exit 0. A full `testops run --coverage` shows the branch summary, the per-class worklist, and enforcement; a filtered run is advisory only. On the PR 1 branch, commit a too-high floor, watch the CI step fail with `::error::` annotations, revert, and watch it pass.
2. **Invariant accounting**: the Invariant class is absent from `Summary.txt`; the `Require` conversions reduce the caller file's branch count in the per-class report; a `Never` without a matching floors bump fails the drift check.
3. **MC/DC pilot**: the per-class report shows `AsbReceiveDecisionMaker` at 100% branch, and Stryker on that file shows every condition mutant at :184 killed.
4. **Stryker lanes**: `testops mutate --project Whizbang.Core --mutate "**/SemanticVersion.cs" --break-at 100` exits nonzero while survivors exist. A draft PR with a deliberately weakened condition trips `mutation-pr.yml`.
5. **SQL pilot**: after the vector class runs, `plpgsql_coverage_branches('flush_completions'::regprocedure)` returns 1.0 and the statement dump shows the `PERFORM` arm executed; `testops sqlcov gate` fails when a pilot function drops below its manifest floor; the static-analysis test is green, or its findings are filed as bugs.
6. **Runner parity**: the shim and the old script, run side by side on identical inputs, select the same tests and return the same exit codes before the old script is deleted.

## Key existing files

- `scripts/Run-Tests.ps1` (`Invoke-CoverageReport` at :763; per-class line worklist around :828)
- `.github/workflows/reusable-quality.yml` (`FILE_FILTERS` ~:243; gate ~:271-288)
- `codecoverage.config` (attribute exclusions); `sonar.config:73`; `.gitignore:173` and `:432` (coverage name patterns)
- `.config/dotnet-tools.json`; `tests/Whizbang.Core.Tests/stryker-config.json`; `scripts/mutation/run-mutation-tests.ps1`; `.github/workflows/mutation.yml`, `reusable-mutation.yml`, `reusable-test-postgres.yml`, `ci.yml`
- `ai-docs/mutation-testing.md`; `ai-docs/coverage-exclusions.md`
- `src/Whizbang.Testing/Containers/SharedPostgresContainer.cs`; `tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresTestBase.cs`
- `src/Whizbang.Data.Postgres/Migrations/029_ProcessWorkBatch.sql` (:1301) and `044_PurgeOrphanInbox.sql`
