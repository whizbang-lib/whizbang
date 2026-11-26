# Workflows

How changes are tested, versioned and published is documented once, in
**[docs/RELEASING.md](../docs/RELEASING.md)**: the supported flows, how to read a run's stage and phase
names, the version rules, and recovery. This file is only a map of the workflow files.

## Entry points

| Workflow | File | Runs on | What it does |
|---|---|---|---|
| CI | `ci.yml` | PRs, the merge queue, pushes to `develop`, `release/v*` and `main`, dispatch | Plan, build, test, analyze and gate every change; publishes develop's alpha and older-line hotfixes |
| Start Release | `start-release.yml` | dispatch, from `develop` | Cuts `release/vX.Y.Z`, gathers `changelog.d/` into `CHANGELOG.md`, and opens the release PR |
| Release Prerelease | `release-prerelease.yml` | dispatch, from a release branch | Publishes a beta or rc from the release branch's tested build |
| Release | `release.yml` | the release PR closing; dispatch for recovery | Promotes the tested packages as stable, tags `main`, syncs `develop` |
| NuGet Publish | `nuget-publish.yml` | dispatch; called by the above | Pushes a packages artifact and creates the tag and GitHub release |
| Git Flow Check | `git-flow-check.yml` | PRs | Refuses a branch direction the supported flows don't allow |
| PR intake | `pr-intake.yml` | PRs (`pull_request_target`), issues, comments, reviews, the merge queue, a finished CI run, a `docs-pr-changed` dispatch, schedule (daily) | The process around the gates (`.github/scripts/Invoke-PrIntake.ps1`): issue labels and commands, `/recheck`, the pipeline-owner re-check after a review and in the queue, and re-running `Gate · Review checks` when the docs pull request that satisfies it is opened, edited or reopened (sent by the docs repository's `library-review-recheck.yml`), or when a run ends that the docs pull request outlived (#1806) |
| Dependabot lock files | `dependabot-lockfiles.yml` | Dependabot PRs into `develop` that change packages | Regenerates the NuGet lock files Dependabot leaves stale and commits them to its branch (below) |
| CodeQL | `codeql.yml` | pushes, PRs, schedule | Static security analysis |
| Secret Scanning | `security-secrets.yml` | pushes, PRs, dispatch | Scans the full git history for committed secrets with Gitleaks (a pinned, checksum-verified release binary); reviewed exceptions in `.github/gitleaks.toml` and `.github/gitleaksignore` |
| Supply Chain Security | `security-supply-chain.yml` | pushes, PRs, schedule, dispatch | Dependency vulnerability scanning |
| OSSF Scorecard | `security-scorecard.yml` | schedule, dispatch | Repository security posture |
| Mutation Testing | `mutation.yml` | dispatch | Stryker mutation runs (see `ai-docs/mutation-testing.md`) |
| Analyzer Sweep | `analyzer-sweep.yml` | dispatch | Runs every analyzer across the solution |
| Build Cost Timing | `build-cost-timing.yml` | PRs and `develop` pushes that change a generator project, the repository's analyzers or the benchmark; schedule (weekly); dispatch | Advisory: times every generator and analyzer over a large generated consumer against `build-cost-thresholds.json` (#1651). Not required and not read by `Gate · CI result`; moved out of CI so it no longer runs on every event (#2068) |
| Test Shard Report | `test-shard-report.yml` | schedule (monthly), dispatch | Measures every test suite and the EFCore shards, and posts the report on the tracking issue (`ai-docs/test-sharding.md`) |
| Cache Cleanup | `cache-cleanup-scheduled.yml` | schedule, dispatch | Prunes stale Actions caches |
| Mirror CI images | `mirror-ci-images.yml` | dispatch, schedule (weekly), `develop` pushes and PRs that change `.github/ci-images.txt` | Copies the Docker Hub images CI pulls to `ghcr.io/whizbang-lib/ci-mirror` (`ai-docs/ci-image-mirror.md`) |
| Notify PR Created / Merged | `notify-pr-created.yml`, `notify-pr-merged.yml` | PRs | Push notifications |

## Reusable pieces

| File | Used by | Purpose |
|---|---|---|
| `reusable-build.yml` | CI | Compile, hash the assemblies (determinism manifest), upload the build, instrument the test-side copies for coverage while it uploads |
| `reusable-test-*.yml` | CI | One suite each: unit, component, PostgreSQL, InMemory, RabbitMQ, Service Bus, Azure Blob, general integration. One leg per target framework the build names (`test-frameworks`): `Run suite (net10.0)` and `Run suite (net11.0)` on a release build, `Run suite` otherwise. Only the leg on the coverage framework (`coverage-framework`, net11.0) collects and uploads coverage (#2135) |
| `reusable-quality-build.yml` | CI | Quality's first half, where the scanner runs (a PR, or main): SonarScanner begin and the analysis build, beside the suites; hands its analysis state to the second half |
| `reusable-quality.yml` | CI | Quality's second half, once every suite has finished: the coverage merge and gates, SonarScanner end and the PR comment, on this run's or a covering run's coverage |
| `reusable-pack.yml` | CI, Release Prerelease | Pack a tested build without rebuilding |
| `reusable-version.yml` | CI | Resolve the version (release PR title, release branch name, or GitVersion) |
| `reusable-mutation.yml` | Mutation Testing | One mutation run |
| `nuget-push.yml` | every publish path | The only workflow nuget.org's trusted publishing accepts; attests, then pushes |
| `notify-build-status.yml` | CI | Build notifications |

| Action | Purpose |
|---|---|
| `.github/actions/find-tested-run` | Which run tested a commit (queue run, or the PR run on a fast-forward) |
| `.github/actions/require-pr-gate` | Refuse to publish a commit whose PR gate (coverage, Sonar) did not pass |
| `.github/actions/verify-packages` | Exactly the packages in `.github/nuget-packages.txt`, at exactly one version |
| `.github/actions/sync-develop` | The one merge-back into `develop` (Release, CI); the calling job declares `release-app` and mints the app token, since a reusable workflow's job cannot read that environment's secrets unless its caller passes them (#1796) |
| `.github/actions/use-ci-image-mirror` | Pull Docker Hub images from the GHCR mirror when it is complete; record the baseline for the pull check |
| `.github/actions/setup-dotnet-retry`, `notify-pushover` | Setup and notification helpers |

Shared data: `.github/nuget-packages.txt` (the packages every full publish ships), `.github/ci-images.txt`
(the Docker Hub images CI pulls, mirrored to GHCR),
`.github/inert-paths.txt` (paths that cannot affect the build or tests, read through
`.github/scripts/Test-InertDiff.ps1`). The rules of the one required check, `Gate · CI result`, are
`.github/scripts/Test-CiResult.ps1`, with tests in `.github/scripts/tests/` run by `Test · Pipeline scripts`.
The build, test and analysis reusables take a `runs-on` input (JSON, default `"ubuntu-latest"`) that CI fills
from `Plan · Pick runners` (below); every other caller leaves it unset.

## Dependabot lock files

Dependabot's NuGet updater bumps `Directory.Packages.props` and the `packages.lock.json` of each
project that references the package directly. A project that reaches the package through a project
reference keeps the old version, and CI's locked-mode restore fails with NU1004 in Compile, Format and
OSV Scanner. `dependabot-lockfiles.yml` fixes that on the pull request itself: it runs
`dotnet restore --force-evaluate` over `Whizbang.slnx` and every project with a lock file outside it
(today `benchmarks/Whizbang.Benchmarks.Postgres` and `tests/Whizbang.Soak.Tests`), and when a lock file
changed it commits only `packages.lock.json` files, signed off, as the GitHub App's bot account, on top
of the Dependabot branch. The logic is `.github/scripts/Update-DependabotLockFiles.ps1`, tested by
`Test · Pipeline scripts`.

- **Why a GitHub App.** A branch update made with `GITHUB_TOKEN` starts no workflow, so CI would never
  run on the fixed head. One made with an app installation token does.
- **Why the API, not `git push`.** `develop`'s ruleset requires verified commits, and a commit made with
  `git commit` on the runner is unsigned, so it blocked the pull request. The script instead creates the
  blobs, the tree and the commit through the GitHub API and fast-forwards the branch (`force: false`).
  GitHub signs a commit an app creates that way, as long as the request names no author or committer;
  the commit is then attributed to the app's bot account. If Dependabot moved the branch meanwhile, the
  update is refused and the run ends with a notice; the run for the new head regenerates again.
- **When it runs.** Only when both the pull request's author and the event's actor are
  `dependabot[bot]`, so a person's push, or the app's own push, never triggers it. A run that finds the
  lock files already current commits nothing. It is not part of CI and is not a required check; the
  gate, `Gate · CI result`, reads only `ci.yml`'s jobs.
- **Without the app.** If either secret is missing, the job ends green with a notice saying which one,
  and the pull request fails at restore as before.

**One-time setup (repository owner).**

1. Create a GitHub App under the organization (Settings > Developer settings > GitHub Apps > New
   GitHub App). Suggested name: `whizbang-lockfiles`. Homepage URL: the repository URL. Turn off
   Webhook (no webhook is needed).
2. Repository permissions: **Contents: Read and write**. Leave every other permission at "No access"
   (Metadata: Read-only is added automatically). Organization and account permissions: none. Where can
   this app be installed: Only on this account.
3. Create the app, note its **App ID**, and generate a **private key** (a `.pem` file downloads).
4. Install the app on the organization and choose **Only select repositories**: this repository only.
5. In this repository, Settings > Secrets and variables > **Dependabot** (not Actions), add:
   - `LOCKFILE_APP_ID`: the App ID (the Client ID works too);
   - `LOCKFILE_APP_PRIVATE_KEY`: the entire contents of the `.pem` file.

   They must be Dependabot secrets: a workflow Dependabot triggers sees no Actions secrets.
6. Delete the downloaded `.pem` once it is stored.

If a ruleset restricts who may push to `dependabot/**` branches, add the app to its bypass list.

**Caveat: Dependabot stops rebasing.** Once a commit that is not Dependabot's lands on its branch,
Dependabot no longer rebases or recreates that pull request on its own (for example when `develop`
moves or a newer version comes out). Comment `@dependabot rebase` (or `@dependabot recreate`) to have it
rebuild the branch; it drops the lock-file commit, and this workflow then regenerates it.

## Runner selection

`Plan · Pick runners` (`ci.yml`, job `pick-runner`) decides where CI's build, test and analysis jobs run.
It runs `.github/scripts/Select-CiRunner.ps1` once per run, on a GitHub-hosted runner, and outputs one
`runs-on` value per job class. The rules are tested by `Test · Pipeline scripts`.

**Fallback is the default.** With none of the setup below, every job runs on `ubuntu-latest`, exactly as
before the picker. The picker job and each of its steps continue on error, it is not one of the gate's
needs, and every output and every consumer defaults to `ubuntu-latest`, so a picker that fails, times
out or never runs leaves CI unchanged and never turns `Gate · CI result` red. Each run's job summary has
a table saying which tier each class got and, for a GitHub-hosted class, why the pool was skipped.

**Tiers, per job class; the first whose every check passes wins.**

1. **Self-hosted pool.** The run is trusted: a push, a merge-queue run, a dispatch, a schedule, or a pull
   request from a branch of this repository that Dependabot did not open. Fork and Dependabot pull
   requests never reach the pool. The runner-pool app can read the runner group; the group lets this
   repository in (visibility "all", or "selected" with this repository selected), allows public
   repositories, and is not restricted to selected workflows; an online, idle runner carries every label
   of the class and has a recent passing self-check showing what the class needs (below); and this
   repository's jobs already queued for those runners leave one spare.
2. **`ubuntu-latest`.** Anything else: an untrusted run, a missing token, an API error, a timeout (10
   seconds a request, 60 for the whole picker), or an unexpected response.

**Job classes.**

| Class | Pool labels | Jobs | Notes |
|---|---|---|---|
| `any-os` | `self-hosted`, `whizbang` | Build · Check formatting, Build · No-op rebuild does no work, Test · Unit, Test · Component, Test · Pipeline scripts | Any OS and architecture. The suites run the build with `dotnet test --test-modules` on the framework-dependent `.dll` (no RID, nothing native beyond what NuGet ships per RID), so the Linux build runs on Windows X64 and ARM64 and on macOS. Every step is pwsh or an action |
| `compile` | `self-hosted`, `whizbang`, `Linux`, `X64` | Build · Compile, Build · Analyzers | Build · Analyzers builds the same solution with the analyzers on (Compile turns them off, #2068) and follows Compile's placement. Compile's determinism manifest is what verify-rebuild compares with the tested run's (same SDK, same assembly set), and its build is what the suites and pack consume. GitHub-hosted on every push: a push run's build is what is packed, published and attested |
| `linux-docker` | `self-hosted`, `whizbang`, `Linux`, `X64`, `docker` | every integration suite (PostgreSQL, InMemory, RabbitMQ, Service Bus, Azure Blob, Integration) | Testcontainers and `docker run` need a Linux Docker host; their steps are still bash |
| `analyze` | none: never the pool | Analyze · Quality build, Analyze · Quality | The two halves of Quality (#2068) must check out at the same path, since the scan's state names files absolutely, which GitHub-hosted runners guarantee; and on a release PR the second half still waits up to an hour for its release-branch run's coverage, holding whatever machine it is on |
| `linux` | `self-hosted`, `whizbang`, `Linux`, `X64` | none today | For a Linux-only job with no containers |

**Self-checks: capability, not just liveness (#1880).** A machine can be online and idle and still unable
to do the job (no PowerShell, a full disk, an upload too slow for the compile's artifacts, a broken
Docker). `Runner self-check` (`runner-self-check.yml`, every 6 hours and by hand) runs
`.github/scripts/Test-CiRunnerCapability.ps1` on each online pool machine and records the result as an
Actions cache entry, `ci-runner-check/v1/<runner>/<ok|fail>/up-<MB/s>/docker-<yes|no>/<nonce>`; the key is
the record and its creation time says when. The picker sends a job only to a machine whose latest record
passed within `RUNNER_POOL_CHECK_MAX_AGE_HOURS`; `compile` also needs an upload speed of at least
`RUNNER_POOL_MIN_UPLOAD_MBPS` (it uploads the build, about 1 GB), and `linux-docker` a working Docker. A
machine passes with PowerShell 7 or later and `RUNNER_POOL_MIN_DISK_GB` free. A machine with no record
(new, or where the check could not even start, such as one without pwsh) gets no job, so missing is as
safe as failing; the summary of Pick runners names each skipped machine and why. The weekly cache cleanup
leaves these records alone. The upload speed includes the upload action's start-up, so it is a lower bound.

**Fallback when a pool machine never starts a job or is lost during one (#1880).** GitHub cannot move a
queued job to another runner, nor re-run one job while its run is going, so the fallback works at the
run's edges. Rules: `.github/scripts/Invoke-CiRunnerFallback.ps1`.

- `Plan · Watch self-hosted jobs` (`ci.yml`, job `runner-watch`) runs on `ubuntu-latest` only when the
  plan sent a class to the pool. A pool job is stuck once it has been queued 10 minutes with no online
  machine carrying its labels, or 45 minutes whatever the machines say. When a job is stuck and no other
  job of the run is still running, the watcher starts `CI runner fallback` and cancels the run.
- `CI runner fallback` (`ci-runner-fallback.yml`, from the default branch) re-runs the run's failed jobs
  when the watcher canceled it, or when CI failed and every failed job was a pool job whose machine was
  lost (its annotations say it lost communication, received a shutdown signal, exceeded its time, or was
  never acquired), or the gate failing after it. If any job failed for another reason, nothing is re-run:
  a re-run would not change the result, and no flaky test gets a second try. At most 3 attempts.
- The re-run jobs take `ubuntu-latest` (next paragraph), and must pass there: no gate is relaxed.

The trade-off: the watcher never touches a running job, so a slow but alive machine is left to the job's
own timeout rather than run twice, and it cancels only once nothing else is running, so no finished work
is thrown away; the cost is that a stuck job is noticed up to the run's longest job later. A job queued
behind a busy but alive machine waits its turn up to 45 minutes before it counts as stuck. A job that times
out on a pool machine is treated as lost and re-run on `ubuntu-latest` once. **The merge queue is not
covered:** the queue removes the pull request when its run fails or is canceled, and a re-run of that run
cannot put it back, so the pull request must be queued again.

**Re-runs re-plan (#1880).** The picker's `attempt` output says which attempt its plan is for, and every
routed job (and every reusable workflow, through its `runner-plan-attempt` input) takes the plan only in
that attempt. "Re-run failed jobs" does not re-run the picker, so the jobs it re-runs go to
`ubuntu-latest` instead of labels chosen for an earlier attempt, which may name a machine that has since
left the pool. "Re-run all jobs" re-runs the picker, so its plan is fresh. A test fails if a routed job
reads the plan without the attempt check.

**Coverage from another checkout.** The build's coverage source-root mappings name the build machine's
checkout. A suite that checks out anywhere else (a team machine, Windows, macOS) rewrites them first
with `.github/scripts/Update-CoverageSourceRoots.ps1`, or the collector writes empty reports while every
test passes; `.github/scripts/Test-CoverageCollected.ps1` fails such a suite. Reports from Windows carry
Windows paths; the quality gate's merge normalizes them to repository-relative paths.

Not routed, always `ubuntu-latest`: the planning and gate jobs (small, and the gate must not depend on a
machine), `Build · Generator and analyzer timing` (`build-cost-timing.yml`; its thresholds were measured on GitHub-hosted runners),
`Build · Verify rebuild matches the tested build`, `Build · Pack` and every publish, release, signing and
attestation job (what reaches nuget.org is built and attested on GitHub-hosted runners), and the report
and notification jobs.

**Labels, never machine names.** A machine joins the pool when it gets the `whizbang` label; remove the
label to take it out. `docker` means Linux with Docker, able to run the integration suites' containers.
OS and architecture are the runner's automatic labels (`Linux`, `Windows`, `macOS`, `X64`, `ARM64`). The
runner group is `Default` unless the `RUNNER_POOL_GROUP` variable names another.

**Variables (Settings > Secrets and variables > Actions > Variables).** All optional.

| Variable | Effect |
|---|---|
| `RUNNER_POOL_DISABLED` | `true` turns the self-hosted tier off without touching the secrets |
| `RUNNER_POOL_GROUP` | The runner group holding the pool. Default `Default` |
| `RUNNER_POOL_CHECK_MAX_AGE_HOURS` | How old a machine's latest passing self-check may be. Default `24` |
| `RUNNER_POOL_MIN_UPLOAD_MBPS` | The upload speed, in MB/s, a self-check must have measured for the `compile` class. Default `10` |
| `RUNNER_POOL_MIN_DISK_GB` | The free disk a machine needs to pass its self-check. Default `20` |

**One-time setup for the self-hosted tier (owner).** All three are needed; until then the tier is skipped.

1. Settings > Actions > General > "Approval for running fork pull request workflows from contributors":
   **Require approval for all external contributors**. A fork pull request runs its own `ci.yml`, which
   can name the pool's labels in `runs-on` and skip the picker; approval puts a person in front of that.
2. The runner group lets this public repository in: Organization settings > Actions > Runner groups >
   the group > "Allow public repositories", and repository access "All" or this repository selected.
   Leave "Restricted to selected workflows" off.
3. A GitHub App under the organization with **Organization permissions > Self-hosted runners: Read**
   (nothing else; no webhook), installed on the organization, and two Actions secrets in this repository:
   `RUNNER_POOL_APP_ID` (the App ID or Client ID) and `RUNNER_POOL_APP_PRIVATE_KEY` (the `.pem`).

**Adding a machine.**

- Run the runner as a dedicated, non-admin account that holds no credentials of its own: a trusted
  pull request still runs code a contributor wrote.
- Prefer ephemeral runners (`--ephemeral`, re-registered by a service after each job). A persistent runner
  must be cleaned between jobs: `actions/checkout` cleans the workspace, but containers, volumes, tool
  caches and anything written outside the workspace stay.
- Several runner instances on one machine each get their own work directory, and the container ports
  the suites publish must not collide between them.
- Install PowerShell 7 and Git and, for `docker`, Docker with the runner account allowed to use it. The
  `linux-docker` and `compile` jobs also use bash and GNU coreutils. Pester 5 is installed by the job
  when the machine lacks it. On Windows, enable long paths (`LongPathsEnabled` in the registry, and
  `git config --system core.longpaths true`): the test output paths are deep.
- A machine that is not wiped between jobs is fine for the `any-os` jobs: the checkout's clean removes
  the previous job's build and test results, and `dotnet-coverage` is installed with `update`.
- Register it in the runner group, then add `whizbang` (and `docker` where it applies). Run
  `Runner self-check` by hand (Actions > Runner self-check > Run workflow): until it passes there, the
  machine gets no job. Watch one run's Pick runners summary to confirm the class it serves.
- A machine that leaves the pool (sleeps, is rebuilt, loses its network) needs no label change: it drops
  out when it goes offline or its self-check goes stale, and the fallback above covers the jobs it held.

**Limits.** The picker decides once, at the start of the run, so a machine that is idle then may be busy
when a later job of the run starts, and that job waits for it (up to the watcher's 45 minutes) rather than
falling back. Every job of a
class in the run goes to the same label set, so one pool machine serves a run's jobs of that class one
at a time; the pool pays off when it has a machine per concurrent job. Only this repository's queue is
visible; other repositories' load shows up only as busy runners.
