# OpenSSF Scorecard Follow-ups

**Status**: Planned (not started)
**Researched**: 2026-09-27, against `origin/develop` and a Scorecard run taken the same day
**Owner**: Phil Carbone

## Context

The OpenSSF Scorecard for this repository went from 5.5 to 7.4 on 2026-09-26/27:

| Check | Was | Now | How |
|---|---|---|---|
| Vulnerabilities | 0 | 10 | Sample UI npm lockfile refreshed, SSH.NET pinned in the sample tests (#910) |
| Pinned-Dependencies | 7 | 10 | Committed `packages.lock.json` + `--locked-mode` restores (#910) |
| Token-Permissions | 0 | 10 | `contents: read` at every workflow's top level, writes on jobs (#910, #912) |
| Branch-Protection | 0 | 3 | Force push and deletion blocked on `main` and `develop` (ruleset change) |
| Signed-Releases | 0 | 2 | Stable releases attach `.sigstore.json` and `.intoto.jsonl` (#910) |

Scorecard runs only on the default branch (`develop`); dispatching it on `main` fails with
"Only the default branch develop is supported". Re-run it with
`gh workflow run security-scorecard.yml --ref develop`, then read
`https://api.securityscorecards.dev/projects/github.com/whizbang-lib/whizbang`.

This plan covers what remains. Items 1, 2 and 6 are the work; items 3 to 5 are decisions already
made, recorded so nobody re-litigates them.

## 1. Fuzzing: property-based tests with FsCheck (do it)

**Decision (owner, 2026-09-27):** add FsCheck and write real property tests. MC/DC
(`plans/mcdc-coverage-program.md`) does not satisfy this check: Scorecard does not measure coverage
criteria, it looks for a fuzzer.

### What Scorecard detects

From `checks/raw/fuzzing.go` in ossf/scorecard: for C# it scans `*.cs` files for the regex
`(using\s+(FsCheck|FsCheck\.(NUnit|Xunit)|Expecto\.ExpectoFsCheck));`. That is a **literal
`using FsCheck;`** (or the NUnit/Xunit integration namespaces). Consequences:

- `using FsCheck.Fluent;` alone does **not** match. FsCheck 3's C# API (`Prop.ForAll`, `Gen`,
  `Arb`) lives in `FsCheck.Fluent`, so a file needs `using FsCheck;` as well, and must actually use
  something from that namespace (`Config`, `Check`), or IDE0005 / `dotnet format` removes the using
  and the check silently drops back to 0. Add a comment on that using saying why it must stay.
- `global using FsCheck;` in a GlobalUsings file also matches the regex; either form works.
- The NUnit/Xunit integrations do not apply: tests here are TUnit only. Run properties from inside
  ordinary `[Test]` methods with `Check.One(Config.QuickThrowOnFailure, property)` (or the
  `Prop.ForAll(...).QuickCheckThrowOnFailure()` extension), so a failing property throws and fails
  the TUnit test with FsCheck's shrunk counterexample in the message.

### Package

- `FsCheck` 3.4.0 (latest stable as of 2026-09-27) in `Directory.Packages.props`; reference it only
  from the test projects that use it. It is an input-generation library, not a test framework, so
  the TUnit + Rocks rule is unaffected; say so in the PR.
- Lock files are committed and CI restores in locked mode: run `dotnet restore Whizbang.slnx` and
  commit every changed `packages.lock.json` in the same commit (see CONTRIBUTING, "Package Versions
  and Lock Files").
- AOT: test projects only; nothing ships with FsCheck.

### Properties worth writing (real value, not a badge)

Pick properties whose failure would be a real bug. Candidates, all pure and fast:

1. **UUIDv7 generator ordering** (`Uuid7Generator`, Core): for any sequence of clock readings,
   including regressions and many calls within one millisecond, generated ids are strictly
   increasing in byte order and the embedded timestamp never goes backwards. Uses the injectable
   clock and random-fill seams that already exist.
2. **Derived identity stability** (`DerivedIdentity` / `EmissionIdentity` /
   `CompositeChildIdentity`): same source + ordinal + canonical always yields the same id; different
   ordinals below 4096 yield different ids; ordinals at or above 4095 saturate; version and variant
   bits are always correct; source bytes 0..9 are preserved.
3. **JSON round-trips** for the id value objects and the generated WhizbangId converters: any Guid
   serializes and deserializes to itself, independent of culture.
4. **Type-name formatting** (`TypeNameFormatter` / `EventTypeMatchingHelper`): formatting then
   matching a generated type name (nested, generic, arrays) always finds itself. See
   `ai-docs/type-naming.md` for why these forms are keys.
5. **Tracked Guid metadata**: flag combinations round-trip, and `SubMillisecondPrecision` /
   `IsTracking` agree with the source flags for every combination.

Aim for three to five properties in the first PR, each with a sensible `MaxTest` and a fixed
replay seed documented in the failure message. Keep runtime small: these run in the unit suite on
every PR.

### Definition of done

- Properties pass locally and in CI; the PR gate is green (100% new-line coverage, zero Sonar
  findings; `/pr-health <n>` watches it and lists uncovered new lines).
- Root `dotnet format --verify-no-changes --no-restore` is clean and the `using FsCheck;` survived it.
- Scorecard re-run on `develop` after merge shows Fuzzing above 0 (expected 10).
- Docs: add a short section to the docs site's testing contributor guide on writing FsCheck
  properties under TUnit, and link the tests (`<tests>` tags where the code has them).

## 2. CII-Best-Practices: draft the OpenSSF Best Practices answers (do it)

**Decision (owner, 2026-09-27):** write the full draft so the owner can register and paste it in.

Scoring: in progress 2, passing 5, silver 7, gold 10. Gold requires multiple active developers, so
**passing is the realistic target**, with silver worth checking once passing is done.

### What the owner must do (not delegable)

1. Sign in at <https://www.bestpractices.dev/> with the GitHub account that administers the repo.
2. Add the project with repo URL `https://github.com/whizbang-lib/whizbang`.
3. Paste the drafted answers, criterion by criterion, and submit.
4. Add the badge to the README (the next session can do this once the project id exists).

### What the next session writes

A single file, `docs/openssf-best-practices-draft.md` (or a docs-site page if preferred), with
every **passing** criterion from <https://www.bestpractices.dev/criteria/0> in order: the criterion
id, the answer (Met / Unmet / N/A), and a one- to three-sentence justification with a URL into the
repo or docs site. Verify every claim against the repo before writing it. Evidence already known to
exist on `develop`:

- **Basics**: MIT `LICENSE`; `README.md` (what it does, how to get it); docs site
  <https://whizba.ng>; `CONTRIBUTING.md` (process, standards, lock files); `CHANGELOG.md`.
- **Change control**: public git on GitHub; every change by pull request; `docs/RELEASING.md`
  (SemVer, gitflow, unique version per release, release notes on GitHub releases).
- **Reporting**: GitHub issues; `SECURITY.md` for private vulnerability reporting through GitHub's
  "Report a vulnerability" flow, with a stated response time of 48 hours.
- **Quality**: full CI matrix on every PR; PR gate requires 100% of new lines covered and zero
  Sonar findings; TUnit suites (the README cites the published test count); `TreatWarningsAsErrors`
  and analyzers in every project; policy that new functionality gets tests (TDD, `ai-docs/tdd-strict.md`).
- **Security**: CodeQL, SonarCloud, secret scanning, OSV / supply-chain workflow, Dependabot;
  committed lock files with locked restores; signed release provenance; no known unpatched
  vulnerabilities of medium or higher severity; HTTPS-only delivery (GitHub, NuGet).
- **Analysis**: static analysis on every PR (CodeQL, Sonar, Roslyn analyzers). Dynamic analysis:
  the property tests from item 1, plus Stryker mutation testing (`ai-docs/mutation-testing.md`).

Expected gap to flag rather than paper over: any criterion about a second person reviewing changes
(see item 3). Mark it Unmet with the reason; do not claim AI or automated review as human review.

## 3. Code-Review and review-dependent Branch-Protection items (deferred)

**Decision (owner, 2026-09-27): defer.** Phil is the sole active developer; the other
collaborators (maintain role) are contributors who are usually not available to approve PRs, and a
PR author cannot approve their own PR, so requiring an approval would block every merge. Work is
reviewed by automated gates instead (full test matrix, 100% new-line coverage, Sonar, CodeQL, AI
review), which Scorecard explicitly does not count as code review.

This leaves Code-Review at 0 and caps Branch-Protection, whose remaining warnings are mostly the
same decision: required approvers, code-owner review, and last-push approval on `develop` and
`main`. Revisit if a second regular reviewer joins. Ideas recorded, not adopted:

- Required approval on `main` only, since release PRs are rare and could wait for a contributor.
  Would also block the automated test-status PR into `main` (`ci.yml` merges it itself).
- Move automated PRs (release, test-status, README refresh, develop sync) to a bot identity:
  Scorecard deducts only 3 for unreviewed bot changes versus 7 to 10 for human ones.

Two non-review Branch-Protection warnings remain and are also left as they are:

- **"Up-to-date branches" (strict status checks)**: `develop` already serializes merges through the
  merge queue, which is the stronger guarantee. On `main`, strict mode would make release PRs chase
  unrelated `main` commits.
- **"Applies to administrators" on `develop`**: the ruleset lets org admins bypass. That bypass is
  the owner's recovery path; removing it is the owner's call.

## 4. Signed-Releases (no work)

Scorecard reads the last five GitHub releases that have assets. From v0.2601.0 on, every stable
release attaches `whizbang-<v>.sigstore.json` and `whizbang-<v>.intoto.jsonl` (SLSA provenance),
which is worth the full 10. The score climbs by one release in five: 2 today, 10 after four more
stable releases. Only if a prerelease ever starts creating GitHub releases with assets would it need
the same attachments; today it does not.

## 5. Not planned

- **Contributors (6)**: counts contributing organizations; not something to engineer.

## 6. License: let GitHub recognize the MIT license (do it, one line)

Scorecard gives License 9 because GitHub's license detection reports `NOASSERTION` for this repo
(`gh api repos/whizbang-lib/whizbang/license -q .license.spdx_id`). The `LICENSE` file is the MIT
text plus an extra `SPDX-License-Identifier: MIT` line under the title, and that extra line stops
GitHub's matcher from recognizing the standard text. Remove that line (keep the copyright line and
the standard MIT body exactly), then confirm the API reports `MIT` after merge and Scorecard shows
License 10. Package metadata already declares the license separately, so nothing else changes.
