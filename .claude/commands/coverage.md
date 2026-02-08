Run all tests with code coverage collection.

Execute the test script with coverage enabled:
```bash
pwsh scripts/Run-Tests.ps1 -Coverage
```

Options:
- `-Mode Ai` - AI-optimized output, excludes integration tests (default)
- `-Mode Ci` - Full output, excludes integration tests
- `-Mode AiFull` - AI-optimized output, includes all tests
- `-Mode Full` - Full output, includes all tests
- `-Mode AiIntegrations` - AI-optimized output, only integration tests
- `-Mode IntegrationsOnly` - Full output, only integration tests
- `-ProjectFilter "Core"` - Run only matching projects

This will:
- Run all test projects in the solution
- Collect coverage using Microsoft.Testing.Extensions.CodeCoverage
- Generate Cobertura XML reports in `tests/*/TestResults/coverage.cobertura.xml`
- Use `codecoverage.runsettings` for CI-compatible coverage settings

Goal: Work toward 100% branch coverage.
