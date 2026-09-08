---
description: Watch a PR's CI, read the Sonar gate and security findings, list uncovered new lines, and fix what fails
---

# /pr-health <pr-number>

Monitor a pull request the way a contributor's AI should: with the repository's own scripts, so the
procedure improves in one place and needs no memory. Run from the repository root; `gh` must be
authenticated. Every script prints a human report by default and JSON with `-Json`.

1. **Wait for the checks.** `pwsh scripts/Watch-PrChecks.ps1 -PullRequest <n>` prints each check as it
   settles and exits 0 when all passed, 1 when any failed (with links), 2 on timeout. Use `-Once` for a
   snapshot. Do not poll by hand.
2. **If "SonarCloud Code Analysis" or "Quality / Quality Analysis" failed:**
   `pwsh scripts/Get-SonarPrFindings.ps1 -PullRequest <n>` prints the failing gate conditions and every
   open vulnerability, bug and hotspot with file, line, rule and message. Fix each finding at its source.
   A schema-qualified function name in a SQL string (S2077) is not injection: it is a validated
   constant, and the accepted fix is the documented `#pragma warning disable S2077` with the reason,
   as the coordinators do.
3. **Coverage of new lines must be 100%.** The Quality job posts the uncovered lines on the PR and
   uploads them as the `uncovered-new-lines` artifact. To reproduce locally:
   `pwsh scripts/Find-UncoveredNewLines.ps1 -CoverageRoot coverage-ci -BaseRef origin/develop -DownloadFromRun <run id>`
   (the run id is in the "CI Result" check's link). Cover every listed line with a test: a fake
   `TimeProvider` for latency and cadence branches, a throwing fake for failure logs, a canceled token
   for cancellation rethrows. A branch that the SQL contract makes unreachable may be restructured so
   the guard disappears, with the reason in a comment; nothing else is removed for coverage.
4. **Re-run the affected test projects one at a time** (never in parallel on a shared machine), format,
   build Release, commit, push, and go back to step 1.

Read `ai-docs/tdd-strict.md` and `ai-docs/coverage-exclusions.md` before deciding a line cannot be
covered. The contributor guide on the docs site ("Keeping new code at 100% coverage") explains the
same process for people.
