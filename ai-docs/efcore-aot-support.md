# EF Core 10 AOT Support

**Last Updated**: December 2025
**EF Core Version**: 10.0
**Status**: Experimental

## Overview

EF Core 10 has **limited AOT support** through precompiled queries and compiled models. However, **many EF Core features remain incompatible with Native AOT**, particularly around migrations and schema management.

## What IS Supported

### ✅ Precompiled Queries (Experimental)
- EF Core can precompile LINQ queries at build time
- Queries are intercepted and replaced with AOT-compatible compiled code
- Enable with `<PublishAot>true</PublishAot>` in your project file
- Use `dotnet ef dbcontext optimize --precompile-queries --nativeaot` command

**Status**: Experimental as of EF Core 10, expect breaking changes

### ✅ Compiled Models
- DbContext model can be precompiled to C# code
- Eliminates reflection during model building
- Generated code may be large and slow to compile

**Limitations**:
- Known to generate non-AOT-compatible code in some scenarios (see [Issue #36817](https://github.com/dotnet/efcore/issues/36817))
- Still produces IL3050 warnings in EF Core 10 RC1

### ✅ Query Execution
- Basic LINQ queries work with precompilation
- Some providers may not support all query patterns

## What IS NOT Supported

### ❌ Migrations and Schema Generation

**The following methods are annotated with `[RequiresDynamicCode]` and will produce IL3050 warnings**:

```csharp
// NOT AOT-compatible:
database.GenerateCreateScript()
database.Migrate()
database.EnsureCreated()
```

**Official Guidance** (from `RequiresDynamicCodeAttribute` in EF Core source):
> "Migrations operations are not supported with NativeAOT.
> Use a migration bundle or an alternate way of executing migration operations."

### ❌ Configuration Binding (Not EF Core-specific)

```csharp
// NOT AOT-compatible:
services.Configure<MyOptions>(configuration.GetSection("MySection"))

// AOT-compatible alternative:
services.AddOptions<MyOptions>()
    .Bind(configuration.GetSection("MySection"))
```

## Enabling AOT Support in Whizbang

### Prerequisites

**Package Versions** (from `Directory.Packages.props`):
```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Tasks" Version="10.0.0" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
```

**Project Configuration**:
```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

### Generating Compiled Models and Precompiled Queries

Run this command in each ECommerce service project that uses EF Core:

```bash
# For each DbContext (BffDbContext, OrderDbContext, InventoryDbContext, etc.):
dotnet ef dbcontext optimize --precompile-queries --nativeaot \
  --context BffDbContext \
  --output-dir Generated/CompiledModel \
  --namespace ECommerce.BFF.API.Generated

# This generates:
# - Compiled model (DbContext without reflection)
# - Query interceptors (precompiled LINQ queries)
```

**When to regenerate**:
- After any change to DbContext or entity configurations
- Typically part of CI/CD publish workflow
- C# interceptors are invalidated by source changes

## Recommended Patterns for Whizbang

### Schema Initialization

**Development/Deployment** (NOT runtime):
- Use `GenerateCreateScript()` in source generators (suppressed IL3050)
- Schema initialization happens once at deployment, not in hot paths
- EF Migration Bundles for production deployments (future consideration)

**Runtime** (AOT-compatible):
- Use compiled models (via `dotnet ef dbcontext optimize --nativeaot`)
- Use precompiled queries for all LINQ queries
- No dynamic schema generation
- Pre-created database schema

### Whizbang's Current Approach

**Source Generator** (`EFCoreServiceRegistrationGenerator`):
```csharp
// Template: DbContextSchemaExtensionTemplate.cs
// Generates EnsureWhizbangDatabaseInitializedAsync()

#pragma warning disable IL3050 // JUSTIFIED: Schema init at deployment, not runtime
var script = dbContext.Database.GenerateCreateScript();
#pragma warning restore IL3050

script = MakeScriptIdempotent(script);
await dbContext.Database.ExecuteSqlRawAsync(script, cancellationToken);
```

**Justification**:
1. Schema initialization runs **once at app startup** (deployment-time), not in hot paths
2. ECommerce sample demonstrates AOT-compatible **runtime behavior** (queries, perspectives)
3. No AOT-compatible alternative exists for schema generation
4. Alternative (migration bundles) requires external tooling and separate deployment artifacts

## Future Outlook

**EF Core Team's Position** (as of December 2025):
- NativeAOT support is **experimental**
- Migration operations will likely **remain incompatible** with AOT
- Focus is on query execution, not schema management
- Migration bundles are the recommended deployment approach for production

**Whizbang's Position**:
- Continue using `GenerateCreateScript()` with suppression for dev/deployment scenarios
- Document clearly that schema init is deployment-time, not AOT runtime
- Investigate migration bundles for production deployments (future enhancement)
- Keep runtime queries AOT-compatible via precompilation

## Testing AOT Compatibility

To verify your app is truly AOT-compatible:

```bash
# Publish with AOT
dotnet publish -c Release -r linux-x64 /p:PublishAot=true

# Warnings to expect (and suppress):
# - IL3050 in schema initialization (justified)
# - IL2026/IL3050 in configuration binding (use AddOptions().Bind())

# Warnings that indicate real problems:
# - IL3050 in query execution (use precompiled queries)
# - IL2057 in Type.GetType() calls (use source generators)
```

## References

- [EF Core NativeAOT and Precompiled Queries](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries)
- [EF Core Migrations Overview](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/)
- [Migration Bundles](https://devblogs.microsoft.com/dotnet/introducing-devops-friendly-ef-core-migration-bundles/)
- [IL3050 Warning Documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/warnings/il3050)
- [EF Core Issue #36817: Compiled models generate non-AOT compatible code](https://github.com/dotnet/efcore/issues/36817)
- [EF Core Issue #34705: EF Core 9 Native AOT](https://github.com/dotnet/efcore/issues/34705)

## Key Takeaway

**EF Core 10 supports AOT for QUERY EXECUTION (experimental), but NOT for MIGRATIONS or SCHEMA GENERATION.**

Whizbang's use of `GenerateCreateScript()` in source generators for schema initialization is justified because:
1. It runs at deployment-time (startup), not runtime
2. There is no AOT-compatible alternative
3. Runtime queries remain AOT-compatible through precompilation
4. The ECommerce sample demonstrates proper AOT-compatible runtime patterns
