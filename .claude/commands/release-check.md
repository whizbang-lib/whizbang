Check if the codebase is ready for release.

Perform the following checks:

1. **Format code**:
   ```bash
   dotnet format
   ```
   - No changes should be needed
   - If changes occur, commit them first

2. **Clean build**:
   ```bash
   dotnet clean && dotnet build
   ```
   - Must complete with zero errors
   - Zero warnings in library code

3. **Run all tests**:
   ```bash
   dotnet test
   ```
   - All tests must pass
   - Zero failures

4. **Check coverage**:
   ```bash
   pwsh scripts/coverage/run-all-tests-with-coverage.ps1
   ```
   - Work toward 100% branch coverage
   - No critical gaps in coverage

5. **Verify async naming**:
   - All async methods end with "Async"
   - Including test methods

6. **Check XML documentation**:
   - All public APIs have XML docs
   - Documentation is accurate and helpful

7. **Boy Scout Rule applied**:
   - Code is better than when you started
   - No "that was pre-existing" excuses

If all checks pass, the code is ready for commit/release.
