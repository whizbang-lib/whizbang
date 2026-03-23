Run the PR lifecycle script to prepare, create, and/or monitor a pull request.

Execute: `pwsh scripts/Run-PR.ps1 -Mode Ai`

Actions:
- **Prepare**: `pwsh scripts/Run-PR.ps1 -Action Prepare -Mode Ai` — format, build, test, sonar
- **Create**: `pwsh scripts/Run-PR.ps1 -Action Create` — push branch, create PR (gitflow-aware)
- **Monitor**: `pwsh scripts/Run-PR.ps1 -Action Monitor` — watch CI checks with live table
- **Full**: `pwsh scripts/Run-PR.ps1 -Mode Ai` — prepare + create + monitor (default)

Key flags (consistent across all PR readiness scripts):
- `-Mode All|Ai` — output verbosity
- `-OutputFormat Text|Json` — human or machine-readable output
- `-LogFile <path> -LogMode All` — tee verbose output to file while console stays sparse
- `-FailFast` — stop on first failure
- `-CoverageThreshold 80` — minimum coverage percentage
- `-SkipSonar` / `-SkipIntegration` — skip specific steps
- `-Draft` — create PR as draft
