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
