# Plan: Fix IDE1006 Naming Violations for v0.1.0-alpha Release

## Current Status

- **Total IDE1006 Violations**: 732 errors (as of 2025-12-22)
- **Blocking**: `EnforceCodeStyleInBuild` is currently **ENABLED** in `Directory.Build.props` (line 11)
- **Impact**: Build fails with naming rule violations, blocking release preparation

## Root Cause

IDE1006 naming rule violations fall into three categories:
1. **Missing "Async" suffix** on async methods (~184 violations)
2. **Missing "_" prefix** on private fields (~200+ violations)
3. **PascalCase violations** - constants/fields using incorrect casing (~108 in schema files, ~240 elsewhere)

## Solution: Use Roslynator CLI with .NET SDK 9

### Why Roslynator?

- `dotnet format` and `dotnet format analyzers` **CANNOT** fix IDE1006 violations
- These tools only handle whitespace/formatting, not symbol renaming
- Roslynator 0.12.0 supports .NET SDK 7, 8, and 9 (NOT 10 yet)
- Roslynator's `rename-symbol` command can perform cascading renames across the solution

### Critical Requirements

1. **Must use .NET SDK 9** for Roslynator commands
   - Current default SDK: `dotnet --version` → `9.0.308` ✅
   - SDK 10 causes `MissingFieldException: 'Microsoft.Build.Shared.MSBuildConstants.InvalidPathChars'`

2. **Must use temporary .sln file** (not .slnx)
   - File: `/Users/philcarbone/src/whizbang/Whizbang.Temp.sln` (already created)
   - Contains all 47 projects from Whizbang.slnx

3. **Roslynator must be installed globally**
   ```bash
   dotnet tool list --global | grep roslynator
   # Should show: roslynator.dotnet.cli  0.12.0
   ```

## Step-by-Step Fix Plan

### Phase 1: Fix Missing "Async" Suffix (~184 violations)

```bash
export PATH="$PATH:/Users/philcarbone/.dotnet/tools"

roslynator rename-symbol ./Whizbang.Temp.sln \
  --msbuild-path "/usr/local/share/dotnet/sdk/9.0.308" \
  --match "symbol is Microsoft.CodeAnalysis.IMethodSymbol m && m.IsAsync && !m.Name.EndsWith(\"Async\")" \
  --new-name "symbol.Name + \"Async\"" \
  --file-log /tmp/rename-async-methods.log \
  --verbosity normal
```

**Expected outcome**: Methods like `SingleSequence` → `SingleSequenceAsync`

### Phase 2: Fix Missing "_" Prefix on Private Fields (~200+ violations)

```bash
export PATH="$PATH:/Users/philcarbone/.dotnet/tools"

roslynator rename-symbol ./Whizbang.Temp.sln \
  --msbuild-path "/usr/local/share/dotnet/sdk/9.0.308" \
  --match "symbol is Microsoft.CodeAnalysis.IFieldSymbol f && f.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Private && !f.Name.StartsWith(\"_\")" \
  --new-name "\"_\" + symbol.Name" \
  --file-log /tmp/rename-private-fields.log \
  --verbosity normal
```

**Expected outcome**: Private fields like `fixture` → `_fixture`

### Phase 3: Fix PascalCase Violations in Constants (~348 violations)

These are trickier - many are in benchmarks and schema files:

**Benchmark parameters** (e.g., `StreamCount`, `PartitionCount`):
```bash
export PATH="$PATH:/Users/philcarbone/.dotnet/tools"

roslynator rename-symbol ./Whizbang.Temp.sln \
  --msbuild-path "/usr/local/share/dotnet/sdk/9.0.308" \
  --match "symbol is Microsoft.CodeAnalysis.IFieldSymbol f && f.IsConst && f.DeclaringType.Name.Contains(\"Benchmark\") && (symbol.Name.Contains(\"Count\") || symbol.Name.Contains(\"Key\") || symbol.Name.Contains(\"Stream\"))" \
  --new-name "symbol.Name.ToUpper()" \
  --file-log /tmp/rename-benchmark-constants.log \
  --verbosity normal
```

**Expected outcome**: `StreamCount` → `STREAMCOUNT`, `StreamKey` → `STREAMKEY`

**Schema properties** - These may need manual review as they map to database columns.

### Phase 4: Verify All Violations Fixed

```bash
# Verify with dotnet format
dotnet format ./Whizbang.slnx --verify-no-changes 2>&1 | grep -c "error IDE1006"
# Expected: 0
```

### Phase 5: Run Full Build with Enforcement Enabled

```bash
# Ensure EnforceCodeStyleInBuild is enabled in Directory.Build.props
dotnet build Whizbang.slnx --no-restore 2>&1 | grep "IDE1006"
# Expected: No IDE1006 errors
```

## Verification Checklist

- [ ] Roslynator Phase 1 complete (async suffix)
- [ ] Roslynator Phase 2 complete (private field prefix)
- [ ] Roslynator Phase 3 complete (PascalCase constants)
- [ ] `dotnet format --verify-no-changes` shows 0 IDE1006 errors
- [ ] `dotnet build` succeeds with `EnforceCodeStyleInBuild=true`
- [ ] Tests still pass: `dotnet test --max-parallel-test-modules 8`
- [ ] Run `dotnet format` to ensure final formatting

## Known Issues & Gotchas

### Issue 1: Roslynator May Create Duplicate Names

**Problem**: Renaming `ProcessResults` → `ProcessResultsAsync` may conflict if `ProcessResultsAsync` already exists.

**Solution**: Use file path filtering in `--match` expression:
```bash
--match 'symbol is Microsoft.CodeAnalysis.IMethodSymbol m && m.Name == "ProcessResults" && m.DeclaringSyntaxReferences.Any(r => r.SyntaxTree.FilePath.EndsWith("SpecificFile.cs"))'
```

### Issue 2: Benchmark Constants May Break BenchmarkDotNet

**Problem**: Benchmark parameters might have specific naming requirements.

**Solution**: Verify benchmarks still run after renaming, or exclude benchmark projects from naming enforcement.

### Issue 3: Schema Properties Map to Database Columns

**Problem**: Renaming schema properties may break database queries if not using generated code.

**Solution**: Review schema changes carefully. These properties often intentionally match database column naming conventions (snake_case).

### Issue 4: Roslynator Doesn't Save on First Run

**Problem**: Sometimes Roslynator requires multiple runs to fix all violations.

**Solution**: Run each phase, then verify with `dotnet format --verify-no-changes`, and repeat if necessary.

## Cleanup After Completion

```bash
# Remove temporary solution file
rm /Users/philcarbone/src/whizbang/Whizbang.Temp.sln

# Remove Roslynator logs
rm /tmp/rename-*.log

# Verify final state
dotnet format ./Whizbang.slnx --verify-no-changes
dotnet build Whizbang.slnx
```

## Fallback: If Roslynator Continues to Fail

If Roslynator cannot fix all violations:

1. **Temporarily disable enforcement**:
   ```xml
   <!-- Directory.Build.props -->
   <!-- <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild> -->
   ```

2. **Create GitHub issue** for tracking manual fixes

3. **Proceed with other release tasks** (TUnit warnings, test failures, etc.)

4. **Re-enable enforcement** before final v0.1.0-alpha release

## Next Steps After Naming Violations

Once IDE1006 violations are resolved:

1. **Phase 1**: Fix 440 obsolete TUnit assertion warnings (`.HasCount()`)
2. **Phase 2**: Fix Aspire.Hosting.Testing version mismatch (NU1603)
3. **Phase 3**: Fix integration test failures
4. **Phase 4**: Code cleanup
5. **Phase 5**: Update version to 0.1.0-alpha
6. **Phase 6**: Final validation

## Reference Commands

```bash
# Check current IDE1006 violation count
dotnet format ./Whizbang.slnx --verify-no-changes 2>&1 | grep -c "error IDE1006"

# Show first 20 violations for debugging
dotnet format ./Whizbang.slnx --verify-no-changes 2>&1 | grep "IDE1006" | head -20

# Verify SDK version
dotnet --version

# Check Roslynator installation
export PATH="$PATH:/Users/philcarbone/.dotnet/tools"
roslynator --version

# Test build with enforcement
dotnet build Whizbang.slnx --no-restore 2>&1 | grep "IDE1006"
```

---

**Created**: 2025-12-22
**Status**: Ready for execution
**Estimated Time**: 15-30 minutes (automated), 1-2 hours if manual fixes needed
