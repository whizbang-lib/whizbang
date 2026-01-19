# Third-Party Licenses

Whizbang is licensed under the MIT License. This document lists all third-party dependencies and their licenses for transparency and compliance.

## License Summary

All dependencies use permissive open-source licenses compatible with MIT:
- **MIT License**: Most dependencies
- **Apache License 2.0**: Some Microsoft and community packages
- **BSD License**: Some utilities
- **PostgreSQL License**: Npgsql (very permissive)

## Core Dependencies

### .NET & Microsoft Packages
**License**: MIT
**Source**: https://github.com/dotnet/runtime

- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.DependencyInjection.Abstractions
- Microsoft.Extensions.Diagnostics.HealthChecks
- Microsoft.Extensions.Hosting
- Microsoft.Extensions.Logging.Abstractions
- Microsoft.Extensions.ServiceDiscovery
- Microsoft.EntityFrameworkCore
- Microsoft.EntityFrameworkCore.Relational
- Microsoft.Data.Sqlite
- Microsoft.Data.SqlClient
- System.Text.Json

### Testing Frameworks
**TUnit**
**License**: MIT
**Source**: https://github.com/thomhurst/TUnit

**Rocks**
**License**: MIT
**Source**: https://github.com/JasonBock/Rocks

**Bogus**
**License**: MIT
**Source**: https://github.com/bchavez/Bogus

### Database & Data Access
**Npgsql**
**License**: PostgreSQL License (very permissive, similar to MIT/BSD)
**Source**: https://github.com/npgsql/npgsql

**Dapper**
**License**: Apache License 2.0
**Source**: https://github.com/DapperLib/Dapper

### Source Generators & Analyzers
**Microsoft.CodeAnalysis (Roslyn)**
**License**: MIT
**Source**: https://github.com/dotnet/roslyn

- Microsoft.CodeAnalysis.CSharp
- Microsoft.CodeAnalysis.Analyzers
- Microsoft.CodeAnalysis.BannedApiAnalyzers

**Roslynator**
**License**: Apache License 2.0
**Source**: https://github.com/dotnet/roslynator

- Roslynator.Analyzers
- Roslynator.CodeAnalysis.Analyzers
- Roslynator.Formatting.Analyzers

**Vogen**
**License**: Apache License 2.0
**Source**: https://github.com/SteveDunn/Vogen

**PolySharp**
**License**: MIT
**Source**: https://github.com/Sergio0694/PolySharp

### Azure & Messaging
**Azure.Messaging.ServiceBus**
**License**: MIT
**Source**: https://github.com/Azure/azure-sdk-for-net

**RabbitMQ.Client**
**License**: Apache License 2.0
**Source**: https://github.com/rabbitmq/rabbitmq-dotnet-client

### Utilities
**Medo.Uuid7**
**License**: MIT
**Source**: https://github.com/medo64/Medo.Uuid7

**ILRepack**
**License**: Apache License 2.0
**Source**: https://github.com/gluck/il-repack

### .NET Aspire
**Aspire.Hosting.**
**License**: MIT
**Source**: https://github.com/dotnet/aspire

- Aspire.Hosting.AppHost
- Aspire.Hosting.Azure.ServiceBus
- Aspire.Hosting.PostgreSQL
- Aspire.Hosting.RabbitMQ

### Build & Testing Tools
**coverlet.collector**
**License**: MIT
**Source**: https://github.com/coverlet-coverage/coverlet

**Microsoft.Testing.Extensions.CodeCoverage**
**License**: MIT
**Source**: https://github.com/microsoft/testfx

**BenchmarkDotNet**
**License**: MIT
**Source**: https://github.com/dotnet/BenchmarkDotNet

## License Compatibility

All dependencies use licenses that are:
1. **OSI-approved** open source licenses
2. **Compatible with MIT License** (permissive, no copyleft requirements)
3. **Allow commercial use** without restriction
4. **Do not require attribution** in binary distributions (though we provide it voluntarily)

## Full License Texts

Full license texts for all dependencies can be found in their respective source repositories (links provided above) or in the NuGet package contents.

## Questions

For questions about licensing, please open an issue at:
https://github.com/whizbang-lib/whizbang/issues

---

**Last Updated**: 2026-01-16
**Whizbang Version**: 0.1.0-alpha
