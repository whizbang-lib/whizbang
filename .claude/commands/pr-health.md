---
description: Run the PR health recipe (checks, Sonar findings, uncovered new lines and branches), read its saved report, and fix what it lists
---

# /pr-health <pr-number>

The standard for every pull request: all checks green, **zero open Sonar findings of any type on new
code**, and **100% coverage of new lines and of every outcome of new hand-written decisions**. One script runs the whole recipe and saves one report:

```
pwsh scripts/Invoke-PrHealth.ps1 -PullRequest <n>
```

It waits for the checks to settle (add `-Snapshot` for the current state), reads the SonarCloud gate
and every open finding, downloads the CI run's coverage artifacts for the head commit and computes the
uncovered new lines and hand-written branches, then writes `.whizbang/cache/pr-health/pr-<n>-<timestamp>.md` with the raw JSON
and text beside it. Exit code 0 means clean; 1 means the report lists what to fix. The report also
carries the whole-library line the PR comment shows ("Whole library: lines 99.9%, hand-written branches
98.6% (382 outcomes untested)"); it is informational and does not affect the verdict. Merging the
coverage across test processes reads the binary reports with `dotnet-coverage`
(`dotnet tool install --global dotnet-coverage --version 18.12.0`).

Read the report, then:

1. **Failed checks**: open the linked job log, fix the cause, and re-run the affected test projects
   one at a time (never in parallel on a shared machine).
2. **Sonar findings**: fix each at its source, tests included. A finding that is wrong for a documented
   reason (a schema-qualified function name in SQL text is S2077 but not injection; Npgsql's own array
   idiom) is resolved with the reason in a comment or a pragma on the line, never left open and never
   suppressed silently.
3. **Uncovered new lines**: cover each with a test. Fake `TimeProvider` for latency and cadence
   branches, a throwing fake for failure logs, a canceled token for cancellation rethrows. A branch a
   contract makes unreachable may be restructured so the guard disappears, with the reason in a
   comment; nothing else is removed for coverage.
4. Format, build Release, commit, push, and run the recipe again.

The individual scripts behind the recipe (`Watch-PrChecks.ps1`, `Get-SonarPrFindings.ps1`,
`Find-UncoveredNewLines.ps1`) take `-OutFile` and `-Json` for a narrower question. Improve the scripts
when they fall short; every contributor's assistant gets the improvement. The contributor guide on the
docs site ("Keeping New Code at 100% Coverage and Zero Sonar Findings") explains the same process for people.
