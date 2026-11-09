---
name: release
description: >-
  The right way to publish Whizbang in any situation: shipping a change (alpha), cutting a release,
  publishing a beta or rc, shipping stable, hotfixing the current or an older line, abandoning or
  recovering a release, and answering "when will my change be on NuGet?". Use whenever the task
  involves releasing, publishing, versions, tags, NuGet packages, release branches, the release PR,
  the nuget-publish approval, a stuck or failed release run, or changing any release workflow.
---

# Releasing Whizbang

`docs/RELEASING.md` is the source of truth. Its **Supported flows** table (F1 to F9) and **Recovery**
table are what this skill applies. Read the section for the flow you are about to run before running
it: the rules there are enforced by the workflows, and a step that fights them fails.

## Pick the flow

| Situation | Flow | Do |
|---|---|---|
| A change is merged, or about to be, and someone wants it "out" | F1 | Nothing to cut. Develop publishes `0.Y.0-alpha.N` on every merge, but **only the changed packages**. If a consumer needs a complete, installable set, that is a beta (F2 + F3). |
| "Cut a release" / "release what's on develop" | F2 | `gh workflow run start-release.yml --ref develop -f release_type=auto`. A clean cut reuses the tested develop commit's results (no suites; ready in about 15 minutes). If its verify-rebuild goes red, the recovery is `RELEASE_CUT_FULL_MATRIX=true` and a re-run. The cut also gathers `changelog.d/` into `CHANGELOG.md` (below); preview that first with `pwsh .github/scripts/Merge-ChangelogFragments.ps1 -Version X.Y.Z -DryRun`. |
| "Give consumers something to test" / beta / rc / release candidate | F3 | Needs an open release branch (F2 first if none). `gh workflow run release-prerelease.yml --ref release/vX.Y.Z -f label=beta` (or `rc`) |
| A bug found during the release | F4 | Fix on `fix/<name>` cut from `release/vX.Y.Z`, PR **into `release/vX.Y.Z`**. After it merges and the branch goes green, publish the next beta/rc (F3). |
| "Ship it" / stable | F5 | Merge the release PR **with a merge commit**: `gh pr merge <n> --merge` (or `--auto --merge`). Then the `nuget-publish` approval. |
| A release must not ship | F6 | Close the release PR, delete `release/vX.Y.Z`. Re-cut later with `auto`: it reuses the number if betas/rcs shipped. |
| Production bug in the latest stable | F7 | `git switch -c release/vX.Y.(Z+1) vX.Y.Z && git push -u origin HEAD`, then put the fix on `fix/<name>` and **PR it into that branch** (a direct push is refused). CI opens the release PR itself. Continue with F3/F5. |
| Bug in an older line someone still runs | F8 | Branch `release/vA.B.(C+1)` from the older tag and push it; put the fix on `fix/<name>` and **PR it into that branch** (a direct push is refused). Merging publishes and opens a develop-only back-merge PR; it never touches `main`. |
| `Gate · Alpha is not below NuGet` red on develop, or a published prerelease sorts above develop's current builds | F9 | Usually an abandoned cut (#872). Tag the commit that first published the stale version with that version, then unlist it on nuget.org (the user runs the unlist: it needs an API key). Verify with GitVersion 6.2 `/nocache` on develop before pushing the tag. Never add `next-version` to `GitVersion.yml`. |
| Something is stuck or red | F9 | The **Recovery** table in RELEASING.md, by symptom. |

Only one release is in flight at a time. Before F2, check:
`gh pr list --base main --state open --json number,headRefName`.

## Before you act, say what will happen

Tell the user, in one or two lines, **the exact version** that will publish, from **which branch**,
and that **the `nuget-publish` approval is theirs to click** (Actions → the run → Review deployments).
Never approve a deployment yourself, even though the API would let you: publishing to nuget.org is
permanent and the approval is the human decision point. The one exception is a standing repository
setting (`PUBLISH_WITHOUT_APPROVAL=true`) that the user set themselves.

Work out the version from facts, not guesses:
- **The rollup (#2136).** A stable release's version is the base plus, for each prerelease label
  published since the last stable (alpha, beta, rc, any label), that label's latest number, added to the
  minor (patch reset, no suffix): `0.2627.0-alpha.674` -> `0.3301.0`; with `beta.5` -> `0.3306.0`; with
  `rc.3` too -> `0.3309.0`. The target component is `rollupTarget` in `.github/release-version.json`
  (`minor`, or `patch` after 1.0). The math is `.github/scripts/Get-ReleaseVersion.ps1`; never do it by hand.
- Next cut: `dotnet-gitversion` 6.2 with `GitVersion.yml` on `develop` gives `MajorMinorPatch` and
  `PreReleaseNumber`; `auto` names the branch `release/v<minor + alpha number>` and records both in the
  release PR body (`<!-- release-rollup: ... -->`). If an open beta/rc series exists it continues that
  number instead and adds no alpha. `major`, `minor`, `patch` and `manual` are overrides: no record, and
  the stable ships the branch number however many betas or rcs follow.
- Next prerelease: `git ls-remote --tags origin 'refs/tags/vX.Y.Z-*'`; the next `beta.N`/`rc.N`
  follows the highest. A beta after an rc is refused. Every beta or rc **raises the stable by one**:
  Release Prerelease's summary says what the merge will ship.
- Stable: the release PR title and branch name the **line**; what ships is the line plus the latest
  beta and rc numbers (`Plan · Is this a release?` prints `alpha 674 + beta 5 + rc 3 = 682 -> 0.3309.0`).
  When it differs from the line, `Build · Repack the tested build at the rolled-up version` repacks the
  release-branch run's tested build (the same DLLs, as a beta is made) instead of promoting its packages.
  Tell the user the rolled-up number, not the title's.

## The changelog

Entries are fragments (#2168; RELEASING.md, *The changelog*; format in `changelog.d/README.md`):

- A PR into `develop` adds `changelog.d/<issue>.md` (front matter `category:` and optional `subsection:`,
  then the entry). It never edits `[Unreleased]` in `CHANGELOG.md`: `Gate · Pull request type` refuses
  that, and takes a breaking change's **Upgrade:** note from the fragment.
- Start Release gathers every fragment, plus anything still under `[Unreleased]`, into `## [X.Y.Z] - <date>`
  in the version bump commit and deletes the gathered fragments. A malformed fragment fails the cut, naming
  it, before anything is pushed. Fragments merged after the cut wait for the next release.
- PRs into a release branch (F4, F7, F8) write in `CHANGELOG.md` under that version's heading, never a
  fragment and never under `[Unreleased]`. If betas or rcs roll the stable number up, rename the heading by
  such a PR after the last prerelease.
- The sync back to develop after a stable now always opens a PR: it carries the version section and the
  fragment deletions.

## Watch it through

- Address runs **by workflow path**, never by `.name`: every workflow sets a run name.
  `gh api "repos/whizbang-lib/whizbang/actions/workflows/ci.yml/runs?head_sha=<sha>&event=push"`.
- Run names say the stage (`CI · 4 release candidate · release/vX.Y.Z`, `Release · 6 publish stable`),
  and job names say the phase (`Plan ·`, `Build ·`, `Test ·`, `Analyze ·`, `Gate ·`, `Publish ·`,
  `Merge back ·`). RELEASING.md → *Reading a run*.
- `release.yml` starts from the push to `main` (not the `pull_request` event), because the publish
  environments admit only `develop`, `main` and `release/**`. RELEASING.md → *Where a publish may run from*.
- Pushes, PRs and `v*` tags are made by the `whizbang-release-bot` app, never `GITHUB_TOKEN` or a
  personal token. A red `Mint the release app token` has its own Recovery row. RELEASING.md →
  *Which identity writes*.
- After merging a release PR, confirm three things, and don't assume: the `release.yml` run reached
  the approval gate, the tag exists (`git ls-remote --tags origin vX.Y.Z`) and is signed (`gitsign verify-tag`, the command in SECURITY.md, "Verifying a Release"), that the release attaches a `*.cdx.json` SBOM per package, and after approval the
  packages are live (`curl -s https://api.nuget.org/v3-flatcontainer/softwareextravaganza.whizbang.core/index.json | jq -r '.versions[-3:][]'`).
- `gh pr merge --auto` prints nothing when the PR is already merged. Read `state` back.
- After a **stable** ships, open one PR into `develop` that moves `PackageValidationBaselineVersion` in
  `Directory.Build.targets` to it and deletes every `CompatibilitySuppressions.xml` (RELEASING.md,
  *Public API baseline*).
- A release-branch run must have built and packed: the publish promotes its `nuget-packages-<run>`
  artifact and the release PR takes its coverage. Every `release/v*` push counts as code and the gate
  fails one with no build or pack (#1208), so a second PR carrying the same fix can no longer leave the
  release with nothing to publish. If an older run still lacks packages, re-running all its jobs is
  safe even after the release PR merged (#1209).
- A red `Plan · Locate the tested packages` says which failure it is: no run found, run not green, or
  packages missing. Each has its own row in the Recovery table. "No run found" after a green
  release-branch run is the search missing it: re-run the release's failed jobs, not the branch CI.

## Never

- **Never** `gh workflow run release.yml` without `-f dry_run=false` when you mean to publish: it
  defaults to a dry run that reports success and publishes nothing. And never dispatch it at all
  except as the recovery named in RELEASING.md.
- **Never** use `release_type=manual` except for recovery, and never with a label.
- **Never** squash-merge a release PR, push to `develop` or `main`, or open a PR into `main` from
  anything but `release/vX.Y.Z`. The gitflow check and `release.yml` refuse these anyway.
- **Never** put anything after the version in a release PR title (`chore(release): vX.Y.Z` exactly).
- **Never** delete a published tag: GitHub keeps a tombstone for immutable releases and the number is
  gone. A wrong tag is superseded by a higher one.
- **Never** try to publish a commit that skipped the PR gate (100% coverage of new lines, zero Sonar
  findings). The gate runs only on pull requests; betas, rcs and hotfixes refuse anything else, and
  so should you: route the change through a PR.
- **Never** park a flaky failure in a release run. Re-run the failed jobs to unblock the release,
  **and** fix the flake in its own PR.
- **Never** name a client, consumer, tenant or environment anywhere: titles, bodies, tags, notes.

## Changing the pipeline

A change to any release workflow, the version logic or the branch rules updates, **in the same PR**:
`docs/RELEASING.md` (flows, recovery, reading a run), this skill, and, if branch directions change,
the list in `.github/workflows/git-flow-check.yml`. Run `actionlint` on every changed workflow.
Both rulesets require `Gate · CI result` (and `main` also `SonarCloud Code Analysis`), never a suite
by name, so suites can be renamed or sharded freely, but a new suite must join the gate's `needs` and
`$Suites` in `.github/scripts/Test-CiResult.ps1`. Any new way to skip suites must become a path in that
script with its own evidence and tests (`Test · Pipeline scripts`); the gate fails a skip it cannot
prove. Renaming the gate itself wedges every merge.
A new workflow step that commits to a branch that will merge into develop, main or a release branch
publishes with `.github/scripts/Publish-SignedCommit.ps1`, never `git push`: those rulesets require
signed commits, and only a commit created through the API is signed (docs/RELEASING.md, "Which
identity writes"). A new workflow step that pushes, opens a PR or tags mints the app token the same way the existing ones
do (`actions/create-github-app-token`, only the permissions it needs) and gets a row in *Which identity
writes*. Its job declares `environment: release-app` (`release-cut` for cutting a release): the app's
secrets are environment secrets, so a job without the environment reads nothing (#1466,
`.github/scripts/tests/ReleaseSecrets.Tests.ps1`). Mint the token in a job of the workflow the event
started, and put shared steps in a composite action (as `.github/actions/sync-develop` does), not a
reusable workflow: a reusable workflow's job gets an environment secret only when its caller passes it
(`secrets: inherit`), and without that it reads an empty string with no error until the token step
fails (#1796). A cut now waits for a `build-admins` approval in
Actions before it runs.
