# Test Filtering Guide

Quick reference for filtering tests using the `Run-Tests.ps1` script.

---

## Basic Usage

```bash
# Run all tests
pwsh scripts/Run-Tests.ps1

# Run all tests in AI mode (compact output)
pwsh scripts/Run-Tests.ps1 -AiMode
```

---

## Filtering Tests

### By Project Name

Filter to specific test projects using `-ProjectFilter`:

```bash
# Run only EFCore.Postgres tests
pwsh scripts/Run-Tests.ps1 -ProjectFilter "EFCore.Postgres"

# Run only Core tests
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Core"
```

**Pattern**: Matches test project names (case-insensitive substring match)

### By Test Name

Filter to specific tests using `-TestFilter`:

```bash
# Run only tests with "ProcessWorkBatchAsync" in the name
pwsh scripts/Run-Tests.ps1 -TestFilter "ProcessWorkBatchAsync"

# Run only tests with "NoWork" in the name
pwsh scripts/Run-Tests.ps1 -TestFilter "NoWork"
```

**Pattern**: Uses MTP `--treenode-filter` syntax: `/*/*/*/*pattern*` (matches any test method containing the pattern)

### Combine Filters

```bash
# Run ProcessWorkBatchAsync tests in EFCore.Postgres project only
pwsh scripts/Run-Tests.ps1 -ProjectFilter "EFCore.Postgres" -TestFilter "ProcessWorkBatchAsync"

# Run NoWork tests in Core project only
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Core" -TestFilter "NoWork"
```

---

## Advanced Filter Syntax

The `-TestFilter` parameter uses MTP tree node filtering with wildcards:

| Pattern | Meaning | Example |
|---------|---------|---------|
| `/*/*/*/*pattern*` | Match in method name | `-TestFilter "NoWork"` (script adds wildcards) |
| `/*/*/*/ExactMethod` | Exact method name | `-TestFilter "ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync"` |
| `/*/Namespace/*/*/*` | Filter by namespace | Requires manual treenode-filter syntax |

**Note**: The script automatically wraps your pattern in `/*/*/*/*{pattern}*` for convenience.

---

## Other Options

```bash
# Custom parallelism (default: CPU core count)
pwsh scripts/Run-Tests.ps1 -MaxParallel 4

# Verbose output
pwsh scripts/Run-Tests.ps1 -Verbose

# Combine options
pwsh scripts/Run-Tests.ps1 -ProjectFilter "EFCore" -TestFilter "NoWork" -AiMode
```

---

## How It Works

- **Project filtering** uses `--test-modules` with glob patterns
- **Test filtering** uses `--treenode-filter /*/*/*/*pattern*` (MTP tree filter syntax)
- **Maintains full parallelism** - tests run concurrently across multiple projects
- **Compatible with TUnit** via Microsoft.Testing.Platform (MTP)

**Note**: The `--filter FullyQualifiedName~pattern` syntax (VSTest) does NOT work with TUnit/MTP. Always use `--treenode-filter`.

---

## Examples

```bash
# Run all work coordinator tests
pwsh scripts/Run-Tests.ps1 -TestFilter "WorkCoordinator"

# Run all schema tests in Data.Schema project
pwsh scripts/Run-Tests.ps1 -ProjectFilter "Data.Schema"

# Run specific test across all projects
pwsh scripts/Run-Tests.ps1 -TestFilter "ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync"

# Run integration tests only
pwsh scripts/Run-Tests.ps1 -TestFilter "Integration"
```

---

## Troubleshooting

**Filter returns 0 tests:**
- Check spelling of filter pattern
- Use broader pattern (e.g., `"Work"` instead of `"WorkCoordinator"`)
- Remove filter to see all test names, then refine

**Tests run slowly:**
- Reduce `-MaxParallel` if system is overloaded
- Use `-AiMode` for cleaner output when debugging

**Need test names:**
- Run without filter and examine output
- Or use: `Get-Help scripts/Test-All.ps1 -Examples`
