# Whizbang

A comprehensive .NET library for building event-driven, CQRS, and event-sourced applications with zero reflection and AOT compatibility.

## Version 0.1.0 - Foundation Release

This is the foundation release establishing all core components with in-memory implementations. The focus is on breadth over depth, ensuring every component exists and works together from day one.

## Project Structure

```
whizbang/
├── src/
│   ├── Whizbang.Core/              # Core interfaces and types
│   ├── Whizbang.Generators/        # Source generators (future)
│   └── Whizbang.Testing/           # Testing utilities
└── tests/
    ├── Whizbang.Core.Tests/        # Unit tests
    └── Whizbang.Documentation.Tests/ # Documentation example tests
```

## Core Components (v0.1.0)

### Receptors
Stateless message handlers that receive commands and produce events. Type-safe with flexible response types (single, tuple, array, Result<T>).

### Dispatcher
Message routing and orchestration engine. Routes messages to appropriate handlers with context tracking (correlation/causation IDs).

### Value Objects (Vogen)
Type-safe IDs using source-generation:
- `MessageId` - Unique message identifier
- `CorrelationId` - Logical workflow identifier
- `CausationId` - Causal chain identifier

## Technology Stack

- **.NET 9.0** - Target framework
- **Vogen** - Source-generated value objects
- **TUnit** - Modern source-generation test framework
- **TUnit.Assertions** - Native assertions
- **Rocks** - Source-generation mocking
- **Bogus** - Test data generation

## Getting Started

### Build
```bash
dotnet build
```

### Run Tests
```bash
dotnet test
```

### Current Status
All tests are currently **failing** by design. This is TDD - tests define the behavior, implementation comes next.

## Philosophy

- **Zero Reflection** - Everything via source generators
- **AOT Compatible** - Native AOT from day one
- **Type Safe** - Compile-time safety everywhere
- **Test Driven** - Comprehensive test coverage

## Documentation

See the [whizbang-lib.github.io](https://github.com/whizbang/whizbang-lib.github.io) repository for comprehensive documentation of all features and design decisions.

## Next Steps

1. ✅ Foundation skeleton (DONE)
2. ⏳ Implement Receptors to pass tests
3. ⏳ Implement Dispatcher to pass tests
4. ⏳ Add source generators for handler discovery
5. ⏳ Add remaining components (Perspectives, Lenses, etc.)

## Contributing

This is v0.1.0 - the foundation. Follow the TDD approach:
1. Tests define behavior (already written)
2. Implement to make tests pass (green)
3. Refactor for quality (refactor)

See [CONTRIBUTING.md](../whizbang-lib.github.io/CONTRIBUTING.md) for full guidelines.
