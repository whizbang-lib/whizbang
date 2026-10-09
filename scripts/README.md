# Scripts

This directory contains utility scripts for development, testing, and maintenance.

All scripts use **PowerShell Core (pwsh)** for cross-platform compatibility.

---

## Testing

### Run-Tests.ps1

Runs all test projects in the Whizbang solution with parallel execution and AI-optimized output modes.

**Usage:**
```powershell
pwsh scripts/Run-Tests.ps1                                    # Default: Ai mode, ALL tests (8000+)
pwsh scripts/Run-Tests.ps1 -Mode AiUnit                       # Unit tests only (fast, ~5800 tests)
pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations               # Integration tests only
pwsh scripts/Run-Tests.ps1 -Mode Unit                         # Unit tests with verbose output
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Core"              # Filter to specific project
pwsh scripts/Run-Tests.ps1 -TestFilter "ProcessWorkBatchAsync" # Filter to specific tests
```

**Modes:**
- `Ai` (default) - AI-optimized sparse output, ALL tests
- `AiUnit` - AI-optimized output, unit tests only
- `AiIntegrations` - AI-optimized output, integration tests only
- `Unit` - Verbose output, unit tests only
- `Integration` - Verbose output, integration tests only

**Parameters:**
- `-MaxParallel` - Maximum parallel test projects (default: CPU core count)
- `-ProjectFilter` - Filter to specific test projects (e.g., "Core", "EFCore.Postgres")
- `-TestFilter` - Filter to specific tests by name pattern
- `-Mode` - Test execution mode (default: Ai)
- `-Verbose` - Show detailed test output

**Output:**
- Summary of passed/failed/skipped tests
- Per-project breakdown
- Pass rate percentage

---

### Move-TestReference.ps1

Relinks everything that points at a test file or test folder that moves (to another project, or within one). Every test move goes through it, and its counts go in the PR description.

**Usage:**
```powershell
pwsh scripts/Move-TestReference.ps1 -From tests/Example.Tests/Async/SignalTests.cs -To tests/Example.Component.Tests/Async/SignalTests.cs -DryRun
pwsh scripts/Move-TestReference.ps1 -From tests/Example.Tests/Workers -To tests/Example.Component.Tests/Workers
pwsh scripts/Move-TestReference.ps1 -MapFile moves.csv -UpdateDocsSite    # CSV columns: From,To
```

**Parameters:**
- `-From` / `-To` - Old and new repository-relative paths (files or folders), paired in order
- `-MapFile` - CSV of moves (From,To columns); combines with -From/-To
- `-RepositoryRoot` - Library checkout (default: the folder above this script)
- `-DocsSiteRoot` - Docs-site checkout (default: `whizbang-lib.github.io` next to the library checkout)
- `-UpdateDocsSite` - Also rewrite the docs site's hand-written references (default: report only)
- `-DryRun` - Write nothing; report what a real run would rewrite (`-WhatIf` also works and lists each file)

**What it touches:**
- Rewrites `<tests>` tags (C# doc comments, SQL comments) and other mentions in `src/**`, plus `ai-docs/**`, `plans/**/*.md`, `docs/**/*.md`, every `README*.md` and every `CLAUDE.md`
- Matches whole paths only: moving `tests/A.Tests/Foo.cs` leaves `FooBar.cs` alone, moving `tests/A.Tests` leaves `tests/A.Tests.Extra/` alone
- Keeps each rewritten file's encoding, byte order mark and line endings; never writes a file without a reference
- Stops before writing anything when a move's paths exist on neither side (a typo)
- Docs site: rewrites hand-written pages only with `-UpdateDocsSite`; reports script code (`.mjs`, `.js`, `.ts`) for review by hand; never rewrites generated files (`code-tests-map.json`, the VS Code feed, `test-status/*.json`, the static page, search indexes, audit reports) and lists them as "regenerate" items with their generator; counts the class-based `tests=[...]` and `{verified: ...}` markers naming a moved class, which a path move leaves valid

**Output:**
- Counts for the PR: tags rewritten (and files), other source references, library doc references, docs-site references found and rewritten, docs-site generated artifacts to regenerate
- A result object on the pipeline (`DryRun`, `TagsRewritten`, `Changes`, `Regenerate`, ...) for scripting

---

## Coverage

### run-all-tests-with-coverage.ps1

Runs all test projects in the solution and collects code coverage.

**Usage:**
```powershell
pwsh scripts/coverage/run-all-tests-with-coverage.ps1
pwsh scripts/coverage/run-all-tests-with-coverage.ps1 -Configuration Release
```

**Parameters:**
- `-Configuration` - Build configuration (Debug or Release). Default: Debug

**Output:**
- Coverage files: `tests/*/TestResults/coverage.xml` (Cobertura format)
- Summary displayed in console

---

### merge-coverage.ps1

Merges multiple coverage files into a single unified report.

**Usage:**
```powershell
pwsh scripts/coverage/merge-coverage.ps1
pwsh scripts/coverage/merge-coverage.ps1 -OutputPath ./coverage-merged.xml
```

**Parameters:**
- `-OutputPath` - Path for merged coverage file. Default: `./coverage-merged.xml`

**Input:**
- Searches for all `coverage.xml` files in `tests/*/TestResults/`

---

### show-coverage-report.ps1

Displays a formatted coverage report from Cobertura XML files.

**Usage:**
```powershell
pwsh scripts/coverage/show-coverage-report.ps1
pwsh scripts/coverage/show-coverage-report.ps1 -CoveragePath ./coverage.xml
```

**Parameters:**
- `-CoveragePath` - Path to coverage file. Default: searches TestResults folders

**Output:**
- Line coverage percentage
- Branch coverage percentage
- Per-project breakdown

---

## Code Quality

### Run-SonarAnalysis.ps1

Runs SonarCloud analysis locally to check code quality before pushing changes. Detects bugs, code smells, vulnerabilities, and technical debt.

**First-time setup:**
1. Restore tools: `dotnet tool restore` (installs dotnet-sonarscanner from `.config/dotnet-tools.json`)
2. Generate a token at: https://sonarcloud.io/account/security
3. Run the script - it will guide you through saving the token
4. Token is stored in `.sonarcloud.token` (gitignored, never committed)

The script automatically restores tools from the manifest on each run.

**Usage:**
```powershell
pwsh scripts/Run-SonarAnalysis.ps1                    # Use token from env var or .sonarcloud.token
pwsh scripts/Run-SonarAnalysis.ps1 -Token "your-token" # Provide token directly
pwsh scripts/Run-SonarAnalysis.ps1 -SkipBuild         # Skip rebuild (faster if you just built)
```

**Token priority:**
1. `-Token` parameter
2. `SONAR_TOKEN` environment variable
3. `.sonarcloud.token` file (created automatically on first run)

**What it analyzes:**
- Code smells, bugs, vulnerabilities
- Cognitive complexity (S3776)
- Technical debt calculation
- Security hotspots
- Excludes: samples, benchmarks, generated code

**Output:**
- Analysis results uploaded to SonarCloud
- View dashboard at: https://sonarcloud.io/project/overview?id=whizbang-lib_whizbang

---

## Benchmarks

### run-benchmarks.ps1

Runs performance benchmarks using BenchmarkDotNet.

**Usage:**
```powershell
pwsh scripts/benchmarks/run-benchmarks.ps1
pwsh scripts/benchmarks/run-benchmarks.ps1 -Filter "*TracingBenchmarks*"
```

**Parameters:**
- `-Filter` - Filter benchmarks by name pattern (optional)

**Output:**
- BenchmarkDotNet results in `benchmarks/Whizbang.Benchmarks/BenchmarkDotNet.Artifacts/results/`
- HTML and markdown reports
- Console summary

---

## Diagnostics

### show-diagnostics.ps1

Shows all diagnostics generated by the Whizbang source generator.

**Usage:**
```powershell
pwsh scripts/diagnostics/show-diagnostics.ps1
```

**VS Code Integration:**
1. Press `Cmd+Shift+P` / `Ctrl+Shift+P`
2. Type: **"Tasks: Run Task"**
3. Select **⚡ Show Generator Diagnostics**

For keyboard shortcuts and status bar integration, see [.vscode/SETUP.md](../.vscode/SETUP.md).

**Output:**
- **WHIZ001** (Info) - Discovered receptors (green)
- **WHIZ002** (Warning) - No receptors found (yellow)
- **WHIZ003** (Error) - Invalid implementations (red)

**Example:**
```
🔍 Running Whizbang source generator diagnostics...

info WHIZ001: Found receptor 'OrderReceptor' handling CreateOrder → OrderCreated
info WHIZ001: Found receptor 'PaymentReceptor' handling ProcessPayment → PaymentProcessed
```

---

## Maintenance

### clean-generator-locks.ps1

Cleans file locks from Whizbang source generator DLLs caused by lingering processes.

**Problem it solves:**
- ILRepack error: "The file '.../Whizbang.Generators.dll' already exists"
- Aspire doesn't always terminate child processes when debugging stops
- File locks prevent rebuilding generator projects

**Usage:**
```powershell
pwsh scripts/maintenance/clean-generator-locks.ps1
```

**What it does:**
1. Kills ECommerce and Aspire AppHost processes
2. Shuts down dotnet build server
3. Cleans generator project build artifacts
4. Removes locked DLL/PDB files

**When to use:**
- Cannot rebuild after stopping Aspire debugging
- Getting "file already exists" errors during build
- Generator changes not being picked up

---

### remove-coverage-from-history.ps1

Removes coverage files from Git history (reduces repository size).

**⚠️ WARNING: Rewrites Git history - use with caution!**

**Usage:**
```powershell
pwsh scripts/maintenance/remove-coverage-from-history.ps1
```

**What it does:**
- Removes all `*.coverage`, `*.coveragexml`, and `coverage.xml` files from history
- Reduces repository size
- **Requires force push** after running

**Only run if:**
- Coverage files were accidentally committed
- Repository size needs reduction
- You have team agreement to rewrite history

---

## Code Fixes

### Fix-IDE1006-PrivateProtectedMembers.ps1

Automatically fixes IDE1006 naming violations for private and protected members.

**Usage:**
```powershell
pwsh scripts/Fix-IDE1006-PrivateProtectedMembers.ps1
```

**What it fixes:**
- Private fields to `_camelCase` convention
- Protected fields to `_camelCase` convention
- Private/protected methods to `PascalCase` convention

---

### Fix-IDE1006-PrivateStaticReadonly.ps1

Automatically fixes IDE1006 naming violations for private static readonly fields.

**Usage:**
```powershell
pwsh scripts/Fix-IDE1006-PrivateStaticReadonly.ps1
```

**What it fixes:**
- Private static readonly fields to `PascalCase` convention (e.g., `DefaultValue`, `EmptyString`)

---

## Prerequisites

### PowerShell Core (pwsh)

All scripts require PowerShell Core for cross-platform compatibility.

**Install PowerShell Core:**

- **Windows**: Included with Windows 10+, or install PowerShell 7+ from [GitHub releases](https://github.com/PowerShell/PowerShell/releases)
- **macOS**: `brew install powershell`
- **Linux**: See [installation instructions](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-linux)

**Verify installation:**
```powershell
pwsh --version
```

---

## Script Standards

All scripts in this directory follow these standards:

- ✅ **PowerShell Core** for cross-platform compatibility
- ✅ **kebab-case.ps1** naming convention
- ✅ **Categorized folders** (coverage, benchmarks, diagnostics, maintenance)
- ✅ **Parameter validation** with help documentation
- ✅ **Error handling** with `Set-StrictMode` and `$ErrorActionPreference = 'Stop'`
- ✅ **Colored output** for better readability

For detailed script development standards, see [ai-docs/script-standards.md](../ai-docs/script-standards.md).

---

## Quick Reference

### Common Tasks

```powershell
# Run all tests with coverage
pwsh scripts/coverage/run-all-tests-with-coverage.ps1

# Show coverage report
pwsh scripts/coverage/show-coverage-report.ps1

# Run benchmarks
pwsh scripts/benchmarks/run-benchmarks.ps1

# Show generator diagnostics
pwsh scripts/diagnostics/show-diagnostics.ps1
```

### VS Code Integration

Many scripts have VS Code task integration. See [.vscode/SETUP.md](../.vscode/SETUP.md) for:
- Keyboard shortcuts
- Status bar buttons
- Task runner configuration
- Debug integration

---

## Contributing

When adding new scripts:

1. **Choose category**: coverage, benchmarks, diagnostics, maintenance, or create new category
2. **Use PowerShell Core**: `.ps1` files preferred over `.sh`
3. **Follow naming**: kebab-case (e.g., `run-all-tests.ps1`)
4. **Add parameters**: Use param block with validation
5. **Include help**: Add comment-based help at top of script
6. **Update README**: Document the new script here
7. **Test cross-platform**: Verify on Windows, macOS, and Linux

See [ai-docs/script-standards.md](../ai-docs/script-standards.md) for detailed guidelines.
