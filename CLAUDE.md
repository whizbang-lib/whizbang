# CLAUDE.md - Whizbang Library Repository

> **Quick Reference**: Essential standards and navigation to focused ai-docs. This is a .NET library implementing event-driven, CQRS, and event-sourced patterns with zero reflection and AOT compatibility.

---

## Essential Standards

### Code Style

**ALWAYS run `dotnet format` after code changes** - Non-negotiable. Format before claiming completion.

**Async Naming Convention**:
```csharp
// ✅ CORRECT
public async Task ProcessMessageAsync(Message msg) { }
public async Task<Result> ExecuteAsync() { }

[Test]
public async Task MessageEnvelope_AddHop_AddsHopToListAsync() { }

// ❌ WRONG
public async Task ProcessMessage(Message msg) { }

[Test]
public async Task MessageEnvelope_AddHop_AddsHopToList() { }
```

**ALL async methods and test methods must end with "Async" suffix.**

---

## Common Commands

```bash
# Build
dotnet clean && dotnet build

# Run tests (parallel execution)
dotnet test --max-parallel-test-modules 8
pwsh scripts/Run-Tests.ps1                    # Convenient wrapper (default: Ai mode, excludes integration)
pwsh scripts/Run-Tests.ps1 -Mode Ci           # Full output for CI
pwsh scripts/Run-Tests.ps1 -Mode Full         # Include integration tests (slow!)
pwsh scripts/Run-Tests.ps1 -Mode AiIntegrations  # Only integration tests, AI output
pwsh scripts/Run-Tests.ps1 -ProjectFilter "EFCore.Postgres"
pwsh scripts/Run-Tests.ps1 -TestFilter "ProcessWorkBatchAsync"

# Run specific test project
cd tests/Whizbang.Core.Tests && dotnet run

# Run specific test using treenode-filter
# Syntax: /<Assembly>/<Namespace>/<ClassName>/<TestMethodName>
cd tests/Whizbang.Core.Tests && dotnet run -- --treenode-filter "/Whizbang.Core.Tests/Whizbang.Core.Tests/DispatcherTests/Dispatch_SendsMessageToCorrectReceptorAsync"

# Filter by wildcards (all tests in a class)
dotnet run -- --treenode-filter "/Whizbang.Core.Tests/Whizbang.Core.Tests/DispatcherTests/*"

# Filter by category attribute
dotnet run -- --treenode-filter "/*/*/*/*[Category=Integration]"

# Format code (MANDATORY before commit)
dotnet format

# Full cycle
dotnet clean && dotnet build && dotnet test --max-parallel-test-modules 8 && dotnet format
```

📖 **Read**: `docs/TEST-FILTERING.md` for test filtering examples and syntax

---

## Project Structure

```
src/
├── Whizbang.Core/              # Core types, interfaces, observability
├── Whizbang.Generators/        # Source generators (Roslyn)
└── Whizbang.Testing/           # Testing utilities (future)

tests/
├── Whizbang.Core.Tests/        # Unit tests
├── Whizbang.Policies.Tests/    # Policy system tests
└── Whizbang.Observability.Tests/

plans/                          # Feature planning documents
ai-docs/                        # Focused topic documentation (11 files)
.claude/commands/               # Slash commands (11 commands)
```

---

## Technology Stack

- **.NET 10.0.1** (LTS) - Target framework
- **TUnit 1.2.11+** - Source-generated testing with Microsoft.Testing.Platform
- **TUnit.Assertions** - Native fluent assertions
- **Rocks 9.3.0+** - Source-generated mocking for AOT compatibility
- **Vogen** - Source-generated value objects
- **Microsoft.Testing.Platform 2.0+** - Native test runner

**Important**: .NET 10 fully supports `dotnet test` with parallel execution. Use `--max-parallel-test-modules` for concurrent test project execution.

---

## When to Read ai-docs/

### 📖 **[README.md](ai-docs/README.md)** - Start here
Navigation hub with scenario-based guidance. **Read this first** when unsure which doc to read.

### 📖 **[tdd-strict.md](ai-docs/tdd-strict.md)** - MANDATORY
**Read when**:
- Starting new feature implementation
- Writing tests for the first time
- Need RED/GREEN/REFACTOR cycle guidance

### 📖 **[testing-tunit.md](ai-docs/testing-tunit.md)** - CRITICAL
**Read when**:
- Writing or debugging tests
- Using TUnit CLI, Rocks mocking, or Bogus
- Getting test failures or unexpected behavior
- Need assertion patterns

**Why critical**: Prevents common TUnit and Rocks mistakes that waste hours.

### 📖 **[efcore-10-usage.md](ai-docs/efcore-10-usage.md)**
**Read when**:
- Working with EF Core 10
- Using PostgreSQL JsonB or UUIDv7
- Implementing DbContext or migrations
- Working with complex types or owned entities

### 📖 **[aot-requirements.md](ai-docs/aot-requirements.md)**
**Read when**:
- Working on Whizbang.Core (zero reflection required)
- Working on Whizbang.Generators (Roslyn, reflection allowed)
- Getting AOT warnings or errors
- Adding JSON serialization

**Key point**: Different rules for different projects - Core = zero reflection, Generators = reflection allowed.

### 📖 **[documentation-maintenance.md](ai-docs/documentation-maintenance.md)** - CRITICAL
**Read when**:
- Changing ANY public API in Core, Generators, or Testing projects
- Adding, renaming, or removing public types/methods
- Before making documentation changes

**Why critical**: Ensures docs stay synchronized with code. ALWAYS ask "What version are you working on?" before API changes.

### 📖 **[boy-scout-rule.md](ai-docs/boy-scout-rule.md)**
**Read when**:
- Before claiming completion
- Touching existing code
- Seeing small issues nearby

**Philosophy**: Leave code better than you found it.

### 📖 **[code-standards.md](ai-docs/code-standards.md)**
**Read when**:
- Unsure about formatting or naming
- Need XML documentation guidance
- Getting dotnet format warnings

### 📖 **[sample-projects.md](ai-docs/sample-projects.md)**
**Read when**:
- Working on sample applications
- Need dogfooding guidance
- Creating example code

### 📖 **[script-standards.md](ai-docs/script-standards.md)**
**Read when**:
- Writing or modifying PowerShell scripts
- Creating build automation
- Need PowerShell Core patterns

### 📖 **[efcore-aot-support.md](ai-docs/efcore-aot-support.md)**
**Read when**:
- Implementing EF Core with AOT
- Need compiled model guidance
- Working on NativeAOT compatibility

---

## When to Read Source Generator Docs

**Location**: `src/Whizbang.Generators/ai-docs/` (11 focused files)

### 📖 **[README.md](src/Whizbang.Generators/ai-docs/README.md)**
Start here for generator navigation and scenario-based guidance.

### 📖 **[quick-reference.md](src/Whizbang.Generators/ai-docs/quick-reference.md)**
**Read when**:
- Starting new source generator
- Need complete working example
- Forgot generator structure

### 📖 **[performance-principles.md](src/Whizbang.Generators/ai-docs/performance-principles.md)** - CRITICAL
**Read when**:
- Writing or optimizing source generators
- Getting slow build times
- Need syntactic filtering patterns

**Why critical**: 50-200ms difference from following best practices (sealed records, value types, syntactic filtering).

### 📖 **[value-type-records.md](src/Whizbang.Generators/ai-docs/value-type-records.md)**
**Read when**: Need to understand why generator data models use `sealed record` pattern.

### 📖 **[common-pitfalls.md](src/Whizbang.Generators/ai-docs/common-pitfalls.md)**
**Read when**:
- Debugging generator issues
- Generator not producing output
- Getting compilation errors from generated code

**Covers**: Seven major mistakes to avoid.

📖 **Refer to**: `src/Whizbang.Generators/ai-docs/README.md` for complete generator documentation list.

---

## Slash Commands (`.claude/commands/`)

Quick workflows via `/command-name`:

**Testing**:
- `/test` - Run all tests
- `/coverage` - Run tests with coverage

**Code Quality**:
- `/format` - Run dotnet format (MANDATORY before commit)
- `/release-check` - Full release checklist

**Context Loading**:
- `/context-tdd` - Load TDD documentation
- `/context-generators` - Load generator documentation
- `/context-testing` - Load testing documentation
- `/context-efcore` - Load EF Core documentation
- `/context-aot` - Load AOT requirements
- `/context-boy-scout` - Load boy scout rule
- `/context-scripts` - Load script standards

---

## Code-Docs Linking

**All public APIs** should have `<docs>` XML tags linking to documentation:

```csharp
/// <summary>
/// Dispatches messages to appropriate handlers
/// </summary>
/// <docs>core-concepts/dispatcher</docs>
public interface IDispatcher {
  // ...
}
```

**Workflow**:
1. Add `<docs>` tags to public types in Core, Generators, Testing projects
2. Regenerate mapping: `cd ../whizbang-lib.github.io && node src/scripts/generate-code-docs-map.mjs`
3. Validate: `mcp__whizbang-docs__validate-doc-links()`

**MCP Tools**:
- `mcp__whizbang-docs__get-code-location` - Find code for a doc concept
- `mcp__whizbang-docs__get-related-docs` - Find docs for a symbol
- `mcp__whizbang-docs__validate-doc-links` - Validate all links

📖 **Read**:
- `ai-docs/documentation-maintenance.md` - Complete workflow with version awareness
- `../whizbang-lib.github.io/CONTRIBUTING.md#mcp-server-setup-optional-for-ai-assisted-development` - MCP setup instructions

---

## Code-Tests Linking

**Convention-based linking** between code and tests (e.g., `DispatcherTests` → `Dispatcher`).

**Optional `<tests>` tags** for complex cases:
```csharp
/// <tests>Whizbang.Core.Tests/DispatcherTests.cs:Dispatch_SendsMessageToCorrectReceptorAsync</tests>
public void Dispatch<TMessage>(TMessage message) where TMessage : IMessage {
  // ...
}
```

**Workflow**:
1. Follow naming convention: `ClassNameTests` tests `ClassName`
2. Regenerate mapping: `cd ../whizbang-lib.github.io && node src/scripts/generate-code-tests-map.mjs`
3. Query coverage: `mcp__whizbang-docs__get-coverage-stats()`

**MCP Tools**:
- `mcp__whizbang-docs__get-tests-for-code` - Find tests for code
- `mcp__whizbang-docs__get-code-for-test` - Find code tested by test
- `mcp__whizbang-docs__validate-test-links` - Validate test links
- `mcp__whizbang-docs__get-coverage-stats` - Get coverage statistics

**Note**: MCP tools require setup. See `../whizbang-lib.github.io/CONTRIBUTING.md#mcp-server-setup-optional-for-ai-assisted-development` for configuration instructions.

---

## Key Principles

1. **Zero Reflection** - Everything via source generators (except Generators project itself)
2. **AOT Compatible** - Native AOT from day one
3. **Type Safe** - Compile-time safety everywhere
4. **Test Driven** - Tests define behavior, implementation follows (RED/GREEN/REFACTOR)
5. **Documentation First** - Never implement before documenting

---

## Cross-Repository Context

**Workspace CLAUDE.md**: `../CLAUDE.md` - Navigation between repos
**Documentation Repo**: `../whizbang-lib.github.io/` - Living specifications
**VSCode Extension**: `../whizbang-vscode/` - IDE integration

📖 **Read workspace CLAUDE.md** when working across multiple repositories.

---

## Notes

- This is a **library**, not an application
- XML documentation required for all public APIs
- Add `<docs>` tags to public APIs for bidirectional navigation
- Update docs when changing public APIs (see ai-docs/documentation-maintenance.md)
- ai-docs/ contains focused, single-topic documentation
- Slash commands provide quick context loading
- This file is intentionally concise - detailed guidance lives in ai-docs/
