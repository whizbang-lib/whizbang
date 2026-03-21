# Plan: PR Readiness Scripts & Tooling

## Context
We need a turnkey, multi-platform script infrastructure for preparing PRs. The current `Run-Tests.ps1` has coverage support but needs significant enhancement. This plan covers all scripts and their interactions.

## Scripts to Build

### 1. `Run-Tests.ps1` (enhance existing)
**Modes**: Unit, Integration, All, Ai, AiUnit, AiIntegrations
**Coverage**: `-Coverage` flag works with any mode
**Features needed**:
- Auto-install tools (reportgenerator via `dotnet tool install -g`, consider `.config/dotnet-tools.json` for repo-local tools)
- Coverage report: HTML (reportgenerator) + console summary with uncovered lines
- Link to full HTML report in output
- AI mode: instructions on fixing flaky tests, bad test design, race conditions
- Boy Scout rule: detect pre-existing failures, spin up agents to fix them
- Tee functionality: `-LogFile <path>` writes verbose output to file while console shows AI-mode summary
- JSON output mode: `-OutputFormat Json` for machine-readable results consumed by uber script
- History log: `logs/test-runs.jsonl` with test names, results, durations
- Estimated completion time: based on history (avg, p85, min/max, stddev) — only show when >1 historical run

### 2. `Run-Sonar.ps1` (new)
**Purpose**: Run SonarCloud/SonarQube analysis locally
**Features**:
- Auto-install SonarScanner (`dotnet tool install -g dotnet-sonarscanner`)
- Modes: verbose, AI-optimized, JSON
- Link to full SonarCloud report
- Console summary of quality gate status, new issues, coverage delta
- AI instructions on failures: fix all issues, categorize by severity
- Tee/log functionality matching Run-Tests.ps1
- History log: `logs/sonar-runs.jsonl`

### 3. `Prepare-PR.ps1` (new — the "uber script")
**Purpose**: Run ALL checks in sequence with fail-fast
**Steps**:
1. `dotnet format --verify-no-changes` — formatting check
2. `dotnet build` — compilation
3. `Run-Tests.ps1 -Mode AiUnit -Coverage -FailFast -OutputFormat Json` — unit tests + coverage
4. `Run-Tests.ps1 -Mode AiIntegrations -FailFast -OutputFormat Json` — integration tests (if applicable)
5. `Run-Sonar.ps1 -OutputFormat Json` — quality gate
6. Coverage threshold check (configurable, default 80%)
7. Generate summary

**Fail-fast**: If any step fails, stop immediately and report what failed + how to fix
**Estimated time**: Based on aggregated history from child scripts
**Output**:
- Console summary of all steps (pass/fail, duration)
- Link to each child report (HTML coverage, SonarCloud)
- AI instructions: specific guidance per failure type
- JSON mode for CI consumption

### 4. Tool Management
**Approach**: Use `.config/dotnet-tools.json` (repo-local manifest) for:
- `dotnet-reportgenerator-globaltool`
- `dotnet-sonarscanner` (if applicable)
- Any other tools

Scripts check for manifest first, fall back to global install.
Multi-platform: PowerShell Core + dotnet tools work on Windows/macOS/Linux.

## Script Design Patterns

### Tee/Logging
```powershell
param(
  [string]$LogFile,           # Write verbose output to file
  [switch]$TeeVerbose         # File gets verbose even when console is AI mode
)
# Console output: AI-friendly (sparse)
# File output: Full verbose (for investigation)
# Log file name printed in initial header
```

### JSON Output Mode
```powershell
param(
  [ValidateSet("Text", "Json")]
  [string]$OutputFormat = "Text"
)
# When Json: output a single JSON object at end with structured results
# Uber script parses JSON to understand child script results
```

### History & Estimation
```powershell
# Each run appends to logs/test-runs.jsonl:
# {"timestamp":"2026-03-21T08:00:00Z","mode":"AiUnit","duration_s":45.2,"total":5986,"failed":2,"passed":5984,"failed_tests":["Diag1","Diag2"]}

# Estimation from history:
# - Average, P85, Min, Max, StdDev of duration
# - Only show if >1 historical run
# - "Estimated completion: ~50s (avg 48.3s, p85 55.1s, range 42-62s)"
```

### AI Instructions on Failure
```
=== AI INSTRUCTIONS ===
Test failures detected. Follow these steps:
1. Fix ALL failing tests, even pre-existing ones (Boy Scout rule)
2. For flaky tests (intermittent failures):
   - Remove race conditions (don't use Thread.Sleep, use proper sync)
   - Don't assert on timing (use completion signals instead)
   - Model after stable tests in the same file
3. For test design issues:
   - No shared mutable state between tests
   - Each test must be independently runnable
   - Use [NotInParallel] only when truly needed
4. Spin up an agent for each independent fix
5. Re-run this script after fixes to verify
========================
```

## Implementation Order
1. Enhance `Run-Tests.ps1` with tee, JSON, history, AI instructions
2. Create `Prepare-PR.ps1` (uber script)
3. Create `Run-Sonar.ps1`
4. Add `.config/dotnet-tools.json` manifest
5. Update CLAUDE.md with new script documentation

## Notes
- All scripts should work in CI and locally
- PowerShell Core 7+ required (already enforced via `#Requires -Version 7.0`)
- `.gitignore` already has `coverage-report/`, need to add `logs/` for history files
- Each script should have proper `.SYNOPSIS`/`.DESCRIPTION`/`.EXAMPLE` help
