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

# Run tests
dotnet test

# Run tests with no build (faster iteration)
dotnet test --no-build

# Format code (ALWAYS run before completion)
dotnet format

# Full cycle
dotnet clean && dotnet build && dotnet test && dotnet format
```

---

## Technology Stack

- **.NET 9.0** - Target framework
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
