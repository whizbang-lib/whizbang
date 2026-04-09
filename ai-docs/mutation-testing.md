# Mutation Testing with Stryker.NET

## What Is Mutation Testing?

Code coverage tells you what code your tests **execute**. Mutation testing tells you what code your tests **actually verify**.

Stryker.NET systematically introduces small bugs (mutants) into your source code:
- Changing `>` to `>=`
- Replacing `true` with `false`
- Removing method calls or statements
- Flipping boolean conditions

If a test fails, the mutant is **killed** (good). If all tests pass despite the mutation, the mutant **survived** (weak test).

**Mutation score** = killed / (killed + survived). Higher is better.

## Running Locally

### Quick start (default: Whizbang.Core)
```bash
pwsh scripts/mutation/run-mutation-tests.ps1
```

### Target a specific file (faster feedback)
```bash
pwsh scripts/mutation/run-mutation-tests.ps1 -Mutate "**/MessageEnvelope.cs"
```

### Diff-based (only changed files)
```bash
pwsh scripts/mutation/run-mutation-tests.ps1 -Since main
```

### Slash command
```
/mutate Core
/mutate Data.Schema
```

## Reading Results

Stryker generates an HTML report in `StrykerOutput/`. The report shows:

- **Killed** - Tests detected the mutation (good)
- **Survived** - Tests missed the mutation (action needed)
- **No coverage** - No tests executed this code at all
- **Timeout** - Mutation caused an infinite loop (usually killed)
- **Compile error** - Mutation caused build failure (ignored)

### What to do with surviving mutants

1. **Missing assertion** - Test executes the code but doesn't assert the result. Add an assertion.
2. **Missing test case** - No test covers this scenario. Write a new test.
3. **Equivalent mutant** - The mutation doesn't actually change behavior (e.g., replacing `x * 1` with `x * -1` when x is always 0). These are false positives - ignore them.

## Configuration

Each mutatable project needs a `stryker-config.json` in its test project directory.

### Template
```json
{
  "$schema": "https://raw.githubusercontent.com/stryker-mutator/stryker-net/master/src/Stryker.Core/Stryker.Core/stryker-config.schema.json",
  "stryker-config": {
    "project": "YourProject.csproj",
    "test-projects": ["./YourProject.Tests.csproj"],
    "target-framework": "net10.0",
    "test-runner": "mtp",
    "reporters": ["html", "progress", "cleartext"],
    "verbosity": "info",
    "concurrency": 1,
    "coverage-analysis": "off",
    "thresholds": {
      "high": 80,
      "low": 60,
      "break": 0
    },
    "mutate": [
      "!**/.whizbang-generated/**",
      "!**/*.g.cs",
      "!**/obj/**",
      "!**/bin/**",
      "!**/Generated/**",
      "!**/AssemblyInfo.cs"
    ],
    "ignore-mutations": [
      "string",
      "linq"
    ]
  }
}
```

### Key settings
- **test-runner: mtp** - Required for TUnit (Microsoft.Testing.Platform)
- **coverage-analysis: off** - MTP coverage capture has compatibility issues; disable for reliability
- **ignore-mutations** - `string` and `linq` produce many low-value surviving mutants (log messages, exception text)
- **mutate exclusions** - Always exclude generated code (`.whizbang-generated/`, `*.g.cs`)

## Adding a New Project

1. Create `tests/YourProject.Tests/stryker-config.json` using the template above
2. Update the `project` and `test-projects` fields
3. Run: `pwsh scripts/mutation/run-mutation-tests.ps1 -Project YourProject`
4. Add the project to `.github/workflows/mutation.yml` choice list

## Performance Tips

- **Target specific files** with `-Mutate "**/FileName.cs"` for fast iteration (seconds vs minutes)
- **Use diff-based mode** with `-Since main` to only mutate changed files
- **Start with `break: 0`** (no CI gate) until you establish a baseline score
- Full Whizbang.Core run takes ~3 minutes for a single file, 30-60 minutes for everything

## Known Limitations

- **concurrency must be 1** - Stryker's MTP runner creates test server processes that interfere with each other when running concurrently. Multiple concurrent runners cause all mutants to report "not fully tested". This makes full-project runs slower but reliable.
- **coverage-analysis must be "off"** - Stryker's MTP coverage capture has a bug with TUnit. This means all tests run for every mutant (slower but reliable).
- **Library projects must not reference TUnit** - If a library (like `Whizbang.Testing`) references the full `TUnit` package, Stryker misidentifies it as a test project. Use `TUnit.Core` only for utility libraries.

## Relationship to Code Coverage

| Metric | Answers | Weakness |
|--------|---------|----------|
| Line coverage | "Did tests execute this code?" | Can be 100% with zero assertions |
| Branch coverage | "Did tests hit both branches?" | Doesn't verify correctness |
| Mutation score | "Would tests catch a bug here?" | Slower, more compute |

Use coverage to find **untested code**. Use mutation testing to find **undertested code**.
