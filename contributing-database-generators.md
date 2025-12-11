# Contributing Database-Specific Generators

This guide explains how to create a new database generator (e.g., Dapper, MongoDB, CosmosDB) that integrates with Whizbang's shared discovery infrastructure.

## Overview

Whizbang uses a **three-layer generator architecture** to avoid code duplication:

1. **Whizbang.Generators.Shared** - Reusable discovery logic (NOT a generator itself)
2. **Whizbang.Generators** - Core generators for dispatcher, receptors, perspectives
3. **Database-specific generators** - EF Core, Dapper, etc. (use shared discovery)

This architecture ensures that perspective discovery, DbContext discovery, and template utilities are written once and reused across all generators.

## Quick Start

### 1. Create Generator Project

```bash
cd src
mkdir Whizbang.Data.YourDb.Generators
cd Whizbang.Data.YourDb.Generators
```

**YourDb.Generators.csproj**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.12.0" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.11.0" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../Whizbang.Generators.Shared/Whizbang.Generators.Shared.csproj" />
  </ItemGroup>

  <!-- Embed template files as resources -->
  <ItemGroup>
    <EmbeddedResource Include="Templates/**/*.cs" />
  </ItemGroup>
</Project>
```

### 2. Use Shared Discovery

The shared library provides three discovery classes:

**PerspectiveDiscovery** - Discovers perspectives from handlers or DbSet properties:
```csharp
using Whizbang.Generators.Shared.Discovery;
using Whizbang.Generators.Shared.Models;

[Generator]
public class YourDbGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover perspectives from IHandlePerspective<TEvent, TState>
    var perspectives = context.SyntaxProvider
      .CreateSyntaxProvider(
        predicate: PerspectiveDiscovery.IsPotentialPerspectiveHandler,
        transform: PerspectiveDiscovery.ExtractFromHandler)
      .Where(p => p is not null);

    // Or discover from DbSet<PerspectiveRow<TModel>> properties
    var dbSetPerspectives = context.SyntaxProvider
      .CreateSyntaxProvider(
        predicate: PerspectiveDiscovery.IsPotentialDbSetProperty,
        transform: PerspectiveDiscovery.ExtractFromDbSet)
      .Where(p => p is not null);
  }
}
```

**DbContextDiscovery** - Discovers DbContext classes:
```csharp
var dbContexts = context.SyntaxProvider
  .CreateSyntaxProvider(
    predicate: DbContextDiscovery.IsPotentialDbContext,
    transform: DbContextDiscovery.Extract)
  .Where(db => db is not null);
```

### 3. Use Shared Template Utilities

**TemplateUtilities** - Load and manipulate templates:
```csharp
using Whizbang.Generators.Shared.Utilities;

// Get embedded template
var template = TemplateUtilities.GetEmbeddedTemplate(
  assembly: typeof(YourDbGenerator).Assembly,
  templateName: "DbContextExtensions.cs",
  resourceNamespace: "Whizbang.Data.YourDb.Generators.Templates"
);

// Replace header region with timestamp
template = TemplateUtilities.ReplaceHeaderRegion(template);

// Replace #region DBSET_PROPERTIES with generated code
var dbSetCode = GenerateDbSetProperties(perspectives);
template = TemplateUtilities.ReplaceRegion(
  template: template,
  regionName: "DBSET_PROPERTIES",
  replacement: dbSetCode
);

// Extract snippet from template for reuse
var snippet = TemplateUtilities.ExtractSnippet(
  assembly: typeof(YourDbGenerator).Assembly,
  templateName: "Snippets.cs",
  regionName: "UPSERT_METHOD",
  resourceNamespace: "Whizbang.Data.YourDb.Generators.Templates"
);
```

## Shared Discovery API Reference

### PerspectiveDiscovery

**Methods**:

- `IsPotentialPerspectiveHandler(SyntaxNode, CancellationToken)` - Fast syntactic predicate for classes with base lists
- `ExtractFromHandler(GeneratorSyntaxContext, CancellationToken)` - Semantic analysis of `IHandlePerspective<TEvent, TState>`
- `IsPotentialDbSetProperty(SyntaxNode, CancellationToken)` - Fast syntactic predicate for properties
- `ExtractFromDbSet(GeneratorSyntaxContext, CancellationToken)` - Semantic analysis of `DbSet<PerspectiveRow<TModel>>`

**Returns**: `PerspectiveInfo?` sealed record:
```csharp
internal sealed record PerspectiveInfo(
  string? HandlerType,       // Nullable when discovered from DbSet
  string? EventType,         // Nullable when discovered from DbSet
  string StateType,          // Always present (the model type)
  string TableName,          // snake_case from type name or property name
  string? StreamKeyType      // For aggregate perspectives
);
```

### DbContextDiscovery

**Methods**:

- `IsPotentialDbContext(SyntaxNode, CancellationToken)` - Fast syntactic predicate for classes with base lists
- `Extract(GeneratorSyntaxContext, CancellationToken)` - Walks inheritance chain to find `DbContext`, extracts existing perspective DbSet properties

**Returns**: `DbContextInfo?` sealed record:
```csharp
internal sealed record DbContextInfo(
  string ClassName,                           // e.g., "MyDbContext"
  string FullyQualifiedName,                  // e.g., "global::MyApp.Data.MyDbContext"
  string Namespace,                           // e.g., "MyApp.Data"
  ImmutableArray<string> ExistingPerspectives, // Don't generate DbSet for these
  Location Location                            // For diagnostic reporting
);
```

### TemplateUtilities

**Methods**:

- `GetEmbeddedTemplate(Assembly, string templateName, string resourceNamespace)` - Load embedded template file
- `ReplaceHeaderRegion(string template)` - Replace HEADER region with timestamped auto-generated header
- `ReplaceRegion(string template, string regionName, string replacement)` - Replace #region with generated code, preserve indentation
- `IndentCode(string code, string indentation)` - Indent each line of code
- `ExtractSnippet(Assembly, string templateName, string regionName, string resourceNamespace)` - Extract #region contents from template

## Template Pattern

### Template Structure

Templates are **real C# files** embedded as resources. Use `#region` markers for replacement:

**Templates/DbContextExtensions.cs**:
```csharp
#region HEADER
// Placeholder - will be replaced with auto-generated header
#endregion

namespace Whizbang.Data.YourDb;

public static class DbContextExtensions {
  public static void ConfigureWhizbang(this DbContext context) {
    #region DBSET_PROPERTIES
    // Placeholder - will be replaced with generated DbSet properties
    #endregion

    #region CONFIGURE_MODELS
    // Placeholder - will be replaced with model configuration
    #endregion
  }
}
```

### Snippet Pattern

Use snippets for reusable code blocks:

**Templates/Snippets.cs**:
```csharp
#region UPSERT_METHOD
public async Task UpsertAsync<TModel>(
    PerspectiveRow<TModel> row,
    CancellationToken ct = default) {

  // Your database-specific upsert logic
}
#endregion

#region QUERY_METHOD
public async Task<TModel?> QueryAsync<TModel>(
    string id,
    CancellationToken ct = default) {

  // Your database-specific query logic
}
#endregion
```

Extract and reuse:
```csharp
var upsertSnippet = TemplateUtilities.ExtractSnippet(
  assembly: typeof(YourDbGenerator).Assembly,
  templateName: "Snippets.cs",
  regionName: "UPSERT_METHOD",
  resourceNamespace: "Whizbang.Data.YourDb.Generators.Templates"
);

// Use snippet in generated code
var implementation = template.Replace("/* UPSERT_IMPL */", upsertSnippet);
```

## Generator Pipeline Pattern

Recommended incremental generator structure:

```csharp
[Generator]
public class YourDbInfrastructureGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {

    // 1. Discover perspectives (shared discovery)
    var perspectives = context.SyntaxProvider
      .CreateSyntaxProvider(
        predicate: PerspectiveDiscovery.IsPotentialPerspectiveHandler,
        transform: PerspectiveDiscovery.ExtractFromHandler)
      .Where(p => p is not null);

    // 2. Discover DbContexts (shared discovery)
    var dbContexts = context.SyntaxProvider
      .CreateSyntaxProvider(
        predicate: DbContextDiscovery.IsPotentialDbContext,
        transform: DbContextDiscovery.Extract)
      .Where(db => db is not null);

    // 3. Combine and collect
    var combined = perspectives.Collect()
      .Combine(dbContexts.Collect());

    // 4. Generate code
    context.RegisterSourceOutput(combined, (spc, source) => {
      var (perspectiveList, dbContextList) = source;

      if (perspectiveList.IsDefaultOrEmpty || dbContextList.IsDefaultOrEmpty) {
        return;
      }

      foreach (var dbContext in dbContextList) {
        // Filter out perspectives that already have DbSet properties
        var newPerspectives = perspectiveList
          .Where(p => !dbContext.ExistingPerspectives.Contains(p!.StateType))
          .ToList();

        // Generate DbContext partial class extension
        var code = GenerateDbContextExtension(dbContext, newPerspectives);
        spc.AddSource($"{dbContext.ClassName}.g.cs", code);
      }

      // Generate infrastructure implementations
      GenerateEventStore(spc);
      GenerateInbox(spc);
      GenerateOutbox(spc);
      GeneratePerspectiveStore(spc);
    });
  }

  private void GenerateDbContextExtension(
      DbContextInfo dbContext,
      List<PerspectiveInfo?> perspectives) {

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
      assembly: typeof(YourDbInfrastructureGenerator).Assembly,
      templateName: "DbContextExtensions.cs",
      resourceNamespace: "Whizbang.Data.YourDb.Generators.Templates"
    );

    // Replace header
    template = TemplateUtilities.ReplaceHeaderRegion(template);

    // Generate DbSet properties
    var dbSetCode = string.Join("\n", perspectives
      .Select(p => $"public DbSet<PerspectiveRow<{p!.StateType}>> {p.TableName} {{ get; set; }}")
    );

    // Replace region
    template = TemplateUtilities.ReplaceRegion(
      template: template,
      regionName: "DBSET_PROPERTIES",
      replacement: dbSetCode
    );

    return template;
  }
}
```

## Best Practices

### 1. Use Value Records for Caching

The shared library uses **sealed records** for all discovery info. This enables incremental generator caching:

```csharp
// ✅ GOOD: Value equality enables caching
internal sealed record MyDiscoveryInfo(string Name, string Type);

// ❌ BAD: Reference equality breaks caching
internal class MyDiscoveryInfo {
  public string Name { get; set; }
  public string Type { get; set; }
}
```

### 2. Two-Stage Discovery (Syntactic → Semantic)

Use fast syntactic predicates before expensive semantic analysis:

```csharp
// Stage 1: Fast syntactic check (no semantic model)
predicate: (node, ct) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 }

// Stage 2: Semantic analysis (expensive)
transform: (context, ct) => {
  var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node, ct);
  // ... check interfaces, base types, etc.
}
```

### 3. Preserve Indentation in Templates

The shared `TemplateUtilities.ReplaceRegion()` automatically preserves indentation:

```csharp
// Template:
  #region METHODS
  // placeholder
  #endregion

// Replacement:
var methods = """
public void Foo() {
  DoSomething();
}
""";

// Result (2-space indent preserved):
  public void Foo() {
    DoSomething();
  }
```

### 4. Embed Templates as Resources

```xml
<ItemGroup>
  <EmbeddedResource Include="Templates/**/*.cs" />
</ItemGroup>
```

Resource name format: `{RootNamespace}.{RelativePath}`

Example: `Whizbang.Data.YourDb.Generators.Templates.DbContextExtensions.cs`

### 5. Avoid Duplicate Generation

Check `DbContextInfo.ExistingPerspectives` to avoid generating DbSet properties that users already defined:

```csharp
var newPerspectives = perspectives
  .Where(p => !dbContext.ExistingPerspectives.Contains(p!.StateType))
  .ToList();
```

## Examples

See existing generators for reference:

- **Whizbang.Generators** (Core) - Dispatcher, receptors, perspective routing
- **Whizbang.Data.EFCore.Postgres.Generators** - EF Core with PostgreSQL JSONB columns

## Troubleshooting

### Generator Not Running

1. Check `IsRoslynComponent=true` in .csproj
2. Verify `EnforceExtendedAnalyzerRules=true`
3. Ensure target is `netstandard2.0`
4. Clean and rebuild: `dotnet clean && dotnet build`

### Template Not Found

1. Verify `<EmbeddedResource Include="Templates/**/*.cs" />` in .csproj
2. Check resource name matches: `{Namespace}.{FileName}`
3. Use `Assembly.GetManifestResourceNames()` to debug

### Discovery Returns Null

1. Ensure syntactic predicate is not too strict (false positives OK, false negatives BAD)
2. Check fully-qualified type names match expected format
3. Verify semantic model is available in transform

### Performance Issues

1. Use syntactic predicates to filter before semantic analysis
2. Use sealed records for value equality (enables caching)
3. Avoid allocations in hot paths
4. Profile with `dotnet-trace` if needed

## Support

- **Documentation**: See `/docs` folder for detailed specifications
- **Examples**: See existing generators in `src/` directory
- **Issues**: Report bugs at GitHub repository
- **Questions**: Open a discussion in GitHub Discussions

## License

All database-specific generators must be compatible with Whizbang's MIT license.
