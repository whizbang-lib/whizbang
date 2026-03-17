<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="assets/logo-light.svg">
    <img alt="Whizbang" src="assets/logo-light.svg" width="500">
  </picture>
</p>

<p align="center">
  A comprehensive .NET library for building event-driven, CQRS, and event-sourced applications with zero reflection and AOT compatibility.
</p>

<p align="center">
  <a href="https://whizba.ng/">Documentation</a> &middot;
  <a href="https://www.nuget.org/packages/Whizbang.Core/">NuGet</a> &middot;
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <a href="https://github.com/whizbang-lib/whizbang/actions/workflows/ci.yml"><img src="https://github.com/whizbang-lib/whizbang/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://codecov.io/gh/whizbang-lib/whizbang"><img src="https://codecov.io/gh/whizbang-lib/whizbang/branch/main/graph/badge.svg" alt="codecov"></a>
  <a href="https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang"><img src="https://sonarcloud.io/api/project_badges/measure?project=whizbang-lib_whizbang&metric=alert_status" alt="Quality Gate Status"></a>
  <a href="https://www.nuget.org/packages/Whizbang.Core/"><img src="https://img.shields.io/nuget/v/Whizbang.Core.svg" alt="NuGet"></a>
  <a href="https://opensource.org/licenses/MIT"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT"></a>
</p>

<p align="center">
  <a href="https://github.com/whizbang-lib/whizbang/actions/workflows/security-secrets.yml"><img src="https://github.com/whizbang-lib/whizbang/actions/workflows/security-secrets.yml/badge.svg" alt="Secret Scanning"></a>
  <a href="https://github.com/whizbang-lib/whizbang/actions/workflows/security-supply-chain.yml"><img src="https://github.com/whizbang-lib/whizbang/actions/workflows/security-supply-chain.yml/badge.svg" alt="Supply Chain"></a>
  <a href="https://securityscorecards.dev/viewer/?uri=github.com/whizbang-lib/whizbang"><img src="https://api.securityscorecards.dev/projects/github.com/whizbang-lib/whizbang/badge" alt="OSSF Scorecard"></a>
</p>

<p align="center">
  <a href="https://codecov.io/gh/whizbang-lib/whizbang"><img src="https://codecov.io/gh/whizbang-lib/whizbang/graphs/sunburst.svg?token=F1AZXLI2MM" alt="Codecov Sunburst" width="200"></a>
</p>

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

- **.NET 10.0** - Target framework (LTS)
- **Vogen** - Source-generated value objects
- **TUnit 1.5+** - Modern source-generation test framework
- **TUnit.Assertions** - Native fluent assertions
- **Rocks 9.3+** - Source-generation mocking for AOT compatibility
- **Bogus** - Test data generation
- **EF Core 10** - Database access with compiled models
- **Dapper** - High-performance SQL queries
- **PostgreSQL** - Primary database with JsonB and UUIDv7 support

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

## Release Workflow

### Three-Phase Release Process

Whizbang uses a three-phase release process:

1. **Alpha** - Internal testing and validation
2. **Beta** - Limited public testing with early adopters
3. **GA** - General availability for public use

### Release Checklist

The complete release checklist is maintained in `.github/RELEASE.md`.

### Using the `/release` Command

Claude Code can guide you through the release process:

```bash
/release alpha   # Start alpha release
/release beta    # Start beta release
/release ga      # Start GA release
```

### Manual Release Process

If not using Claude Code, follow these steps:

#### Alpha Release
1. Follow all items in `.github/RELEASE.md` Alpha Phase section
2. Verify all exit criteria are met
3. Tag version: `git tag -a v0.1.0-alpha.1 -m "Alpha 1"`
4. Push tag: `git push origin v0.1.0-alpha.1`
5. GitHub Actions will automatically publish to NuGet

#### Beta Release
1. Complete Alpha phase
2. Address feedback from alpha testing
3. Follow all items in `.github/RELEASE.md` Beta Phase section
4. Tag version: `git tag -a v0.1.0-beta.1 -m "Beta 1"`
5. Push tag: `git push origin v0.1.0-beta.1`

#### GA Release
1. Complete Beta phase
2. Address feedback from beta testing
3. Follow all items in `.github/RELEASE.md` GA Phase section
4. Tag version: `git tag -a v0.1.0 -m "Release v0.1.0"`
5. Push tag: `git push origin v0.1.0`
6. Announce to community

### Version Numbering

See GitVersion section for automatic version calculation.

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

See [CONTRIBUTING.md](CONTRIBUTING.md) for full guidelines.
