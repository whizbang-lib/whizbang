# CI pipeline: optimization and correctness

> **Status:** assessment complete, nothing from the change set built yet.
> **Assessed:** 2026-09-25, against `develop` at `fdfe99719`.
> **Companion:** a diagrammed version of this analysis is published as an artifact
> ("Release Pipeline Atlas"). This file is the durable copy; the artifact is the review surface.

## Why this exists

The pipeline schedules its test matrices well — three gates already remove the redundant legs. The
leak is **between build and release**: the packages that pass the matrix are discarded, and the
canonical release path builds fresh ones on `main` to publish. Alongside that, three gaps turned up
that are correctness problems rather than cost problems: a hotfix never merges back, prerelease
labels cannot be chosen without violating the versioning policy, and the stable publish gate is
held up by an undocumented rule.

Scope is `.github/workflows/**` plus `docs/RELEASING.md`. Nothing here changes what version ships.

---

## State when this was written

**Merged (this session):**

| PR | What |
|---|---|
| #850 | `release-pr` gate — a release cut tests its tree once, not twice (six suites only) |
| #852 | Extended that gate to `format`, `build` and `quality`, which had their own change filters |
| #854 | `run-name` for a `release/v*` push is now `release cut`, not `post-merge` |

**Open:** #853 `chore(release): v0.2451.0`, auto-merge armed (merge commit).

**Dropped deliberately:** #849 and #851 were release cuts abandoned before the gates landed. #851's
branch tip `5ebd4075a` is recorded; its branch was deleted and both CI runs cancelled and verified
stopped. Neither tagged nor published anything.

---

## Findings

Each carries the evidence that established it. Re-verify before acting — line numbers drift.

### F1 — a `main` push runs the whole matrix to produce one SonarCloud analysis

`on.push.branches` includes `main` solely so `reusable-quality` records a **main-line** analysis
(omitting `sonar.branch.name`). The eleven test jobs, the build and the pack are collateral.

*Evidence:* run `35808661046`, 02:01 → 02:47 UTC — **46 minutes**, eleven test jobs plus build, pack
and quality, on a tree the release branch had fully verified minutes earlier. Once per release.

### F2 — the canonical release path rebuilds instead of promoting (the critical one)

Two publish paths disagree about where the bytes come from.

- `nuget-publish.yml:73` — `if: ${{ inputs.artifact-name == '' }}` skips its pack job when an
  artifact is supplied. Its header comment says the pushed packages are then "the exact bits" that
  passed the matrix. **`ci.yml`'s `release-publish` uses this** (the standalone hotfix path).
- `release.yml:462` and `:469` — `dotnet build` then `dotnet pack` on `main`, uploaded at `:532`.
  **This is the canonical release path**, and it publishes bytes no suite ever ran against.

The release-branch run's packages are already stamped `X.Y.Z` from the branch name, so they are
directly promotable. They are discarded.

Net today: **3 builds, 3 packs, 2 full matrices per release**, and the shipped bytes are the
untested ones.

### F3 — `release-publish` and `prerelease-publish` gate their tests differently

GitHub applies `success()` to a job implicitly **unless** the `if` already contains a status-check
function.

- `prerelease-publish` contains `!failure()`, so it lost the implicit gate and re-asserts `pack`,
  `format` and all six suites by name.
- `release-publish` (`ci.yml:1117`) carries a plain `if` and still relies on the implicit gate.

It is correct today. But every other job in the file opens with `!failure() && !cancelled()`, so
adding that here is the obvious tidy-up — and it would silently delete the entire test gate on the
path that publishes **stable** releases. The rule appears nowhere in `RELEASING.md`.

### F4 — `release-pr` yields to a push run it never confirms is healthy

`queue-validated` requires `&status=success` on its lookup. `release-pr` only checks that a push run
*exists* — deliberately, since it is still running. But if that run is cancelled, the PR run has
already skipped, the 13 required contexts are never reported, and the PR blocks permanently with no
failing check to point at. Recovery is `RELEASE_PR_FULL_MATRIX=true` plus a re-run, which is
invisible from the PR. This shape was hit during this session when #851's runs were cancelled.

### F5 — `quality`'s `coverage-artifact-count` is hardcoded to `11`

Reshard the suites and Quality waits forever on artifacts that never arrive.

### F6 — the release globs disagree

`push` uses `release/v**`; `pull_request` uses `release/**`. A PR into `release/foo` gets CI; a push
to it does not.

### F7 — prerelease labels cannot be chosen, and both channels emit the same one

`GitVersion.yml` sets `label: alpha` on `develop`; `release` and `main` carry `label: ''`. Release
labels therefore come from the **branch name**, parsed at `reusable-version.yml:104`:

```
^release/v([0-9]+)\.([0-9]+)\.([0-9]+)(-([0-9A-Za-z.]+))?$
```

That optional trailing group is the entire prerelease mechanism. Reaching it requires
`release_type=manual` with a hand-written version — the one path the versioning policy reserves for
recovery. `RELEASING.md` concedes this in its own rule-of-thumb ("if you use `manual` … for a
prerelease label, since `minor` produces a bare `0.X.0`").

Worse: **all 8 prerelease tags in the repo are `alpha`, emitted by both channels.** develop publishes
`-alpha.N` (commit height); a deliberate cut publishes `-alpha.1`. That shared label is the mechanism
behind the July 2026 incident, where `0.959.1-alpha.1` sorted *under* the already-published
`0.959.1-alpha.12`. The fix applied then was disjoint **bands**; disjoint **labels** would have
prevented it structurally.

### F8 — a hotfix never merges back to `main` or `develop`

`release.yml` triggers **only** on `pull_request: types: [closed], branches: [main]` or
`workflow_dispatch` (`release.yml:3-10`). `sync-develop` lives inside it at `:602`, gated
`needs: [check-release, publish]`.

The standalone hotfix path publishes through `ci.yml`'s `release-publish` → `nuget-publish.yml`,
which **never enters `release.yml`**. So a hotfix on a no-PR `release/vX.Y.Z` branch:

- publishes to nuget.org — with correctly promoted, tested bytes
- creates its tag and GitHub release
- **never reaches `main`**
- **never reaches `develop`**

The fix exists only on its own branch. The next release cut from develop silently ships without it.

Two distinct cases hide under one path, and neither is handled:

1. **Hotfix on the current line** (main's tip) — should reach both `main` and `develop`.
2. **Patch of an older line** — must **not** reach `main` (it would regress main to an older
   release) but must still reach `develop`, or the fix is lost.

Hotfix branches are also created **by hand** (`RELEASING.md:172`), so none of `start-release`'s
guardrails — band checks, the expected `Directory.Build.props` conflict resolution, PR creation —
apply.

**Note on "bugfix" vs "hotfix".** They are genuinely different and only one needs work. A *bugfix*
branches from develop, merges via an ordinary PR and ships in the next release — that is the normal
feature flow and needs nothing new. A *hotfix* is the out-of-band path above, and it is the one
missing its merge-back.

### F9 — documentation drift

`RELEASING.md` §261 is titled "queue-validated + ff-validated" and says "two guards". There are now
**three**. Neither `release-pr` nor its escape hatch `RELEASE_PR_FULL_MATRIX` appears anywhere in the
document. Also recorded only as comments inside `ci.yml`: the `run-name` convention, the implicit
`success()` rule (F3), and why `main` is a push trigger (F1).

---

## Proposed change set

One PR — each PR costs a full matrix. Ordered by value.

| | Change | Effect |
|---|---|---|
| **P1** | `release.yml`'s `build-and-pack` downloads the release-branch run's `nuget-packages-*` artifact and passes it through, as the hotfix path already does. Find the run via the merge commit's release-branch parent — the same lookup `reupload-reports` performs. | removes a build and a pack; **shipped bytes become the tested bytes** |
| **P2** | Skip the six suites and the pack on a `main` push; republish the release run's coverage and TRX so Quality still analyzes real coverage. Same pattern as `queue-validated` + `reupload-reports`. | **~46 min per release** |
| **P3** | Make `release-publish`'s gate explicit, mirroring `prerelease-publish`. | no saving; removes the F3 trap |
| **P4** | Name the run `release-pr` yielded to, and how to take the matrix back, somewhere visible from the PR. | recoverability |
| **P5** | Derive `coverage-artifact-count` instead of hardcoding `11`. | removes a silent hang |
| **P6** | Align `release/v**` and `release/**`. | consistency |
| **P7** | Close the F9 documentation drift in `RELEASING.md`. | — |
| **P8** | `prerelease_label` input on `start-release` (choice: `beta`, `rc`, none), with series-number pinning and automatic iteration; reserve `alpha` for the develop channel. | unlocks beta/rc without hand-picking a number |
| **P9** | Give the hotfix path a merge-back. | closes F8 |

### P8 in detail — two rules make it safe

**Rule 1 — a series pins its number once opened.** The first cut takes its number from GitVersion.
Every later cut in that series (`beta.2`, `rc.1`, the stable promotion) reuses it, found by looking
for the highest `X.Y.Z` that has prerelease tags but no stable tag. Without this, promotion drifts
upward as tags land and you can never ship the number you betaed.

**Rule 2 — the continuous channel's label is reserved.** `develop` keeps `-alpha.N` permanently;
deliberate cuts use `beta`, `rc` or no label, never `alpha`. Precedence then falls out on its own:

```
0.Y.0-alpha.N  <  0.Y.0-beta.1  <  0.Y.0-rc.1  <  0.Y.0
```

Constrain the input to an ordered choice list, not free text — prerelease identifiers compare
ASCII-wise, so an ad-hoc `-preview` or `-hotfix` sorts somewhere nobody predicted.

Iteration comes free from the same lookup: count `vX.Y.Z-<label>.*`, take the max, add one.

### P9 in detail — three options

- **A. Keep the no-PR push publish, add merge-back.** After `release-publish` succeeds, open sync PRs
  into `main` and `develop`. Fastest, but writes a second merge-back mechanism beside `sync-develop`.
- **B. Route hotfixes through a PR to `main` like every other release.** `release.yml` then runs and
  `sync-develop` already handles develop. One release path, no new mechanism. **Adds only a PR
  round-trip** — both paths already hit the `nuget-publish` approval gate, so the latency difference
  is smaller than it looks. *Preferred, but see the caveat.*
- **C. Hybrid** — publish on push for urgency, and open the back-merge PRs from the same run.

**Caveat on B, and the reason the no-PR path exists:** a hotfix on an *older* line must not merge to
`main`, because that would regress main to an older release. B is correct for case 1 (current line)
only. Case 2 (older line) needs develop-only back-merge regardless of which option is chosen.

---

## Invariants that must survive any change

- **A publish is never cancelled mid-push.** The concurrency group keys push runs by SHA so a third
  push cannot evict a queued one — an evicted run reports green having published nothing (#539).
- **Yielding only ever runs downhill.** A run may yield to another covering the same SHA, never the
  reverse: the yielding run's skip resolves in seconds; the real results land minutes later and win
  as the last report for each name.
- **The queue build always runs**, even when its suites skip, because the push run's
  `verify-rebuild` reads its determinism manifest.
- **Byte-identity is not a gate.** Generator nondeterminism makes it flake; it blocked a publish once
  (#691). Tamper-evidence is the SLSA attestation on the published bits.
- **13 required contexts on `main`** are satisfied by the release-branch push run.
- **The release PR title is the version string.** Anything after `chore(release): v` is parsed as
  part of the version, and a bad one dies at `git tag` after everything else has run.
- **Disjoint version bands** between the three channels. See `RELEASING.md` → "Version increment
  rules" and the July 2026 incident.

---

## Open questions — settle before building

1. **P1 fallback:** should the release path refuse to publish when it cannot find the release-branch
   artifact? Failing closed means a release can block on artifact expiry; falling back to a rebuild
   silently restores today's weaker provenance. *Leaning: fail closed with a named recovery.*
2. **Does `main` still need `pack`?** It currently packs artifacts nobody publishes. No consumer was
   found, but that was not exhaustively verified.
3. **Should the release cut wait for green before opening the PR?** `start-release` currently pushes
   the branch and opens the PR simultaneously, which is what creates the duplicate-run problem the
   `release-pr` gate solves. Opening after green removes the gate's reason to exist, at the cost of a
   ~40-minute window where the release is invisible.
4. **Is one SonarCloud main-line analysis per release the right cadence?** It is the only reason
   `main` is a push trigger. If a stale main-line badge between releases is acceptable, the trigger
   goes and P2 is moot.
5. **Does `release_type=auto` offer the betaed number or skip past it?** Once `v0.Y.0-beta.1` is the
   highest repo-wide tag, GitVersion keys off it. **Unverified.** If auto jumps to `0.(Y+1).0`, P8's
   Rule 1 is not a nicety, it is the whole feature. Settle with a `dry_run` dispatch, not by
   reasoning.
6. **Should `develop`'s label ever change as 1.0 approaches?** Moving it from `alpha` to `beta` during
   stabilization immediately re-creates the shared-label collision of F7. *Leaning: leave develop on
   `alpha` permanently and let deliberate cuts carry the phase.*
7. **Which P9 option**, and how to detect "older line" versus "current line" reliably.

---

## Gotchas discovered while assessing this

These cost real time. They are not in any doc.

- **`main` is protected by a ruleset, not classic branch protection.**
  `gh api repos/whizbang-lib/whizbang/branches/main/protection` returns **404 "Branch not
  protected"**, which reads as "no gating exists". The truth is at
  `gh api repos/whizbang-lib/whizbang/rules/branches/main` — 13 required contexts.
- **A skipped job that `uses:` a reusable workflow reports under the *parent* job name.** `Build`
  when skipped, `Build / Build` when it runs. So the PR-side skip and the push-side real result do
  not share a check name, and cannot mask each other. This makes the dedup safer than the in-code
  comment assumes.
- **`pull_request` triggers filter on the BASE branch only.** There is no head-branch filter, so a
  release PR always starts a CI run. Its jobs can be skipped; the run itself cannot be prevented.
  This is why duplicate *rows* remain on a release PR even though the duplicate *work* is gone.
- **GitHub applies `success()` implicitly unless the `if` contains a status-check function.** This is
  the rule F3 turns on, and getting it backwards leads to reporting a nonexistent vulnerability.
- **Address workflow runs by path, never by `.name`.** This workflow sets a `run-name`, so a run of
  `ci.yml` is never named plain "CI". Filtering on `.name` silently matches nothing — it has already
  broken two gates. Use `repos/$REPO/actions/workflows/ci.yml/runs?...`.
- **`gh pr merge <n> --auto` prints nothing and exits 0 when the PR is already merged**, which looks
  identical to a silently failed enqueue. Read back `autoMergeRequest` or `state` to tell them apart.

---

## Verification commands

```bash
# What actually ran, and what was skipped, on a given run
gh run view <id> --json jobs -q '.jobs[] | "\(.conclusion // .status)  \(.name)"' | sort

# Duplicate check-run names on a PR (entries vs distinct names)
gh pr checks <n> --json name -q '"entries: \(length)  distinct: \([.[].name] | unique | length)"'

# The real gating on main
gh api repos/whizbang-lib/whizbang/rules/branches/main \
  --jq '.[] | select(.type=="required_status_checks") | .parameters.required_status_checks[].context'

# Cost of a main push run
gh run list --workflow=ci.yml --branch main --limit 4 \
  --json databaseId,conclusion,createdAt,updatedAt \
  -q '.[] | "\(.databaseId) \(.conclusion) \(.createdAt) -> \(.updatedAt)"'

# Prerelease labels actually in use
git tag -l 'v*' | sed -n 's/.*-\([a-z]*\)\..*/\1/p' | sort | uniq -c

# Lint before pushing any workflow change
actionlint .github/workflows/ci.yml
```
