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
| CodeQL | `codeql.yml` | pushes, PRs, schedule | Static security analysis |
| Secret Scanning | `security-secrets.yml` | pushes, PRs, dispatch | Scans for committed secrets |
| Supply Chain Security | `security-supply-chain.yml` | pushes, PRs, schedule, dispatch | Dependency vulnerability scanning |
| OSSF Scorecard | `security-scorecard.yml` | schedule, dispatch | Repository security posture |
| Mutation Testing | `mutation.yml` | dispatch | Stryker mutation runs (see `ai-docs/mutation-testing.md`) |
| Analyzer Sweep | `analyzer-sweep.yml` | dispatch | Runs every analyzer across the solution |
| Cache Cleanup | `cache-cleanup-scheduled.yml` | schedule, dispatch | Prunes stale Actions caches |
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
| `.github/actions/setup-dotnet-retry`, `notify-pushover` | Setup and notification helpers |

Shared data: `.github/nuget-packages.txt` (the packages every full publish ships),
`.github/inert-paths.txt` (paths that cannot affect the build or tests, read through
`.github/scripts/inert-diff.sh`).
