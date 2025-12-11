# CLAUDE.md - Whizbang Library Repository

**Purpose**: Quick reference for AI coding standards and workflow in the Whizbang library.

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

# Run ALL tests
dotnet test

# Run tests without rebuilding
dotnet test --no-build

# Run a specific test project
cd tests/Whizbang.Core.Tests && dotnet run

# Format code (ALWAYS run before completion)
dotnet format

# Full cycle
dotnet clean && dotnet build && dotnet test && dotnet format
```

**Note**: .NET 10 uses Microsoft.Testing.Platform (configured via `global.json`) for native `dotnet test` support.

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

- **.NET 10.0 RC2** - Target framework
- **Vogen** - Source-generated value objects (`MessageId`, etc.)
- **TUnit v0.88** - Modern test framework with source generation
- **TUnit.Assertions** - Fluent assertion library
- **Rocks** - Source-generated mocking (future)
- **Bogus** - Test data generation (future)

---

## Key Principles

1. **Zero Reflection** - Everything via source generators
2. **AOT Compatible** - Native AOT from day one
3. **Type Safe** - Compile-time safety everywhere
4. **Test Driven** - Tests define behavior, implementation follows

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
- See main `/Users/philcarbone/src/CLAUDE.md` for cross-repo guidance
- See `TESTING.md` for detailed testing guidelines
