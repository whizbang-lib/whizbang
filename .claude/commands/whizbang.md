---
argument-hint: test | coverage | format | build | release-check | diagnostics | benchmarks | context-tdd | context-testing | context-generators | context-standards
---

# Whizbang Command Menu

Welcome to the Whizbang command center! You can run `/whizbang <command>` or just `/whizbang` to see this menu.

## Available Commands

### Testing & Validation
- **test** - Run all tests in the solution
- **coverage** - Run tests with code coverage collection
- **format** - Format code with `dotnet format` (MANDATORY before commit)
- **build** - Clean and rebuild the entire solution
- **release-check** - Full release readiness checklist

### Diagnostics & Performance
- **diagnostics** - Run diagnostic checks on the solution
- **benchmarks** - Run performance benchmarks

### Context Loading (Documentation)
- **context-tdd** - Load TDD (Test-Driven Development) documentation
- **context-testing** - Load testing framework documentation (TUnit, Rocks, Bogus)
- **context-generators** - Load source generator documentation
- **context-standards** - Load code standards and conventions

---

## Usage Examples

```bash
# Run specific command
/whizbang test
/whizbang coverage
/whizbang context-tdd

# Or use the short form directly
/test
/coverage
/context-tdd
```

## Quick Reference

**Most Common Workflows:**

1. **Starting new feature**:
   ```
   /whizbang context-tdd
   /whizbang test
   ```

2. **Before committing**:
   ```
   /whizbang format
   /whizbang test
   ```

3. **Pre-release checklist**:
   ```
   /whizbang release-check
   ```

4. **Checking code quality**:
   ```
   /whizbang coverage
   /whizbang diagnostics
   ```

---

## Command Details

### /whizbang test
Runs all tests in the solution. Execute: `dotnet test`

### /whizbang coverage
Runs all test projects with coverage collection using the PowerShell script.
Goal: Work toward 100% branch coverage.

### /whizbang format
Formats code using `dotnet format`. ALWAYS run before claiming work complete.

### /whizbang build
Clean rebuild: `dotnet clean && dotnet build`
Use when starting fresh or troubleshooting build issues.

### /whizbang release-check
Comprehensive release readiness checklist:
1. Format code
2. Clean build
3. Run all tests
4. Check coverage
5. Verify async naming conventions
6. Check XML documentation
7. Apply Boy Scout Rule

### /whizbang diagnostics
Run diagnostic checks to identify potential issues in the codebase.

### /whizbang benchmarks
Execute performance benchmarks to measure and track performance metrics.

### /whizbang context-tdd
Load TDD workflow documentation:
- RED/GREEN/REFACTOR cycle
- Test-first development
- Boy Scout Rule

### /whizbang context-testing
Load testing framework documentation:
- TUnit CLI usage
- Rocks mocking framework
- Bogus test data generation

### /whizbang context-generators
Load source generator documentation:
- Performance principles
- Common pitfalls
- Testing strategies

### /whizbang context-standards
Load code standards and conventions:
- Formatting rules
- Naming conventions
- Documentation requirements

---

## Implementation

If an argument is provided, execute the corresponding command.
If no argument is provided, show this menu.

Execute based on $1 argument:

$1
