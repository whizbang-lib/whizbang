# Workflows

How changes are tested, versioned and published is documented once, in
**[docs/RELEASING.md](../docs/RELEASING.md)**: the supported flows, how to read a run's stage and phase
names, the version rules, and recovery. This file is only a map of the workflow files.

## Entry points

| Workflow | File | Runs on | What it does |
|---|---|---|---|
| CI | `ci.yml` | PRs, the merge queue, pushes to `develop`, `release/v*` and `main`, dispatch | Plan, build, test, analyze and gate every change; publishes develop's alpha and older-line hotfixes |
| Start Release | `start-release.yml` | dispatch, from `develop` | Cuts `release/vX.Y.Z` and opens the release PR |
| Release Prerelease | `release-prerelease.yml` | dispatch, from a release branch | Publishes a beta or rc from the release branch's tested build |
| Release | `release.yml` | the release PR closing; dispatch for recovery | Promotes the tested packages as stable, tags `main`, syncs `develop` |
| NuGet Publish | `nuget-publish.yml` | dispatch; called by the above | Pushes a packages artifact and creates the tag and GitHub release |
| Git Flow Check | `git-flow-check.yml` | PRs | Refuses a branch direction the supported flows don't allow |
| Dependabot lock files | `dependabot-lockfiles.yml` | Dependabot PRs into `develop` that change packages | Regenerates the NuGet lock files Dependabot leaves stale and pushes them to its branch (below) |
| CodeQL | `codeql.yml` | pushes, PRs, schedule | Static security analysis |
| Secret Scanning | `security-secrets.yml` | pushes, PRs, dispatch | Scans for committed secrets |
| Supply Chain Security | `security-supply-chain.yml` | pushes, PRs, schedule, dispatch | Dependency vulnerability scanning |
| OSSF Scorecard | `security-scorecard.yml` | schedule, dispatch | Repository security posture |
| Mutation Testing | `mutation.yml` | dispatch | Stryker mutation runs (see `ai-docs/mutation-testing.md`) |
| Analyzer Sweep | `analyzer-sweep.yml` | dispatch | Runs every analyzer across the solution |
| Test Shard Report | `test-shard-report.yml` | schedule (monthly), dispatch | Measures every test suite and the EFCore shards, and posts the report on the tracking issue (`ai-docs/test-sharding.md`) |
| Cache Cleanup | `cache-cleanup-scheduled.yml` | schedule, dispatch | Prunes stale Actions caches |
| Mirror CI images | `mirror-ci-images.yml` | dispatch, schedule (weekly), `develop` pushes and PRs that change `.github/ci-images.txt` | Copies the Docker Hub images CI pulls to `ghcr.io/whizbang-lib/ci-mirror` (`ai-docs/ci-image-mirror.md`) |
| Notify PR Created / Merged | `notify-pr-created.yml`, `notify-pr-merged.yml` | PRs | Push notifications |

## Reusable pieces

| File | Used by | Purpose |
|---|---|---|
| `reusable-build.yml` | CI | Compile, hash the assemblies (determinism manifest), upload the build, instrument the test-side copies for coverage while it uploads |
| `reusable-test-*.yml` | CI | One suite each: unit, PostgreSQL, InMemory, RabbitMQ, Service Bus, Azure Blob |
| `reusable-quality.yml` | CI | Sonar analysis and the coverage gates, on this run's or a covering run's coverage |
| `reusable-pack.yml` | CI, Release Prerelease | Pack a tested build without rebuilding |
| `reusable-version.yml` | CI | Resolve the version (release PR title, release branch name, or GitVersion) |
| `reusable-sync-develop.yml` | Release, CI | The one merge-back into `develop` |
| `reusable-mutation.yml` | Mutation Testing | One mutation run |
| `nuget-push.yml` | every publish path | The only workflow nuget.org's trusted publishing accepts; attests, then pushes |
| `notify-build-status.yml` | CI | Build notifications |

| Action | Purpose |
|---|---|
| `.github/actions/find-tested-run` | Which run tested a commit (queue run, or the PR run on a fast-forward) |
| `.github/actions/require-pr-gate` | Refuse to publish a commit whose PR gate (coverage, Sonar) did not pass |
| `.github/actions/verify-packages` | Exactly the packages in `.github/nuget-packages.txt`, at exactly one version |
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
changed it commits only `packages.lock.json` files, signed off, as the GitHub App's bot account, and
pushes to the Dependabot branch. The logic is `.github/scripts/Update-DependabotLockFiles.ps1`, tested
by `Test · Pipeline scripts`.

- **Why a GitHub App.** A push made with `GITHUB_TOKEN` starts no workflow, so CI would never run on the
  fixed head. A push made with an app installation token does.
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
a table saying which tier each class got and why every higher tier was skipped.

**Tiers, per job class; the first whose every check passes wins.**

1. **Self-hosted pool.** The run is trusted: a push, a merge-queue run, a dispatch, a schedule, or a pull
   request from a branch of this repository that Dependabot did not open. Fork and Dependabot pull
   requests never reach the pool. The runner-pool app can read the runner group; the group lets this
   repository in (visibility "all", or "selected" with this repository selected), allows public
   repositories, and is not restricted to selected workflows; an online, idle runner carries every label
   of the class; and this repository's jobs already queued for those runners leave one spare.
2. **Blacksmith.** `CI_BLACKSMITH_RUNNER` (or the class's own variable) names a label that fits the class,
   and the run is trusted, or it is a fork or Dependabot pull request and `CI_BLACKSMITH_FORKS` is `true`.
   When `CI_BLACKSMITH_MAX_QUEUED` is above 0, fewer than that many jobs are already queued for the label.
3. **`ubuntu-latest`.** Anything else: a missing token, an unset or malformed variable, an API error, a
   timeout (10 seconds a request, 60 for the whole picker), or an unexpected response.

**Job classes.**

| Class | Pool labels | Jobs | Notes |
|---|---|---|---|
| `compile` | `self-hosted`, `whizbang`, `Linux`, `X64` | Build · Compile | GitHub-hosted on every push: a push run's build is what is packed, published and attested |
| `linux` | `self-hosted`, `whizbang`, `Linux`, `X64` | Build · Check formatting, Build · No-op rebuild does no work, Test · Unit, Test · Component, Test · Pipeline scripts | Linux-only steps (bash, `find`, `sha256sum`); the suites run the Linux x64 build, whose test hosts are x64 executables |
| `analyze` | none: never the pool | Analyze · Quality | Waits up to hours for the suites' coverage; on a pool machine it would hold the machine the suites need |
| `linux-docker` | `self-hosted`, `whizbang`, `Linux`, `X64`, `docker` | every integration suite (PostgreSQL, InMemory, RabbitMQ, Service Bus, Azure Blob, Integration) | Testcontainers and `docker run` |
| `any-os` | `self-hosted`, `whizbang` | none yet | For a job whose steps are portable (pwsh only, no Linux paths) and that does not run the Linux build |

Not routed, always `ubuntu-latest`: the planning and gate jobs (small, and the gate must not depend on a
machine), `Build · Generator and analyzer timing` (its thresholds were measured on GitHub-hosted runners),
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
| `CI_BLACKSMITH_RUNNER` | The Blacksmith label every class uses, for example `blacksmith-4vcpu-ubuntu-2404`. Unset: no Blacksmith tier |
| `CI_BLACKSMITH_RUNNER_COMPILE`, `_LINUX`, `_ANALYZE`, `_LINUX_DOCKER`, `_ANY_OS` | A label for one class, over `CI_BLACKSMITH_RUNNER` |
| `CI_BLACKSMITH_FORKS` | `true` lets fork and Dependabot pull requests use Blacksmith (it is billed). Default: they run GitHub-hosted |
| `CI_BLACKSMITH_MAX_QUEUED` | Above 0: skip Blacksmith when that many of this repository's jobs are already queued for the label. Default 0, not checked |

A Blacksmith label must be a plain label; for a Linux class it must be an `ubuntu` label, and for an
X64 class not an `-arm` one. A label that does not fit skips the tier for that class only.

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
- Install PowerShell 7, Git and, for `docker`, Docker with the runner account allowed to use it. The
  `linux` and `linux-docker` jobs also use bash and GNU coreutils.
- Register it in the runner group, then add `whizbang` (and `docker` where it applies). Watch one run's
  Pick runners summary to confirm the class it serves.

**Limits.** The picker decides once, at the start of the run, so a machine that is idle then may be busy
when a later job of the run starts, and that job waits for it rather than falling back. Every job of a
class in the run goes to the same label set, so one pool machine serves a run's jobs of that class one
at a time; the pool pays off when it has a machine per concurrent job. Only this repository's queue is
visible; other repositories' load shows up only as busy runners.
