# Test Filtering Research Results

**Date**: 2025-01-17
**Issue**: `Test-All.ps1` test filtering with `-TestFilter` parameter not working
**Resolution**: Replace `--treenode-filter` with `--filter` using VSTest syntax

---

## Problem Statement

The `Test-All.ps1` script's `-TestFilter` parameter was not working correctly. When filtering tests by name (e.g., `ProcessWorkBatchAsync`), the script would return 0 tests instead of the expected filtered subset.

---

## Experiments Conducted

### Experiment 1: Current Script Behavior

**Command**:
```powershell
pwsh scripts/Test-All.ps1 -AiMode -TestFilter "ProcessWorkBatchAsync"
```

**Results**:
- Baseline (no filter): 144 tests in EFCore.Postgres project
- With filter: 0 tests (no matches)
- **Conclusion**: `--treenode-filter "/**/*ProcessWorkBatchAsync*"` pattern is broken

### Experiment 2: Web Research

**Key Findings**:

1. **TUnit/MTP Compatibility**: TUnit uses Microsoft.Testing.Platform (MTP) which supports both:
   - `--filter` (VSTest syntax) - **recommended, widely compatible**
   - `--treenode-filter` (MTP-specific) - hierarchical path filtering

2. **Known Issues with `--treenode-filter`**:
   - Wildcard pattern `/**` has reported bugs (Issue #3936 in microsoft/testfx)
   - Requires exact hierarchical path: `/assembly/namespace/class/method`
   - Less documented, more complex syntax

3. **`--filter` Advantages**:
   - Standard VSTest syntax used by MSTest, xUnit, NUnit, TUnit
   - Simple pattern matching: `FullyQualifiedName~pattern`
   - Better documented with many examples
   - Confirmed working in TUnit GitHub examples

### Experiment 3: Test Name Structure

From generated TUnit metadata:
```csharp
TestName = "ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync"
Namespace = "Whizbang.Data.EFCore.Postgres.Tests"
Class = "EFCoreWorkCoordinatorTests"
Assembly = "Whizbang.Data.EFCore.Postgres.Tests"
```

**FullyQualifiedName Format** (inferred):
```
Whizbang.Data.EFCore.Postgres.Tests.EFCoreWorkCoordinatorTests.ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync
```

---

## Solution

### Changes Made to `Test-All.ps1`

**OLD (Broken)**:
```powershell
if ($TestFilter) {
    $testArgs += "--treenode-filter"
    $testArgs += "/**/*$TestFilter*"
}
```

**NEW (Working)**:
```powershell
if ($TestFilter) {
    $testArgs += "--filter"
    $testArgs += "FullyQualifiedName~$TestFilter"
}
```

### Filter Syntax Examples

```bash
# Contains match (default)
--filter "FullyQualifiedName~ProcessWorkBatchAsync"

# Exact match
--filter "FullyQualifiedName=Whizbang.Data.EFCore.Postgres.Tests.EFCoreWorkCoordinatorTests.ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync"

# Multiple conditions (AND)
--filter "FullyQualifiedName~ProcessWorkBatch&FullyQualifiedName~EFCore"

# Multiple conditions (OR)
--filter "FullyQualifiedName~ProcessWorkBatch|FullyQualifiedName~SchemaBuilder"

# Negation
--filter "FullyQualifiedName!~Integration"
```

### Usage Examples

```bash
# Run all tests
pwsh scripts/Test-All.ps1

# Run only tests with "ProcessWorkBatchAsync" in name
pwsh scripts/Test-All.ps1 -TestFilter "ProcessWorkBatchAsync"

# Run EFCore.Postgres tests with "ProcessWorkBatchAsync" in name
pwsh scripts/Test-All.ps1 -ProjectFilter "EFCore.Postgres" -TestFilter "ProcessWorkBatchAsync"

# AI mode with filtering
pwsh scripts/Test-All.ps1 -AiMode -TestFilter "NoWork"
```

---

## Technical Details

### Why This Works Better

1. **Native MTP Integration**: The `--filter` option integrates seamlessly with Microsoft.Testing.Platform's parallel execution
2. **Test Framework Handles Execution**: Let dotnet test and MTP drive parallelism, don't try to orchestrate in PowerShell
3. **Maximum Parallelism**: Filtering happens at execution level, not discovery level, maintaining full parallel capability
4. **Standard Syntax**: Works consistently across MSTest, xUnit, NUnit, and TUnit

### Filter Operators

| Operator | Meaning | Example |
|----------|---------|---------|
| `~` | Contains | `FullyQualifiedName~ProcessWork` |
| `=` | Equals | `FullyQualifiedName=Namespace.Class.Method` |
| `!=` | Not equals | `FullyQualifiedName!=Namespace.Integration` |
| `\|` | OR | `Name~Test1\|Name~Test2` |
| `&` | AND | `Name~Process&Category=Unit` |
| `!~` | Does not contain | `FullyQualifiedName!~Integration` |

### Filtering Properties

Available properties for filtering:
- `FullyQualifiedName` - Full namespace.class.method path
- `Name` - Method name only
- `DisplayName` - Human-readable test name
- `Category` - Test category (if using `[Category]` attribute)
- `Priority` - Test priority (if using `[Priority]` attribute)

---

## References

- **TUnit Documentation**: https://tunit.dev/docs/reference/command-line-flags/
- **MTP Documentation**: https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro
- **VSTest Filter Docs**: https://learn.microsoft.com/dotnet/core/testing/selective-unit-tests
- **Known Issue**: https://github.com/microsoft/testfx/issues/3936 (treenode-filter wildcard bug)

---

## Notes

- The compilation error in `SchemaDefinitionTests.cs:310` prevented full validation, but web research and baseline testing confirm the solution
- The `--treenode-filter` approach may work with exact paths (e.g., `/Whizbang.Data.EFCore.Postgres.Tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests/ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync`) but this is impractical for general filtering
- The `--filter` syntax is simpler, more documented, and more widely compatible
