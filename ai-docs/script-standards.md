# Script Standards

**Multi-Platform Scripts with PowerShell Core**

All scripts in the Whizbang project must be multi-platform compatible. This document defines standards for script development, organization, and containerization.

---

## Table of Contents

1. [Preferred Technology: PowerShell Core](#preferred-technology-powershell-core)
2. [When Bash is Acceptable](#when-bash-is-acceptable)
3. [Container-Based Tools](#container-based-tools)
4. [Script Organization](#script-organization)
5. [Script Documentation](#script-documentation)
6. [Common Patterns](#common-patterns)

---

## Preferred Technology: PowerShell Core

### Why PowerShell Core (.ps1)

**Use `.ps1` for all new scripts:**

✅ **Benefits:**
- Cross-platform (Windows, macOS, Linux)
- Consistent syntax across platforms
- Rich object pipeline
- Strong typing support
- Excellent error handling
- Built-in cmdlets for common tasks
- Native .NET integration

❌ **Bash limitations:**
- Platform-specific quirks
- String-based pipeline
- Weak typing
- Inconsistent between shells (bash, zsh, sh)

---

### PowerShell Script Template

```pwsh
#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Runs all tests with code coverage collection.

.DESCRIPTION
    Executes all test projects in the solution, collects code coverage,
    and generates a unified coverage report.

.PARAMETER Configuration
    Build configuration to use (Debug or Release). Default: Debug

.PARAMETER Output
    Output directory for coverage reports. Default: ./coverage

.EXAMPLE
    ./run-tests-with-coverage.ps1

.EXAMPLE
    ./run-tests-with-coverage.ps1 -Configuration Release -Output ./reports
#>

param(
    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [Parameter()]
    [string]$Output = './coverage'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "Running tests with coverage..." -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Gray
Write-Host "Output: $Output" -ForegroundColor Gray
Write-Host ""

try {
    # Ensure output directory exists
    if (-not (Test-Path $Output)) {
        New-Item -ItemType Directory -Path $Output -Force | Out-Null
    }

    # Find all test projects
    $testProjects = Get-ChildItem -Path tests -Filter "*.csproj" -Recurse

    Write-Host "Found $($testProjects.Count) test projects" -ForegroundColor Green

    foreach ($project in $testProjects) {
        Write-Host "Testing: $($project.Directory.Name)" -ForegroundColor Yellow

        Push-Location $project.Directory

        try {
            # Run tests with coverage
            dotnet run -- `
                --coverage `
                --coverage-output-format cobertura `
                --coverage-output "$Output/$($project.Directory.Name)-coverage.xml"

            if ($LASTEXITCODE -ne 0) {
                throw "Tests failed in $($project.Directory.Name)"
            }
        }
        finally {
            Pop-Location
        }
    }

    Write-Host ""
    Write-Host "Coverage collection complete!" -ForegroundColor Green
    Write-Host "Reports available in: $Output" -ForegroundColor Gray
}
catch {
    Write-Error "Script failed: $_"
    exit 1
}
```

---

### Key PowerShell Patterns

**1. Parameter Validation:**
```pwsh
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectName,

    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [Parameter()]
    [ValidateRange(1, 100)]
    [int]$Parallelism = 4
)
```

**2. Error Handling:**
```pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    # Do work
    dotnet build
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
}
catch {
    Write-Error "Operation failed: $_"
    exit 1
}
finally {
    # Cleanup
    Pop-Location
}
```

**3. Cross-Platform Paths:**
```pwsh
# ✅ CORRECT - Use Join-Path for cross-platform
$coveragePath = Join-Path $PSScriptRoot "coverage" "results.xml"

# ❌ WRONG - Hard-coded separators
$coveragePath = "$PSScriptRoot/coverage/results.xml"  # Fails on Windows
$coveragePath = "$PSScriptRoot\coverage\results.xml"  # Fails on Linux/macOS
```

**4. File Operations:**
```pwsh
# Test if file exists
if (Test-Path $filePath) {
    Remove-Item $filePath -Force
}

# Create directory
New-Item -ItemType Directory -Path $dirPath -Force | Out-Null

# Copy files
Copy-Item -Path $source -Destination $dest -Recurse -Force

# Find files
$projects = Get-ChildItem -Path . -Filter "*.csproj" -Recurse
```

**5. Output Formatting:**
```pwsh
Write-Host "Starting build..." -ForegroundColor Cyan
Write-Host "✓ Build successful" -ForegroundColor Green
Write-Warning "Coverage is below threshold"
Write-Error "Tests failed"
```

---

## When Bash is Acceptable

### Acceptable Use Cases

Use `.sh` scripts **ONLY** when:
1. PowerShell truly won't work (rare)
2. Integrating with Unix-specific tools
3. Container entrypoint scripts (prefer pwsh)

**If using bash, MUST:**
- Document why PowerShell won't work
- Ensure cross-platform compatibility (Linux + macOS)
- Use `#!/usr/bin/env bash` shebang
- Test on multiple platforms

---

### Bash Script Template

```bash
#!/usr/bin/env bash
# NOTE: Using bash because [specific reason why PowerShell won't work]
# TODO: Consider converting to PowerShell if possible

set -euo pipefail  # Exit on error, undefined vars, pipe failures

# Script configuration
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Color output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}Starting operation...${NC}"

# Example function
run_tests() {
    local config="${1:-Debug}"

    echo -e "${YELLOW}Running tests (${config})...${NC}"

    if dotnet test -c "$config"; then
        echo -e "${GREEN}✓ Tests passed${NC}"
        return 0
    else
        echo -e "${RED}✗ Tests failed${NC}"
        return 1
    fi
}

# Main
main() {
    cd "$PROJECT_ROOT"

    run_tests "Release"
}

main "$@"
```

---

### Bash Portability Checklist

- [ ] Uses `#!/usr/bin/env bash` (not `#!/bin/bash`)
- [ ] Sets `set -euo pipefail`
- [ ] Avoids bashisms (works in bash, not just sh)
- [ ] Tested on Linux
- [ ] Tested on macOS
- [ ] Documented platform requirements
- [ ] Uses portable commands only

---

## Container-Based Tools

### Prefer Containers for Tools

**Benefits:**
- ✅ No local installation required
- ✅ Consistent versions across developers
- ✅ Isolated from host system
- ✅ Easy to update (change image version)

---

### Container Patterns

**1. Database Tools:**
```pwsh
#!/usr/bin/env pwsh
# Run PostgreSQL migrations using container

param(
    [string]$ConnectionString = "Host=localhost;Database=whizbang;Username=dev;Password=dev"
)

docker run --rm `
    --network host `
    -v "${PWD}/migrations:/migrations" `
    flyway/flyway:latest `
    -url="jdbc:postgresql://localhost:5432/whizbang" `
    -user=dev `
    -password=dev `
    migrate
```

**2. Code Quality Tools:**
```pwsh
#!/usr/bin/env pwsh
# Run markdown linter using container

docker run --rm `
    -v "${PWD}:/workdir" `
    markdownlint/markdownlint:latest `
    /workdir/**/*.md
```

**3. Build Tools:**
```pwsh
#!/usr/bin/env pwsh
# Build documentation using container

docker run --rm `
    -v "${PWD}:/docs" `
    -w /docs `
    node:20-alpine `
    sh -c "npm install && npm run build"
```

---

### When NOT to Use Containers

❌ **Don't containerize:**
- .NET SDK operations (`dotnet build`, `dotnet test`)
  - Reason: Already cross-platform, version managed by `global.json`
- PowerShell scripts
  - Reason: PowerShell Core is cross-platform
- Git operations
  - Reason: Git is ubiquitous

✅ **DO containerize:**
- Database migrations (Flyway, Liquibase)
- Documentation generators
- Linters/formatters (specific versions)
- Third-party tools without easy install

---

## Script Organization

### Directory Structure

```
scripts/
├── README.md               # Documents all scripts
├── testing/
│   ├── run-all-tests.ps1
│   └── run-tests-summary.ps1
├── coverage/
│   ├── collect-coverage.ps1
│   ├── merge-coverage.ps1
│   ├── show-coverage-report.ps1
│   └── run-all-tests-with-coverage.ps1
├── benchmarks/
│   └── run-benchmarks.ps1
├── diagnostics/
│   └── show-diagnostics.ps1
├── maintenance/
│   ├── clean-generator-locks.ps1
│   └── remove-coverage-from-history.sh
└── containers/
    ├── run-migrations.ps1
    └── validate-markdown.ps1
```

---

### Naming Conventions

**File Names:**
- Use kebab-case: `run-all-tests.ps1`
- Verb-Noun pattern: `collect-coverage.ps1`, `show-diagnostics.ps1`
- Descriptive, not cryptic: `run-tests.ps1` not `test.ps1`

**Common Verbs:**
- `run-*` - Execute operation (run-tests, run-benchmarks)
- `collect-*` - Gather data (collect-coverage)
- `show-*` - Display information (show-diagnostics)
- `clean-*` - Remove/cleanup (clean-binaries)
- `merge-*` - Combine data (merge-coverage)
- `validate-*` - Check correctness (validate-config)

---

## Script Documentation

### README.md Template

```markdown
# Scripts

This directory contains utility scripts for development, testing, and maintenance.

## Testing

### run-all-tests.ps1
Runs all test projects in the solution.

**Usage:**
```pwsh
./testing/run-all-tests.ps1
./testing/run-all-tests.ps1 -Configuration Release
```

**Parameters:**
- `-Configuration` - Build configuration (Debug or Release). Default: Debug

---

### run-tests-summary.ps1
Runs tests and displays a summary of results.

**Usage:**
```pwsh
./testing/run-tests-summary.ps1
```

---

## Coverage

### collect-coverage.ps1
Collects code coverage for a specific test project.

**Usage:**
```pwsh
./coverage/collect-coverage.ps1 -ProjectPath tests/Whizbang.Core.Tests
```

**Parameters:**
- `-ProjectPath` (required) - Path to test project
- `-Output` - Output path for coverage file. Default: ./coverage

---

## Benchmarks

### run-benchmarks.ps1
Runs performance benchmarks using BenchmarkDotNet.

**Usage:**
```pwsh
./benchmarks/run-benchmarks.ps1
./benchmarks/run-benchmarks.ps1 -Filter "*TracingBenchmarks*"
```

**Parameters:**
- `-Filter` - Filter benchmarks by name pattern
```

---

## Common Patterns

### Pattern 1: Find and Process Projects

```pwsh
# Find all test projects
$testProjects = Get-ChildItem -Path tests -Filter "*.csproj" -Recurse

foreach ($project in $testProjects) {
    Write-Host "Processing: $($project.Directory.Name)" -ForegroundColor Yellow

    Push-Location $project.Directory

    try {
        dotnet run -- --coverage
    }
    finally {
        Pop-Location
    }
}
```

---

### Pattern 2: Conditional Execution

```pwsh
param(
    [switch]$SkipBuild,
    [switch]$Verbose
)

if (-not $SkipBuild) {
    Write-Host "Building solution..." -ForegroundColor Cyan
    dotnet build -c Release
}

if ($Verbose) {
    dotnet test --verbosity detailed
} else {
    dotnet test --verbosity quiet
}
```

---

### Pattern 3: Error Aggregation

```pwsh
$errors = @()

foreach ($project in $projects) {
    try {
        dotnet test $project
        if ($LASTEXITCODE -ne 0) {
            $errors += "Tests failed in $project"
        }
    }
    catch {
        $errors += "Exception in ${project}: $_"
    }
}

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Error "Encountered $($errors.Count) error(s):"
    $errors | ForEach-Object { Write-Error "  - $_" }
    exit 1
}
```

---

### Pattern 4: Progress Reporting

```pwsh
$total = $projects.Count
$current = 0

foreach ($project in $projects) {
    $current++
    $percent = [math]::Round(($current / $total) * 100)

    Write-Progress `
        -Activity "Running tests" `
        -Status "$current of $total" `
        -PercentComplete $percent `
        -CurrentOperation $project.Name

    # Do work
    dotnet test $project
}

Write-Progress -Activity "Running tests" -Completed
```

---

## Quick Reference

### PowerShell Essentials

```pwsh
# Parameters with validation
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Required,

    [Parameter()]
    [ValidateSet('Option1', 'Option2')]
    [string]$Choice = 'Option1',

    [switch]$Flag
)

# Error handling
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    # Work
    dotnet build
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed"
    }
}
catch {
    Write-Error $_
    exit 1
}

# Cross-platform paths
$path = Join-Path $PSScriptRoot "coverage" "results.xml"

# File operations
if (Test-Path $path) { Remove-Item $path -Force }
New-Item -ItemType Directory -Path $dir -Force | Out-Null

# Colored output
Write-Host "Info" -ForegroundColor Cyan
Write-Host "Success" -ForegroundColor Green
Write-Warning "Warning"
Write-Error "Error"
```

---

### Container Essentials

```pwsh
# Run containerized tool
docker run --rm `
    -v "${PWD}:/workspace" `
    -w /workspace `
    tool-image:version `
    command args

# Interactive container
docker run -it --rm `
    -v "${PWD}:/workspace" `
    tool-image:version `
    /bin/bash
```

---

## See Also

- [Boy Scout Rule](boy-scout-rule.md) - Keep scripts clean and organized
- [Code Standards](code-standards.md) - Overall code quality standards
