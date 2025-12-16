# CLAUDE.md - Whizbang Library Repository

**Purpose**: Quick reference for AI coding standards and workflow in the Whizbang library.

---

## Table of Contents

1. [Essential Standards](#essential-standards)
2. [TDD Workflow](#tdd-workflow-strict)
3. [Testing Standards](#testing-standards)
4. [Project Structure](#project-structure)
5. [Common Commands](#common-commands)
6. [Code Coverage](#code-coverage)
7. [Technology Stack](#technology-stack)
8. [Key Principles](#key-principles)
9. [Code-Docs Linking](#code-docs-linking)
10. [Code-Tests Linking](#code-tests-linking)
11. [Documentation Maintenance](#documentation-maintenance)
12. [ID Generation](#id-generation)
13. [Observability Architecture](#observability-architecture)
14. [Work Coordination & Event Store](#work-coordination--event-store-architecture)
15. [Plan Documents](#plan-documents)

---

## Quick Navigation

### 📁 Core Documentation (`ai-docs/`)

For comprehensive guidance on specific topics, see focused documentation in `ai-docs/`:

- **[README.md](ai-docs/README.md)** - Navigation hub with scenario-based guidance
- **[testing-tunit.md](ai-docs/testing-tunit.md)** - TUnit CLI usage, Rocks mocking, Bogus (CRITICAL - read this to avoid common mistakes!)
- **[efcore-10-usage.md](ai-docs/efcore-10-usage.md)** - EF Core 10, JsonB, UUIDv7, complex types
- **[aot-requirements.md](ai-docs/aot-requirements.md)** - Zero reflection rules by project type
- **[tdd-strict.md](ai-docs/tdd-strict.md)** - RED/GREEN/REFACTOR cycle (MANDATORY)
- **[documentation-maintenance.md](ai-docs/documentation-maintenance.md)** - CRITICAL: Keeping docs synchronized with code changes across ALL projects
- **[boy-scout-rule.md](ai-docs/boy-scout-rule.md)** - Leave code better than you found it
- **[code-standards.md](ai-docs/code-standards.md)** - Formatting, naming, dotnet format
- **[sample-projects.md](ai-docs/sample-projects.md)** - Dogfooding philosophy
- **[script-standards.md](ai-docs/script-standards.md)** - PowerShell Core preferences

### 📁 Source Generators (`src/Whizbang.Generators/ai-docs/`)

For source generator development, see `src/Whizbang.Generators/ai-docs/`:

- **[README.md](src/Whizbang.Generators/ai-docs/README.md)** - Generator-specific navigation
- **[quick-reference.md](src/Whizbang.Generators/ai-docs/quick-reference.md)** - Complete working example
- **[performance-principles.md](src/Whizbang.Generators/ai-docs/performance-principles.md)** - CRITICAL: Syntactic filtering, value-type records
- **[value-type-records.md](src/Whizbang.Generators/ai-docs/value-type-records.md)** - Why sealed records matter (50-200ms difference!)
- **[common-pitfalls.md](src/Whizbang.Generators/ai-docs/common-pitfalls.md)** - Seven major mistakes to avoid

### ⚡ Slash Commands (`.claude/commands/`)

Quick access to common workflows via `/command-name`:

- `/test` - Run all tests
- `/coverage` - Run tests with coverage
- `/format` - Run dotnet format (MANDATORY before commit)
- `/release-check` - Full release checklist
- `/context-tdd` - Load TDD documentation
- `/context-generators` - Load generator documentation
- `/context-testing` - Load testing documentation
- `/context-efcore` - Load EF Core documentation
- `/context-aot` - Load AOT requirements
- `/context-boy-scout` - Load boy scout rule
- `/context-scripts` - Load script standards

See `.claude/commands/` for all available commands.

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
public async Task Execute() { }

[Test]
public async Task MessageEnvelope_AddHop_AddsHopToList() { }
```

**ALL async methods and test methods must end with "Async" suffix.**

---

## TDD Workflow (Strict)

**Red → Green → Refactor** cycle for ALL new code:

1. **RED**: Write failing tests first
2. **GREEN**: Write minimal code to make tests pass
3. **REFACTOR**: Run `dotnet format`, clean up code

**Test-First Rule**: If you write implementation before tests, you're doing it wrong.

---

## Testing Standards

### Test Organization
```
tests/
├── Whizbang.Core.Tests/           # Core functionality tests
├── Whizbang.Policies.Tests/       # Policy system tests
└── Whizbang.Observability.Tests/  # Observability tests
```

### Test Naming Convention
```csharp
[Test]
public async Task ClassName_MethodName_ExpectedBehaviorAsync() {
  // Arrange
  var sut = new SystemUnderTest();

  // Act
  var result = await sut.DoSomethingAsync();

  // Assert
  await Assert.That(result).IsNotNull();
}
```

**Pattern**: `ClassName_MethodOrScenario_ExpectedOutcome` + `Async` suffix

### Assertion Style
Use TUnit's fluent assertions:
```csharp
// ✅ CORRECT
await Assert.That(value).IsEqualTo(expected);
await Assert.That(list).HasCount().EqualTo(3);
await Assert.That(result).IsNotNull();

// ❌ WRONG
Assert.Equal(expected, value);  // xUnit style
Assert.AreEqual(expected, value);  // NUnit style
```

---

## Project Structure

### Source Code
```
src/
├── Whizbang.Core/              # Core types, interfaces, observability
│   ├── Observability/          # MessageEnvelope, MessageHop, etc.
│   ├── Policies/               # PolicyContext, PolicyDecisionTrail
│   └── ValueObjects/           # MessageId, CorrelationId, CausationId
├── Whizbang.Generators/        # Source generators (Roslyn)
└── Whizbang.Testing/           # Testing utilities (future)
```

### Key Files
- `Directory.Build.props` - Shared MSBuild properties
- `Directory.Packages.props` - Central package management
- `.editorconfig` - Code style rules
- `plans/` - Feature planning documents

---

## Common Commands

```bash
# Clean build
dotnet clean && dotnet build

# Run ALL tests (parallel execution, .NET 10 native)
dotnet test --max-parallel-test-modules 8

# Or use PowerShell script for convenience
pwsh scripts/Test-All.ps1

# Run tests with automatic parallel detection (uses CPU core count)
pwsh scripts/Test-All.ps1 -MaxParallel 0

# Run tests with custom parallel level
pwsh scripts/Test-All.ps1 -MaxParallel 4

# Run tests with verbose output
pwsh scripts/Test-All.ps1 -Verbose

# Run tests in AI Mode (compact, parseable output for Claude/AI tools)
pwsh scripts/Test-All.ps1 -AiMode

# Run a specific test project directly
cd tests/Whizbang.Core.Tests && dotnet run

# Format code (ALWAYS run before completion)
dotnet format

# Full cycle
dotnet clean && dotnet build && dotnet test --max-parallel-test-modules 8 && dotnet format
```

**Important Notes:**
- **.NET 10 fully supports `dotnet test`** with Microsoft.Testing.Platform (configured in global.json)
- **Parallel execution** via `--max-parallel-test-modules` runs multiple test projects concurrently (like VS Code Test Explorer)
- **Default parallelism** is Environment.ProcessorCount (typically your CPU core count)
- **TUnit tests** run in parallel by design, both within and across test projects
- **VS Code Test Explorer** uses the same MTP infrastructure for optimal performance
- **AI Mode** (`-AiMode` flag): Provides compact, parseable test output optimized for AI analysis (filters noise, summarizes results)

---

## Code Coverage

### Collecting Coverage

The project uses **Microsoft.Testing.Extensions.CodeCoverage** (built-in .NET coverage) with the Microsoft Testing Platform.

**Important**: Use `dotnet run` (NOT `dotnet test`) for coverage with TUnit/MTP:

```bash
# Navigate to test project directory
cd tests/Whizbang.Generators.Tests

# Run tests with coverage collection
dotnet run -- --coverage --coverage-output-format cobertura --coverage-output coverage.xml

# Coverage report will be generated at:
# bin/Debug/net10.0/TestResults/coverage.xml
```

### Coverage Options

- `--coverage` - Enable coverage collection using dotnet-coverage
- `--coverage-output-format` - Format: `coverage` (default), `xml`, or `cobertura`
- `--coverage-output` - Output file path (default: `coverage.xml`)

### Coverage Formats

- **Cobertura**: XML format compatible with most CI/CD tools and coverage viewers
- **XML**: Microsoft coverage XML format
- **Coverage**: Binary .coverage format for Visual Studio

### Viewing Coverage Results

```bash
# Read coverage summary from Cobertura XML
grep "line-rate" bin/Debug/net10.0/TestResults/coverage.xml | head -5
```

### Coverage Targets

**Current Status** (as of Phase 6 completion):
- **Whizbang.Generators**: 68.4% line coverage, 58.6% branch coverage
- **Whizbang.Core**: TBD (run coverage from Core.Tests)

---

## Technology Stack

**As of December 2025:**

- **.NET 10.0.1** (LTS release, November 2025) - Target framework with 3-year support
- **TUnit 1.2.11+** - Modern source-generated testing framework built on Microsoft.Testing.Platform
- **TUnit.Assertions** - Native fluent assertion library
- **Rocks 9.3.0+** - Roslyn-based source-generated mocking for AOT compatibility
- **Vogen** - Source-generated value objects (`MessageId`, `CorrelationId`, etc.)
- **Bogus** - Test data generation (future)
- **Microsoft.Testing.Platform 2.0+** - Native test runner (replaces VSTest)

**Important Notes:**
- **.NET 10 natively integrates Microsoft.Testing.Platform** - configured in `global.json`
- **`dotnet test` works perfectly** in .NET 10 with `--max-parallel-test-modules` for concurrent execution
- **VS Code Test Explorer** uses the same MTP infrastructure, providing the same parallel execution you see in the UI
- All testing infrastructure is source-generated for zero reflection and AOT compatibility
- Use `pwsh scripts/Test-All.ps1` as a convenient wrapper around `dotnet test` with automatic parallelism detection

---

## Key Principles

1. **Zero Reflection** - Everything via source generators
2. **AOT Compatible** - Native AOT from day one
3. **Type Safe** - Compile-time safety everywhere
4. **Test Driven** - Tests define behavior, implementation follows

---

## Code-Docs Linking

**All library projects** (Core, Generators, Testing, etc.) participate in a bidirectional linking system with the documentation repository:

**How to Add `<docs>` Tags**:
```csharp
/// <summary>
/// Dispatches messages to appropriate handlers
/// </summary>
/// <docs>core-concepts/dispatcher</docs>
public interface IDispatcher {
  // ...
}
```

**Tag Format**:
- Add `/// <docs>path/to/doc</docs>` XML tag above public types
- Path format: `category/doc-name` (e.g., `core-concepts/dispatcher`)
- Must match actual documentation file path in docs repository

**Mapping Generation**:
```bash
# From documentation repository
cd /Users/philcarbone/src/whizbang-lib.github.io
node src/scripts/generate-code-docs-map.mjs

# This scans library code and generates:
# src/assets/code-docs-map.json
```

**MCP Tools for Querying**:
The documentation repository's MCP server provides tools for bidirectional navigation:
- `mcp__whizbang-docs__get-code-location` - Find code implementing a doc concept
- `mcp__whizbang-docs__get-related-docs` - Find docs for a symbol
- `mcp__whizbang-docs__validate-doc-links` - Validate all `<docs>` tags

**When to Add Tags**:
- Public interfaces (IDispatcher, IReceptor, etc.)
- Public classes that implement core concepts
- Key value objects (MessageId, etc.)
- Attributes and markers (not necessarily all of them)

**Best Practices**:
- Tag before committing new public APIs
- Run validation after adding tags
- Keep paths in sync with documentation structure
- One `<docs>` tag per type (above type declaration)

**For complete documentation maintenance workflow** (version awareness, when to update docs, commit strategies), see **[documentation-maintenance.md](ai-docs/documentation-maintenance.md)**.

---

## Code-Tests Linking

**NEW**: Bidirectional linking between source code and tests for improved test coverage awareness and maintainability:

**Architecture**:
1. **Convention-based discovery** - Automatically links tests via naming patterns (e.g., `DispatcherTests` → `Dispatcher`)
2. **`<tests>` XML tags** (optional) - Manual override for complex cases
3. **Script-based generation** - Scans tests and source code to build bidirectional mapping
4. **MCP Server Tools** - Programmatic access to test mappings via documentation repository

**How to Add `<tests>` Tags** (Optional):
```csharp
/// <summary>
/// Dispatches messages to appropriate handlers
/// </summary>
/// <docs>core-concepts/dispatcher</docs>
/// <tests>Whizbang.Core.Tests/DispatcherTests.cs:Dispatch_SendsMessageToCorrectReceptorAsync</tests>
public void Dispatch<TMessage>(TMessage message) where TMessage : IMessage {
  // ...
}
```

**Tag Format**:
- Add `/// <tests>TestFile.cs:TestMethodName</tests>` XML tag above types/methods
- Path format: `ProjectName/TestFile.cs:TestMethodName`
- Used only when convention-based discovery isn't sufficient

**Mapping Generation**:
```bash
# From documentation repository
cd /Users/philcarbone/src/whizbang-lib.github.io
node src/scripts/generate-code-tests-map.mjs

# This scans library tests and source code, generates:
# src/assets/code-tests-map.json
```

**MCP Tools for Querying**:
The documentation repository's MCP server provides tools for bidirectional navigation:
- `mcp__whizbang-docs__get-tests-for-code` - Find all tests for a code symbol
- `mcp__whizbang-docs__get-code-for-test` - Find code tested by a test method
- `mcp__whizbang-docs__validate-test-links` - Validate all code-test links
- `mcp__whizbang-docs__get-coverage-stats` - Get test coverage statistics

**When Claude Updates Code**:
When modifying implementation code, Claude should:
1. Query MCP to find related tests: `mcp__whizbang-docs__get-tests-for-code({ symbol: "ClassName" })`
2. Review and update those tests if behavior changes
3. Add new tests for new functionality
4. Follow naming convention for new tests

**When Claude Updates Tests**:
When modifying tests, Claude should:
1. Query MCP to find what code is being tested: `mcp__whizbang-docs__get-code-for-test({ testKey: "TestClass.TestMethod" })`
2. Understand what the test is validating
3. Ensure test name accurately reflects behavior being tested

**Best Practices**:
- Follow naming convention: `ClassNameTests` for `ClassName`
- Use descriptive test method names: `ClassName_MethodName_ExpectedBehaviorAsync`
- Add `<tests>` tags only when convention-based linking fails
- Regenerate mapping after adding/removing tests: `node src/scripts/generate-code-tests-map.mjs`
- Use MCP tools to ensure test coverage when making changes

**Status**: Phase 1 complete - Script-based generation and MCP tools operational. Source generator and analyzer planned for v2.

---

## Documentation Maintenance

**CRITICAL**: When modifying public APIs in ANY project (Core, Generators, Testing, etc.), documentation MUST be updated.

See **[documentation-maintenance.md](ai-docs/documentation-maintenance.md)** for:
- Version awareness workflow (ask version first!)
- Same version vs. next version strategies
- When and how to update documentation
- Safety: commit before deletions
- Claude's responsibility and checklist

**Key Principle**: Always ask "What version are you working on?" before making documentation changes.

---

## ID Generation

**UUIDv7** for all identity value objects:
```csharp
// ✅ CORRECT - Time-ordered, database-friendly
MessageId.New()  // Uses Guid.CreateVersion7() internally

// ❌ WRONG - Random GUIDs cause index fragmentation
Guid.NewGuid()
```

---

## Observability Architecture

**Hop-Based Design** - Network packet analogy:

- **MessageEnvelope**: Identity + Payload + Hops
  - Contains: MessageId, CorrelationId, CausationId, Payload, Hops (required)

- **MessageHop**: All contextual metadata
  - Type: `Current` (this message) or `Causation` (parent message)
  - Routing: Topic, StreamKey, PartitionIndex, SequenceNumber
  - Security: SecurityContext (UserId, TenantId)
  - Policy: PolicyDecisionTrail
  - Metadata: Dictionary with stitching
  - Debugging: CallerMemberName, CallerFilePath, CallerLineNumber
  - Timing: Timestamp, Duration

**First Hop Required** - Every message must originate somewhere.

**Causation Hops** - Carry forward parent message hops for distributed tracing.

---

## Work Coordination & Event Store Architecture

### IWorkCoordinator - Atomic Work Batch Processing

The `IWorkCoordinator` interface provides atomic, transactional work coordination across outbox, inbox, and event store tracking:

```csharp
Task<WorkBatch> ProcessWorkBatchAsync(
  Guid instanceId, string serviceName, string hostName, int processId,
  Dictionary<string, JsonElement>? metadata,

  // Outbox/Inbox completions and failures
  MessageCompletion[] outboxCompletions,
  MessageFailure[] outboxFailures,
  MessageCompletion[] inboxCompletions,
  MessageFailure[] inboxFailures,

  // Event store tracking (receptors & perspectives)
  ReceptorProcessingCompletion[] receptorCompletions,
  ReceptorProcessingFailure[] receptorFailures,
  PerspectiveCheckpointCompletion[] perspectiveCompletions,
  PerspectiveCheckpointFailure[] perspectiveFailures,

  // New work to store
  OutboxMessage[] newOutboxMessages,
  InboxMessage[] newInboxMessages,

  // Lease renewals
  Guid[] renewOutboxLeaseIds,
  Guid[] renewInboxLeaseIds,

  WorkBatchFlags flags = WorkBatchFlags.None,
  int partitionCount = 10000,
  int maxPartitionsPerInstance = 100,
  int leaseSeconds = 300,
  int staleThresholdSeconds = 600,
  CancellationToken cancellationToken = default
);
```

**Key Characteristics**:
- **Atomic Operations**: All work in a batch succeeds or fails together (database transaction)
- **Lease-Based Coordination**: Prevents duplicate processing across instances
- **Partition-Based Distribution**: Work distributed via consistent hashing (UUIDv7 message IDs)
- **PostgreSQL Implementation**: Uses `process_work_batch` stored procedure for performance

### Event Store Tracking - Receptors & Perspectives

**Two distinct tracking patterns for event processing:**

#### 1. Receptors - Independent Event Handlers

**Pattern**: Log-style tracking where multiple receptors process the same event independently.

```csharp
public record ReceptorProcessingCompletion {
  public required Guid EventId { get; init; }
  public required string ReceptorName { get; init; }
  public required ReceptorProcessingStatus Status { get; init; }
}

public record ReceptorProcessingFailure {
  public required Guid EventId { get; init; }
  public required string ReceptorName { get; init; }
  public required ReceptorProcessingStatus Status { get; init; }
  public required string Error { get; init; }
}
```

**Use Cases**:
- Side effects (sending emails, notifications)
- Read model updates that don't require ordering
- Analytics/metrics collection
- Audit logging

**Characteristics**:
- Many receptors can process the same event
- No ordering guarantees required
- Failures tracked per receptor
- Retries handled independently

**Database**: `wh_receptor_processing` table tracks (event_id, receptor_name) pairs

#### 2. Perspectives - Checkpoint-Based Read Models

**Pattern**: Checkpoint-based processing per stream for read model projections with time-travel capabilities.

```csharp
public record PerspectiveCheckpointCompletion {
  public required Guid StreamId { get; init; }
  public required string PerspectiveName { get; init; }
  public required Guid LastEventId { get; init; }
  public required PerspectiveProcessingStatus Status { get; init; }
}

public record PerspectiveCheckpointFailure {
  public required Guid StreamId { get; init; }
  public required string PerspectiveName { get; init; }
  public required Guid LastEventId { get; init; }
  public required PerspectiveProcessingStatus Status { get; init; }
  public required string Error { get; init; }
}
```

**Use Cases**:
- Read model projections (e.g., order summary view)
- Temporal queries (state as of specific event)
- Rebuilding projections from event history
- Stream-specific views

**Characteristics**:
- One checkpoint per (stream_id, perspective_name) pair
- Ordered event processing within stream
- Can rebuild from any point (time-travel)
- Checkpoint = last successfully processed event

**Database**: `wh_perspective_checkpoints` table tracks (stream_id, perspective_name, last_event_id)

### Work Coordination Flow

```
1. Application processes message
2. Calls ProcessWorkBatchAsync with:
   - Completions for successfully processed messages
   - Failures for failed messages
   - New outbox messages to send
   - Receptor/Perspective tracking updates

3. PostgreSQL process_work_batch:
   - Deletes completed messages
   - Updates failed messages (retry counts)
   - Inserts new outbox messages
   - Updates receptor_processing records
   - Updates perspective_checkpoints
   - Claims new work (via leasing)
   - Returns: WorkBatch with claimed work

4. WorkCoordinatorPublisherWorker:
   - Polls ProcessWorkBatchAsync every N milliseconds
   - Publishes claimed outbox messages to transport
   - Reports completions/failures back to coordinator
```

### Database Schema

**Outbox/Inbox Tables**:
- `wh_outbox` - Outbound messages awaiting publication
- `wh_inbox` - Inbound messages awaiting processing

**Event Store Tracking Tables**:
- `wh_receptor_processing` - Tracks which receptors processed which events
- `wh_perspective_checkpoints` - Tracks read model projection checkpoints

**Key Columns**:
- `instance_id`, `lease_expiry` - Lease-based coordination
- `partition_number` - Consistent hashing for work distribution
- `status` - MessageProcessingStatus flags (Stored, Published, Failed, etc.)
- `attempts` - Retry tracking

### AOT-Compatible Serialization

All work coordination uses **JsonTypeInfo** for AOT-compatible JSON serialization:

```csharp
private string SerializeReceptorCompletions(ReceptorProcessingCompletion[] completions) {
  if (completions.Length == 0) return "[]";
  return JsonSerializer.Serialize(
    completions,
    _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingCompletion[]))
  );
}
```

**Pattern**: Use `JsonContextRegistry.CreateCombinedOptions()` for all serialization.

---

## Plan Documents

Living documents in `plans/` directory:
- Track implementation progress
- Document design decisions
- Maintain changelog
- Record test counts

**Update plan documents after completing features.**

---

## Notes

- This is a **library**, not an application
- Follow .NET library conventions
- XML documentation required for all public APIs
- **Code-Docs Linking**: Add `<docs>` tags to public APIs for bidirectional navigation (see Code-Docs Linking section)
- **Documentation Maintenance**: Update docs when changing public APIs in ANY project (see [documentation-maintenance.md](ai-docs/documentation-maintenance.md))
- See main `/Users/philcarbone/src/CLAUDE.md` for cross-repo guidance
- See `TESTING.md` for detailed testing guidelines

---

## Cross-Repository Context

This repository is part of the Whizbang project workspace:

- **Workspace CLAUDE.md**: `/Users/philcarbone/src/CLAUDE.md` - Cross-repo guidance and workflows
- **Documentation Repo**: `/Users/philcarbone/src/whizbang-lib.github.io/` - Living specifications and user docs
- **VSCode Extension**: `/Users/philcarbone/src/whizbang-vscode/` - IDE integration

For comprehensive workflow and architecture guidance, see the workspace CLAUDE.md.
