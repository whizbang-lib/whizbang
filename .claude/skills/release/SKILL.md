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
| "Cut a release" / "release what's on develop" | F2 | `gh workflow run start-release.yml --ref develop -f release_type=auto`. A clean cut reuses the tested develop commit's results (no suites; ready in about 15 minutes). If its verify-rebuild goes red, the recovery is `RELEASE_CUT_FULL_MATRIX=true` and a re-run. |
| "Give consumers something to test" / beta / rc / release candidate | F3 | Needs an open release branch (F2 first if none). `gh workflow run release-prerelease.yml --ref release/vX.Y.Z -f label=beta` (or `rc`) |
| A bug found during the release | F4 | Fix on `fix/<name>` cut from `release/vX.Y.Z`, PR **into `release/vX.Y.Z`**. After it merges and the branch goes green, publish the next beta/rc (F3). |
| "Ship it" / stable | F5 | Merge the release PR **with a merge commit**: `gh pr merge <n> --merge` (or `--auto --merge`). Then the `nuget-publish` approval. |
| A release must not ship | F6 | Close the release PR, delete `release/vX.Y.Z`. Re-cut later with `auto`: it reuses the number if betas/rcs shipped. |
| Production bug in the latest stable | F7 | `git switch -c release/vX.Y.(Z+1) vX.Y.Z && git push -u origin HEAD`, then commit the fix and push. CI opens the release PR itself. Continue with F3/F5. |
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
- Next cut: run Start Release's own logic by reading `git tag -l 'v*' --sort=-v:refname | head`, or
  `dotnet-gitversion` 6.2 with `GitVersion.yml` on `develop`. `auto` gives the next minor, unless an
  open beta/rc series exists (then it continues that number).
- Next prerelease: `git ls-remote --tags origin 'refs/tags/vX.Y.Z-*'`; the next `beta.N`/`rc.N`
  follows the highest. A beta after an rc is refused.

## Watch it through

- Address runs **by workflow path**, never by `.name`: every workflow sets a run name.
  `gh api "repos/whizbang-lib/whizbang/actions/workflows/ci.yml/runs?head_sha=<sha>&event=push"`.
- Run names say the stage (`CI · 4 release candidate · release/vX.Y.Z`, `Release · 6 publish stable`),
  and job names say the phase (`Plan ·`, `Build ·`, `Test ·`, `Analyze ·`, `Gate ·`, `Publish ·`,
  `Merge back ·`). RELEASING.md → *Reading a run*.
- After merging a release PR, confirm three things, and don't assume: the `release.yml` run reached
  the approval gate, the tag exists (`git ls-remote --tags origin vX.Y.Z`), and after approval the
  packages are live (`curl -s https://api.nuget.org/v3-flatcontainer/softwareextravaganza.whizbang.core/index.json | jq -r '.versions[-3:][]'`).
- `gh pr merge --auto` prints nothing when the PR is already merged. Read `state` back.

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
the list in `.github/workflows/git-flow-check.yml`. Run `actionlint` on every changed workflow. If a
job is renamed, its status-check name changes, and the `main`/`develop` rulesets must be updated at
the moment the PR merges (`gh api repos/whizbang-lib/whizbang/rules/branches/main` shows the
required names), or every release PR waits forever on a check that no longer exists.
