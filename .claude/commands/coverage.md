Run all tests with code coverage collection.

Execute the coverage script:
```bash
pwsh scripts/coverage/run-all-tests-with-coverage.ps1
```

This will:
- Run all test projects in the solution
- Collect coverage using Microsoft.Testing.Extensions.CodeCoverage
- Generate Cobertura XML reports in `tests/*/TestResults/coverage.xml`
- Display coverage summary

Goal: Work toward 100% branch coverage.
