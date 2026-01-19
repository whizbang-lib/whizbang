Load TDD (Test-Driven Development) context and documentation.

Read these files to understand TDD workflow:
- ai-docs/tdd-strict.md - RED/GREEN/REFACTOR cycle (MANDATORY)
- ai-docs/testing-tunit.md - TUnit CLI usage, Rocks mocking, Bogus
- ai-docs/boy-scout-rule.md - Leave code better than you found it

Key principles:
1. **RED**: Write failing test first
2. **GREEN**: Write minimal code to make it pass
3. **REFACTOR**: Clean up code, run `dotnet format`, apply Boy Scout Rule

**Test-First Rule**: If you write implementation before tests, you're doing it wrong.

Use this command when:
- Starting new feature development
- Fixing bugs
- Need reminder of TDD workflow
