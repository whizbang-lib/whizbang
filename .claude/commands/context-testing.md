Load testing context and TUnit documentation.

Read these files to understand testing practices:
- ai-docs/testing-tunit.md - TUnit framework, Rocks, Bogus
- ai-docs/tdd-strict.md - TDD workflow
- TESTING.md - Testing infrastructure overview

Critical TUnit differences:
- Use `dotnet run` NOT `dotnet test` for coverage
- Use `--treenode-filter` NOT `--filter` for filtering tests
- ALL async tests MUST end with "Async" suffix

Common commands:
```bash
# Run all tests
dotnet test

# Run specific test class
cd tests/Whizbang.Core.Tests
dotnet run -- --treenode-filter "/*/*/MyTestClass/*"

# Run with coverage
dotnet run -- --coverage --coverage-output-format cobertura
```

Use this command when:
- Writing tests
- Debugging test failures
- Setting up test infrastructure
