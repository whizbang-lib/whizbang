Run all tests with code coverage collection.

Execute the test script with coverage enabled:
```bash
pwsh scripts/Run-Tests.ps1 -Coverage
```

Options:
- `-Mode Ai` - AI-optimized output, ALL tests (default)
- `-Mode AiUnit` - AI-optimized output, unit tests only
- `-Mode AiIntegrations` - AI-optimized output, integration tests only
- `-Mode Unit` - Verbose output, unit tests only
- `-Mode Integration` - Verbose output, integration tests only
- `-ProjectFilter "Core"` - Run only matching projects

This will:
- Run all test projects in the solution
- Collect coverage using Microsoft.Testing.Extensions.CodeCoverage
- Generate Cobertura XML reports in `tests/*/TestResults/coverage.cobertura.xml`
- Use `codecoverage.runsettings` for CI-compatible coverage settings

Goal: Work toward 100% branch coverage.
