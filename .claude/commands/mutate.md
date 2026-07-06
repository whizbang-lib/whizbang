---
argument-hint: Core | Data.Schema | Generators
---

Run Stryker.NET mutation testing on a specific project.

If an argument is provided, use it as the project name:
```bash
pwsh scripts/mutation/run-mutation-tests.ps1 -Project "Whizbang.$ARGUMENTS"
```

If no argument provided, default to Whizbang.Core:
```bash
pwsh scripts/mutation/run-mutation-tests.ps1
```

To target a specific file (useful for faster feedback):
```bash
pwsh scripts/mutation/run-mutation-tests.ps1 -Mutate "**/FileName.cs"
```

Options:
- `Core` - Whizbang.Core (default, most critical)
- `Data.Schema` - Whizbang.Data.Schema
- `Generators` - Whizbang.Generators

This will:
- Run Stryker.NET mutation testing on the specified project
- Create mutations (small bugs) and check if tests catch them
- Generate HTML report in StrykerOutput/ directory
- Show mutation score summary (killed vs survived)

Goal: Identify untested code paths where bugs could hide undetected.
