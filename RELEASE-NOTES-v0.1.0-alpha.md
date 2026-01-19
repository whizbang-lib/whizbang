# Whizbang v0.1.0-alpha Release Notes

## Release Summary

Whizbang v0.1.0-alpha is the **Foundation Release** establishing core infrastructure for event-driven, CQRS, and event-sourced .NET applications with zero reflection and AOT compatibility.

**Status**: Alpha - Ready for internal testing and validation
**Release Date**: 2026-01-15
**Target Framework**: .NET 10.0

## 📦 Published Packages (12)

All packages published to NuGet with v0.1.0 version:

### Core & Generators
- **Whizbang.Core** - Core types, interfaces, dispatcher, observability
- **Whizbang.Generators** - Roslyn source generators for zero-reflection code
- **Whizbang.Generators.Shared** - Shared utilities for generator development

### Data Access
- **Whizbang.Data.Postgres** - PostgreSQL integration foundation
- **Whizbang.Data.Dapper.Postgres** - Dapper-based PostgreSQL data access
- **Whizbang.Data.Dapper.Sqlite** - Dapper-based SQLite data access
- **Whizbang.Data.EFCore.Postgres** - EF Core 10 PostgreSQL with JsonB support
- **Whizbang.Data.EFCore.Custom** - Custom EF Core extensions
- **Whizbang.Data.EFCore.Postgres.Generators** - EF Core source generators

### Transport & Hosting
- **Whizbang.Transports.AzureServiceBus** - Azure Service Bus transport
- **Whizbang.Hosting.Azure.ServiceBus** - Aspire hosting for Azure Service Bus

### Tools
- **Whizbang.CLI** - Command-line tool for schema management (`whizbang` global tool)

## ✅ Quality Metrics

### Build Quality
- **Compiler Errors**: 0
- **Compiler Warnings**: 0
- **AOT Compliance**: 100% (zero reflection in production code)
- **Code Formatting**: 100% compliant

### Testing
- **Total Tests**: 4,139
- **Passing**: 4,115 (100%)
- **Skipped**: 24 (intentionally)
- **Failed**: 0

Test breakdown:
- Unit tests: 100% passing
- Integration tests: 100% passing (with Docker)
- Test frameworks: TUnit 1.5.70.0, Rocks 9.3.0, Bogus

### Package Quality
- Version: 0.1.0 (consistent across all packages)
- License: MIT
- Symbols: .snupkg files included
- README: Embedded in all packages
- Source Link: Configured for debugging

## 🎯 Features

### Zero Reflection Architecture
- All type discovery via source generators
- Native AOT compatible from day one
- Compile-time validation and generation

### Event-Driven Foundation
- Message dispatcher with receptor pattern
- Event sourcing primitives
- CQRS command/query separation

### Database Support
- PostgreSQL with JsonB and UUIDv7
- EF Core 10 with compiled models
- Dapper for high-performance queries
- SQLite for testing

### Azure Integration
- Azure Service Bus transport
- Aspire hosting support
- Health checks included

## 📋 Known Limitations (Alpha)

1. **Whizbang.Data.Schema**: Multi-targeting issue prevents packaging (will fix in beta)
2. **Whizbang.Testing**: Utilities library not yet packaged (will include in beta)
3. **Whizbang.Transports.RabbitMQ**: In development, not yet packaged
4. **Whizbang.Hosting.RabbitMQ**: In development, not yet packaged
5. **Whizbang.Data.Dapper.Custom**: Utilities library not yet built/packaged

## 🚀 Installation

### NuGet Packages
```bash
# Core library
dotnet add package Whizbang.Core --version 0.1.0

# PostgreSQL with EF Core
dotnet add package Whizbang.Data.EFCore.Postgres --version 0.1.0

# Azure Service Bus
dotnet add package Whizbang.Transports.AzureServiceBus --version 0.1.0
dotnet add package Whizbang.Hosting.Azure.ServiceBus --version 0.1.0

# Source generators (automatically included)
dotnet add package Whizbang.Generators --version 0.1.0
```

### Global CLI Tool
```bash
dotnet tool install --global Whizbang.CLI --version 0.1.0
whizbang --version
```

## 🔧 CI/CD Infrastructure

### GitHub Actions Workflows
- **build.yml**: Build and format verification
- **test.yml**: Automated testing with Codecov
- **quality.yml**: SonarCloud code quality scanning
- **nuget-pack.yml**: Package validation on PRs
- **nuget-publish.yml**: Automated publishing on version tags

### Release Process
1. Tag commit: `git tag v0.1.0-alpha.1`
2. Push tag: `git push origin v0.1.0-alpha.1`
3. GitHub Actions automatically:
   - Builds all projects
   - Runs all tests
   - Creates NuGet packages
   - Publishes to NuGet.org
   - Creates GitHub Release

## 📚 Documentation

- **Repository**: https://github.com/whizbang-lib/whizbang
- **Documentation Site**: https://whizbang-lib.github.io
- **API Documentation**: TBD (Beta phase)

## 🎉 Completed Release Checklist

### Section 1: Repository Hygiene ✅
- [x] Clean root directory (77,763 deletions)
- [x] Reorganize scripts
- [x] Fix absolute paths to relative

### Section 2: Build Quality ✅
- [x] 0 errors, 0 warnings
- [x] AOT compliance verified
- [x] BannedSymbols.txt restored

### Section 3: Documentation & Standards ✅
- [x] SECURITY.md created
- [x] CONTRIBUTORS.md created
- [x] Standard repository files

### Section 4: CLAUDE.md Organization ✅
- [x] .github/SETUP.md created
- [x] CONTRIBUTING.md updated

### Section 5: CI/CD Infrastructure ✅
- [x] 5 GitHub Actions workflows
- [x] Automated build/test/publish

### Section 6: NuGet Package Configuration ✅
- [x] Package metadata configured
- [x] IsPackable settings (28 projects)
- [x] 12 packages successfully created

### Section 7: Quality Assurance ✅
- [x] 4,115 tests passing
- [x] Code formatting clean
- [x] Build: 0 errors, 0 warnings

## 🔜 Beta Phase (Upcoming)

Sections 8-11 deferred to Beta release:
- Section 8: Documentation Publishing
- Section 9: Open Source Services (SonarCloud, Codecov)
- Section 10: Legal & Compliance
- Section 11: Release Process Documentation

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and [.github/SETUP.md](.github/SETUP.md) for development environment setup.

## 📄 License

MIT License - see [LICENSE](LICENSE) file

---

**Generated**: 2026-01-15
**Release Manager**: Claude Sonnet 4.5
**Repository**: https://github.com/whizbang-lib/whizbang
