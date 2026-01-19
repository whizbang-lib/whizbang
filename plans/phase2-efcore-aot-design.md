# Phase 2: EF Core AOT Support - Detailed Design Document

**Status**: ✅ COMPLETED
**Version**: 0.1.0
**Date**: 2025-12-14
**Author**: Claude Code + Phil Carbone
**Completed**: 2025-12-14

---

## Completion Summary

**Phase 2 is complete!** All objectives achieved with comprehensive test coverage and real-world validation.

### Final Results
- **Status**: All success criteria met ✅
- **Schema Tests**: 10/10 passing (100%)
- **Generator Tests**: 19/19 passing (100%)
- **Integration Tests**: 7/7 passing (100%)
- **ECommerce Sample**: All tests passing (2 integration, 5 unit) ✅
- **Total Tests**: 36/36 passing (100%)

### Implementation Highlights
1. ✅ **Embedded Resource Pattern**: Core schema pre-generated to `Resources/CoreInfrastructureSchema.sql` (5,327 bytes)
2. ✅ **No Reflection**: Generator reads from embedded resource, fully AOT-compatible
3. ✅ **Complete Schema**: All 9 core tables + stream_id/scope columns
4. ✅ **Proper Escaping**: SQL properly escaped for `ExecuteSqlRawAsync`
5. ✅ **Composite PKs**: Fixed perspective_checkpoints primary key
6. ✅ **Migration Compatibility**: Fixed bitwise operators, all migrations execute successfully
7. ✅ **End-to-End Integration Tests**: Comprehensive workflow validation
8. ✅ **Dogfooding Validation**: ECommerce sample confirms real-world usability

### Commits
1. `feat: Implement AOT-compatible core infrastructure schema generation`
2. `fix: Add stream_id and scope columns to event_store, fix test expectations`
3. `test: Add generator tests for AOT-compatible schema generation`
4. `test: Add end-to-end schema initialization integration tests`

### What Changed from Original Design
- **Embedded Resource vs Reflection**: Instead of loading schema builder via reflection at generation time, we pre-generate the SQL file and embed it as a resource. This is MORE AOT-friendly.
- **Schema Fixes**: Added missing `stream_id` and `scope` columns to event_store table for migration compatibility.
- **Test Coverage**:
  - Added 5 generator tests for AOT schema generation (Step 10)
  - Added 7 integration tests for end-to-end workflow validation (Step 11)
  - Validated with ECommerce sample (Step 12)

### Dogfooding Success
All ECommerce sample projects using the Phase 2 EF Core AOT schema generation:
- ✅ ECommerce.IntegrationTests: 2/2 passing
- ✅ ECommerce.OrderService.Tests: 5/5 passing
- ✅ All services (OrderService.API, NotificationWorker, PaymentWorker, ShippingWorker, InventoryWorker, BFF.API) successfully build with generated schema

---

## Table of Contents

1. [Overview](#overview)
2. [Current State](#current-state)
3. [Proposed Architecture](#proposed-architecture)
4. [Implementation Plan](#implementation-plan)
5. [Composite PK and FK Strategy](#composite-pk-and-fk-strategy)
6. [Code Generation Flow](#code-generation-flow)
7. [Migration Strategy](#migration-strategy)
8. [Testing Strategy](#testing-strategy)
9. [Risks and Mitigations](#risks-and-mitigations)
10. [Alternative Approaches](#alternative-approaches)

---

## Overview

Phase 2 replaces EF Core's runtime `GenerateCreateScript()` (not AOT-compatible) with pre-generated SQL schema that ships with the library. This enables Native AOT compilation for applications using Whizbang.

**Key Goals**:
1. Remove `GenerateCreateScript()` dependency (IL3050 warning)
2. Use `PostgresSchemaBuilder.BuildInfrastructureSchema()` for core tables
3. Generate perspective table DDL at build time (source generator)
4. Execute schema via `ExecuteSqlRawAsync()` (AOT-compatible)
5. Add manual CONSTRAINT syntax for composite PKs and foreign keys

---

## Current State

### What Works
- ✅ Phase 1 complete: All 9 schema definitions created
- ✅ `PostgresSchemaBuilder.BuildInfrastructureSchema()` generates DDL for core tables
- ✅ EF Core generator discovers `PerspectiveRow<TModel>` types
- ✅ Generator creates `DbSet<PerspectiveRow<TModel>>` properties
- ✅ PostgreSQL function migrations embedded as string constants

### What Needs Changing

**Current Flow** (NOT AOT-compatible):
```csharp
// DbContextSchemaExtensionTemplate.cs line 48
#pragma warning disable IL3050
var script = dbContext.Database.GenerateCreateScript();
#pragma warning restore IL3050

script = MakeScriptIdempotent(script);
await dbContext.Database.ExecuteSqlRawAsync(script, cancellationToken);
```

**Problems**:
1. `GenerateCreateScript()` requires reflection/dynamic code
2. Cannot compile to Native AOT
3. Warns IL3050 in consuming applications

---

## Proposed Architecture

### New Schema Initialization Flow

```csharp
public static async Task EnsureWhizbangDatabaseInitializedAsync(...) {
  // Step 1: Create core infrastructure tables (pre-generated SQL)
  logger?.LogInformation("Creating Whizbang core infrastructure tables...");
  await ExecuteCoreInfrastructureSchemaAsync(dbContext, logger, cancellationToken);

  // Step 2: Create perspective tables (generated at build time)
  logger?.LogInformation("Creating perspective tables...");
  await ExecutePerspectiveTablesAsync(dbContext, logger, cancellationToken);

  // Step 3: Create PostgreSQL functions (already embedded)
  logger?.LogInformation("Creating PostgreSQL functions...");
  await ExecuteMigrationsAsync(dbContext, logger, cancellationToken);

  logger?.LogInformation("Whizbang database initialization complete");
}
```

### Three-Tier Schema System

#### Tier 1: Core Infrastructure Schema (Static, Pre-generated)
**Source**: `PostgresSchemaBuilder.BuildInfrastructureSchema()`
**Generated by**: EF Core source generator calls schema builder
**Embedded as**: String constant in generated code
**Tables**: 9 core infrastructure tables (inbox, outbox, event_store, etc.)

```csharp
private static async Task ExecuteCoreInfrastructureSchemaAsync(...) {
  // SQL embedded by source generator
  const string CoreInfrastructureSchema = @"
    -- Generated by PostgresSchemaBuilder.BuildInfrastructureSchema()
    CREATE TABLE IF NOT EXISTS wh_service_instances (...);
    CREATE TABLE IF NOT EXISTS wh_message_deduplication (...);
    -- ... all 9 tables
  ";

  try {
    await dbContext.Database.ExecuteSqlRawAsync(CoreInfrastructureSchema, cancellationToken);
    logger?.LogInformation("Core infrastructure tables created");
  } catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07") {
    logger?.LogWarning("Table already exists: {Table}", ex.TableName);
  }
}
```

#### Tier 2: Perspective Tables (Dynamic, Build-time Generated)
**Source**: Discovered `PerspectiveRow<TModel>` types
**Generated by**: EF Core source generator inspects model properties
**Embedded as**: String constant in generated code
**Tables**: One table per `TModel` type (e.g., `wh_per_product_dto`)

```csharp
private static async Task ExecutePerspectiveTablesAsync(...) {
  // SQL generated at build time from discovered PerspectiveRow<TModel> types
  const string PerspectiveTablesSchema = @"
    CREATE TABLE IF NOT EXISTS wh_per_product_dto (...);
    CREATE TABLE IF NOT EXISTS wh_per_order_summary (...);
    -- ... one per discovered perspective
  ";

  if (string.IsNullOrWhiteSpace(PerspectiveTablesSchema)) {
    logger?.LogInformation("No perspective tables to create");
    return;
  }

  try {
    await dbContext.Database.ExecuteSqlRawAsync(PerspectiveTablesSchema, cancellationToken);
    logger?.LogInformation("Perspective tables created");
  } catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07") {
    logger?.LogWarning("Table already exists: {Table}", ex.TableName);
  }
}
```

#### Tier 3: PostgreSQL Functions (Static, Embedded)
**Source**: SQL files in `Whizbang.Data.Postgres/Migrations/`
**Generated by**: Existing generator logic (no changes)
**Embedded as**: Array of (name, sql) tuples
**Functions**: `process_work_batch`, `acquire_receptor_processing`, etc.

```csharp
// No changes to this method - already AOT-compatible
private static async Task ExecuteMigrationsAsync(...) {
  var migrations = GetMigrationScripts();
  foreach (var (name, sql) in migrations) {
    await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
  }
}
```

---

## Implementation Plan

### Step 1: Update EFCoreServiceRegistrationGenerator.cs

**File**: `src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs`

**Changes in `GenerateSchemaExtensions()` method**:

```csharp
private static void GenerateSchemaExtensions(...) {
  // Existing code...

  // NEW: Generate core infrastructure schema SQL
  string coreInfrastructureSchema = GenerateCoreInfrastructureSchema(context);

  // NEW: Generate perspective tables schema SQL
  string perspectiveTablesSchema = GeneratePerspectiveTablesSchema(context, matchingPerspectives);

  // Existing template loading...
  var template = templateBase;

  // NEW: Replace CORE_INFRASTRUCTURE_SCHEMA region
  template = TemplateUtilities.ReplaceRegion(
    template,
    "CORE_INFRASTRUCTURE_SCHEMA",
    coreInfrastructureSchema
  );

  // NEW: Replace PERSPECTIVE_TABLES_SCHEMA region
  template = TemplateUtilities.ReplaceRegion(
    template,
    "PERSPECTIVE_TABLES_SCHEMA",
    perspectiveTablesSchema
  );

  // Existing MIGRATIONS region replacement...
  template = TemplateUtilities.ReplaceRegion(
    template,
    "MIGRATIONS",
    migrationsCode
  );

  // Existing placeholder replacements...
}
```

**Add new helper method**:

```csharp
/// <summary>
/// Generates core infrastructure schema SQL by calling PostgresSchemaBuilder.
/// This requires loading Whizbang.Data.Dapper.Postgres assembly at generator compile time.
/// </summary>
private static string GenerateCoreInfrastructureSchema(SourceProductionContext context) {
  try {
    // Load schema builder assembly
    var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
    var dapperAssembly = loadedAssemblies.FirstOrDefault(
      a => a.GetName().Name == "Whizbang.Data.Dapper.Postgres"
    );

    if (dapperAssembly is null) {
      // Report warning and return fallback
      var descriptor = new DiagnosticDescriptor(
        id: "EFCORE201",
        title: "Schema Builder Not Available",
        messageFormat: "Could not load Whizbang.Data.Dapper.Postgres assembly. Core schema will not be pre-generated.",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
      );
      context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None));
      return "// Schema builder not available - core tables must be created manually";
    }

    // Get SchemaConfiguration type
    var configType = dapperAssembly.GetType("Whizbang.Data.Schema.SchemaConfiguration");
    var builderType = dapperAssembly.GetType("Whizbang.Data.Dapper.Postgres.Schema.PostgresSchemaBuilder");

    if (configType is null || builderType is null) {
      return "// Schema types not found";
    }

    // Create SchemaConfiguration instance (prefix: "wh_")
    var config = Activator.CreateInstance(configType, "wh_", "wb_per_", "public", 1);

    // Call PostgresSchemaBuilder.BuildInfrastructureSchema(config)
    var buildMethod = builderType.GetMethod(
      "BuildInfrastructureSchema",
      System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
    );

    if (buildMethod is null) {
      return "// BuildInfrastructureSchema method not found";
    }

    var sql = (string)buildMethod.Invoke(null, new[] { config })!;

    // Escape for C# verbatim string literal
    sql = sql
      .Replace("\"", "\"\"")  // Escape quotes
      .Replace("{", "{{")     // Escape braces for ExecuteSqlRawAsync
      .Replace("}", "}}");

    return $"@\"{sql}\"";

  } catch (Exception ex) {
    var descriptor = new DiagnosticDescriptor(
      id: "EFCORE202",
      title: "Schema Generation Failed",
      messageFormat: "Failed to generate core infrastructure schema: {0}",
      category: "Whizbang.Generator",
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true
    );
    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, ex.Message));
    return $"// Error generating schema: {ex.Message}";
  }
}
```

**Add perspective table generation method**:

```csharp
/// <summary>
/// Generates CREATE TABLE statements for perspective tables.
/// Inspects PerspectiveRow&lt;TModel&gt; properties to determine columns.
/// </summary>
private static string GeneratePerspectiveTablesSchema(
  SourceProductionContext context,
  List<PerspectiveModelInfo> perspectives
) {
  if (perspectives.Count == 0) {
    return "\"\""; // Empty string - no perspective tables
  }

  var sb = new StringBuilder();
  sb.AppendLine("-- Perspective Tables (auto-generated from PerspectiveRow<TModel> types)");

  // Get unique models (same model might be used by multiple perspectives)
  var uniqueModels = perspectives
    .GroupBy(p => p.TableName)
    .Select(g => g.First())
    .ToList();

  foreach (var model in uniqueModels) {
    // PerspectiveRow<TModel> has fixed schema:
    // - StreamId (UUID PK)
    // - Data (JSONB - serialized TModel)
    // - Version (BIGINT)
    // - UpdatedAt (TIMESTAMPTZ)

    sb.AppendLine($"CREATE TABLE IF NOT EXISTS {model.TableName} (");
    sb.AppendLine($"  stream_id UUID NOT NULL PRIMARY KEY,");
    sb.AppendLine($"  data JSONB NOT NULL,");
    sb.AppendLine($"  version BIGINT NOT NULL,");
    sb.AppendLine($"  updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP");
    sb.AppendLine($");");
    sb.AppendLine();

    // Add index on updated_at for queries
    sb.AppendLine($"CREATE INDEX IF NOT EXISTS idx_{model.TableName}_updated_at ON {model.TableName} (updated_at);");
    sb.AppendLine();
  }

  // Escape for C# verbatim string
  var sql = sb.ToString()
    .Replace("\"", "\"\"")
    .Replace("{", "{{")
    .Replace("}", "}}");

  return $"@\"{sql}\"";
}
```

### Step 2: Update DbContextSchemaExtensionTemplate.cs

**File**: `src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs`

**Replace entire `EnsureWhizbangDatabaseInitializedAsync()` method**:

```csharp
public static async Task EnsureWhizbangDatabaseInitializedAsync(
  this __DBCONTEXT_FQN__ dbContext,
  ILogger? logger = null,
  CancellationToken cancellationToken = default) {

  // Step 1: Create core infrastructure tables (pre-generated SQL from PostgresSchemaBuilder)
  logger?.LogInformation("Creating Whizbang core infrastructure tables for {DbContext}...", "__DBCONTEXT_CLASS__");
  await ExecuteCoreInfrastructureSchemaAsync(dbContext, logger, cancellationToken);

  // Step 2: Create perspective tables (generated at build time from discovered PerspectiveRow<TModel> types)
  logger?.LogInformation("Creating perspective tables for {DbContext}...", "__DBCONTEXT_CLASS__");
  await ExecutePerspectiveTablesAsync(dbContext, logger, cancellationToken);

  // Step 3: Create PostgreSQL functions (process_work_batch, etc.)
  logger?.LogInformation("Creating PostgreSQL functions for {DbContext}...", "__DBCONTEXT_CLASS__");
  await ExecuteMigrationsAsync(dbContext, logger, cancellationToken);

  logger?.LogInformation("Whizbang database initialization complete for {DbContext}", "__DBCONTEXT_CLASS__");
}
```

**Add new method for core infrastructure**:

```csharp
/// <summary>
/// Executes pre-generated core infrastructure schema.
/// SQL is embedded at build time from PostgresSchemaBuilder.BuildInfrastructureSchema().
/// Creates 9 core tables: service_instances, message_deduplication, inbox, outbox,
/// event_store, receptor_processing, perspective_checkpoints, request_response, sequences.
/// AOT-compatible - no reflection or dynamic code generation.
/// </summary>
private static async Task ExecuteCoreInfrastructureSchemaAsync(
  __DBCONTEXT_FQN__ dbContext,
  ILogger? logger,
  CancellationToken cancellationToken) {

  // SQL embedded by source generator from PostgresSchemaBuilder
  const string CoreInfrastructureSchema = #region CORE_INFRASTRUCTURE_SCHEMA
  // Schema SQL will be embedded here by the source generator
  #endregion;

  if (string.IsNullOrWhiteSpace(CoreInfrastructureSchema)) {
    logger?.LogWarning("Core infrastructure schema is empty - schema builder may not be available");
    return;
  }

  try {
    await dbContext.Database.ExecuteSqlRawAsync(CoreInfrastructureSchema, cancellationToken);
    logger?.LogInformation("Core infrastructure tables created successfully");
  } catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07") {
    // 42P07 = duplicate_table (expected if tables already exist)
    logger?.LogInformation("Core infrastructure tables already exist (expected): {Table}", ex.TableName ?? "unknown");
  } catch (Exception ex) {
    logger?.LogError(ex, "Failed to create core infrastructure tables");
    throw;
  }
}
```

**Add new method for perspective tables**:

```csharp
/// <summary>
/// Executes perspective table DDL generated at build time.
/// SQL is generated by discovering PerspectiveRow&lt;TModel&gt; types in user code.
/// Creates tables with schema: stream_id (UUID PK), data (JSONB), version (BIGINT), updated_at (TIMESTAMPTZ).
/// AOT-compatible - no reflection or dynamic code generation.
/// </summary>
private static async Task ExecutePerspectiveTablesAsync(
  __DBCONTEXT_FQN__ dbContext,
  ILogger? logger,
  CancellationToken cancellationToken) {

  // SQL embedded by source generator from discovered PerspectiveRow<TModel> types
  const string PerspectiveTablesSchema = #region PERSPECTIVE_TABLES_SCHEMA
  // Perspective table DDL will be embedded here by the source generator
  #endregion;

  if (string.IsNullOrWhiteSpace(PerspectiveTablesSchema)) {
    logger?.LogInformation("No perspective tables to create (DbContext has no perspectives)");
    return;
  }

  try {
    await dbContext.Database.ExecuteSqlRawAsync(PerspectiveTablesSchema, cancellationToken);
    logger?.LogInformation("Perspective tables created successfully");
  } catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07") {
    // 42P07 = duplicate_table (expected if tables already exist)
    logger?.LogInformation("Perspective tables already exist (expected): {Table}", ex.TableName ?? "unknown");
  } catch (Exception ex) {
    logger?.LogError(ex, "Failed to create perspective tables");
    throw;
  }
}
```

**Remove these methods** (no longer needed):
- `MakeScriptIdempotent()` - schema builder already adds IF NOT EXISTS

---

## Composite PK and FK Strategy

### Problem
Phase 1 schema definitions have limitations:
1. **Composite Primary Keys**: PerspectiveCheckpointsSchema needs PK on (stream_id, perspective_name)
2. **Foreign Keys**: ReceptorProcessingSchema and PerspectiveCheckpointsSchema need FK to event_store
3. **Unique Constraints**: ReceptorProcessingSchema needs UNIQUE (event_id, receptor_name)

### Solution Options

#### Option A: Extend TableDefinition (Future)
Add support to `TableDefinition` for:
- Composite primary keys
- Foreign key constraints
- Unique constraints across multiple columns

**Pros**: Clean, type-safe, reusable
**Cons**: More work, out of scope for Phase 2
**Decision**: Defer to future version

#### Option B: Post-Process Generated SQL (Chosen for Phase 2)
Add manual CONSTRAINT statements after core infrastructure schema.

**Implementation**:

```csharp
private static async Task ExecuteCoreInfrastructureSchemaAsync(...) {
  // Step 1: Execute core infrastructure schema
  await dbContext.Database.ExecuteSqlRawAsync(CoreInfrastructureSchema, cancellationToken);

  // Step 2: Add constraints that TableDefinition doesn't support yet
  await ExecuteConstraintsAsync(dbContext, logger, cancellationToken);
}

private static async Task ExecuteConstraintsAsync(...) {
  const string Constraints = @"
    -- Composite PK for perspective_checkpoints (stream_id, perspective_name)
    ALTER TABLE wh_perspective_checkpoints
      DROP CONSTRAINT IF EXISTS wh_perspective_checkpoints_pkey CASCADE;

    ALTER TABLE wh_perspective_checkpoints
      ADD CONSTRAINT pk_perspective_checkpoints PRIMARY KEY (stream_id, perspective_name);

    -- Foreign keys
    ALTER TABLE wh_receptor_processing
      ADD CONSTRAINT IF NOT EXISTS fk_receptor_processing_event
      FOREIGN KEY (event_id) REFERENCES wh_event_store(event_id) ON DELETE CASCADE;

    ALTER TABLE wh_perspective_checkpoints
      ADD CONSTRAINT IF NOT EXISTS fk_perspective_checkpoints_event
      FOREIGN KEY (last_event_id) REFERENCES wh_event_store(event_id) ON DELETE RESTRICT;

    -- Unique constraint for receptor_processing
    CREATE UNIQUE INDEX IF NOT EXISTS uq_receptor_processing_event_receptor
      ON wh_receptor_processing(event_id, receptor_name);
  ";

  try {
    await dbContext.Database.ExecuteSqlRawAsync(Constraints, cancellationToken);
    logger?.LogInformation("Constraints added successfully");
  } catch (Exception ex) {
    logger?.LogWarning(ex, "Failed to add constraints (may already exist)");
    // Don't throw - constraints might already exist
  }
}
```

**Note**: PostgreSQL doesn't support `ADD CONSTRAINT IF NOT EXISTS` for foreign keys, so we catch exceptions.

---

## Code Generation Flow

```mermaid
graph TD
    A[User builds project] --> B[EFCoreServiceRegistrationGenerator runs]
    B --> C[Discover DbContext classes]
    B --> D[Discover PerspectiveRow types]

    C --> E[Load Whizbang.Data.Dapper.Postgres assembly]
    E --> F[Call PostgresSchemaBuilder.BuildInfrastructureSchema]
    F --> G[Get core infrastructure SQL]

    D --> H[Inspect PerspectiveRow types]
    H --> I[Generate perspective table DDL]

    G --> J[Embed SQL in template CORE_INFRASTRUCTURE_SCHEMA region]
    I --> K[Embed SQL in template PERSPECTIVE_TABLES_SCHEMA region]

    J --> L[Generate DbContext_SchemaExtensions.g.cs]
    K --> L

    L --> M[Compile consuming application]
    M --> N[Application calls EnsureWhizbangDatabaseInitializedAsync]
    N --> O[Execute core infrastructure schema]
    O --> P[Execute perspective tables schema]
    P --> Q[Execute PostgreSQL functions]
    Q --> R[Database ready]
```

---

## Migration Strategy

### Backward Compatibility

**Problem**: Existing databases created with `GenerateCreateScript()` have tables already.

**Solution**: All SQL uses `CREATE TABLE IF NOT EXISTS` and `CREATE INDEX IF NOT EXISTS`.

**Verification**:
1. Test on fresh database (no tables) → should create all tables
2. Test on existing database (tables exist) → should skip creation, no errors
3. Test on partial database (some tables missing) → should create missing tables

### Version Management

**Problem**: Schema changes over time (new columns, new tables).

**Solution** (Deferred to Phase 3):
- Add schema version table (`wh_schema_version`)
- Check version before applying schema
- Support incremental migrations

**For Phase 2**: Assume fresh database or existing v0.1.0 schema.

---

## Testing Strategy

### Unit Tests

**New Test File**: `tests/Whizbang.Data.Schema.Tests/SchemaDefinitionTests.cs`

```csharp
[Test]
public async Task ServiceInstancesSchema_GeneratesValidSql() {
  var config = new SchemaConfiguration(InfrastructurePrefix: "wh_");
  var sql = PostgresSchemaBuilder.BuildCreateTable(ServiceInstancesSchema.Table, config.InfrastructurePrefix);

  await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS wh_service_instances");
  await Assert.That(sql).Contains("instance_id UUID NOT NULL PRIMARY KEY");
  await Assert.That(sql).Contains("last_heartbeat_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP");
}

[Test]
public async Task BuildInfrastructureSchema_IncludesAllNineTables() {
  var config = new SchemaConfiguration(InfrastructurePrefix: "wh_");
  var sql = PostgresSchemaBuilder.BuildInfrastructureSchema(config);

  await Assert.That(sql).Contains("wh_service_instances");
  await Assert.That(sql).Contains("wh_message_deduplication");
  await Assert.That(sql).Contains("wh_inbox");
  await Assert.That(sql).Contains("wh_outbox");
  await Assert.That(sql).Contains("wh_event_store");
  await Assert.That(sql).Contains("wh_receptor_processing");
  await Assert.That(sql).Contains("wh_perspective_checkpoints");
  await Assert.That(sql).Contains("wh_request_response");
  await Assert.That(sql).Contains("wh_sequences");
}
```

### Generator Tests

**New Test File**: `tests/Whizbang.Data.EFCore.Postgres.Generators.Tests/AotSchemaGenerationTests.cs`

```csharp
[Test]
public async Task Generator_EmbedsNonEmptyCoreInfrastructureSchema() {
  var generatedSource = RunGenerator();

  await Assert.That(generatedSource).Contains("ExecuteCoreInfrastructureSchemaAsync");
  await Assert.That(generatedSource).Contains("const string CoreInfrastructureSchema");
  await Assert.That(generatedSource).Contains("CREATE TABLE IF NOT EXISTS wh_service_instances");
}

[Test]
public async Task Generator_EmbedsNonEmptyPerspectiveTablesSchema_WhenPerspectivesExist() {
  var generatedSource = RunGeneratorWithPerspectives();

  await Assert.That(generatedSource).Contains("ExecutePerspectiveTablesAsync");
  await Assert.That(generatedSource).Contains("const string PerspectiveTablesSchema");
  await Assert.That(generatedSource).Contains("CREATE TABLE IF NOT EXISTS wh_per_");
}
```

### Integration Tests

**New Test File**: `tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationTests.cs`

```csharp
[Test]
public async Task EnsureWhizbangDatabaseInitialized_CreatesCoreInfrastructureTables() {
  // Arrange
  var dbContext = CreateTestDbContext();
  await DropAllTablesAsync(dbContext);

  // Act
  await dbContext.EnsureWhizbangDatabaseInitializedAsync();

  // Assert
  var tables = await GetTableNamesAsync(dbContext);
  await Assert.That(tables).Contains("wh_service_instances");
  await Assert.That(tables).Contains("wh_message_deduplication");
  // ... all 9 tables
}

[Test]
public async Task EnsureWhizbangDatabaseInitialized_IsIdempotent() {
  // Arrange
  var dbContext = CreateTestDbContext();

  // Act
  await dbContext.EnsureWhizbangDatabaseInitializedAsync();
  await dbContext.EnsureWhizbangDatabaseInitializedAsync(); // Call twice

  // Assert - no exceptions thrown
  await Assert.That(true).IsTrue();
}
```

### End-to-End AOT Test

**New Test File**: `tests/ECommerce.Integration.Tests/AotPublishTests.cs`

Uses Testcontainers to:
1. Spin up PostgreSQL container
2. Run `dotnet publish -c Release` on ECommerce.BFF.API
3. Execute EnsureWhizbangDatabaseInitializedAsync()
4. Verify all tables created
5. Run basic CRUD operations

---

## Risks and Mitigations

### Risk 1: Generator Can't Load Schema Builder Assembly

**Impact**: Core infrastructure schema won't be embedded, deployment breaks.

**Mitigation**:
- Add project reference from `Whizbang.Data.EFCore.Postgres.Generators` to `Whizbang.Data.Dapper.Postgres`
- Generator diagnostic (EFCORE201) warns if assembly not found
- Fallback: Include schema SQL as embedded resource in generator

### Risk 2: Composite PK Constraints Fail on Existing Databases

**Impact**: `ALTER TABLE DROP CONSTRAINT` might fail if constraint doesn't exist.

**Mitigation**:
- Use `DROP CONSTRAINT IF EXISTS`
- Catch and log exceptions (don't fail deployment)
- PostgreSQL supports IF EXISTS since version 9.2

### Risk 3: PerspectiveRow Schema Changes

**Impact**: Future schema changes to PerspectiveRow require migration.

**Mitigation**:
- Document PerspectiveRow schema in code comments
- Add schema version column (future)
- Use ALTER TABLE for additive changes (future)

### Risk 4: Performance - Large Schema Strings

**Impact**: Embedding large SQL strings in generated code increases assembly size.

**Mitigation**:
- Current SQL ~2-3 KB per DbContext (negligible)
- If problematic, compress strings (future optimization)

---

## Alternative Approaches

### Alternative 1: Migration Bundles Only (Rejected)

**Approach**: Use EF Core migration bundles exclusively, no schema generation.

**Pros**:
- Official EF Core AOT approach
- Well-documented

**Cons**:
- Requires .NET SDK in production (bundles are executables)
- Complex deployment workflow
- Doesn't support perspective tables (dynamically discovered)
- User must manually create migrations

**Decision**: Rejected - too complex for library users.

---

### Alternative 2: Embedded Binary Schema (Rejected)

**Approach**: Serialize schema as binary protobuf/msgpack, deserialize at runtime.

**Pros**:
- Smaller than SQL strings
- Faster parsing

**Cons**:
- Adds dependency on serialization library
- More complex implementation
- SQL strings are human-readable (better debugging)

**Decision**: Rejected - premature optimization, adds complexity.

---

### Alternative 3: External Schema Files (Rejected)

**Approach**: Ship SQL files alongside DLL, read at runtime.

**Pros**:
- Smaller assembly size
- Easy to edit for debugging

**Cons**:
- File I/O required (not self-contained)
- Deployment complexity (must include .sql files)
- Breaking change if files missing

**Decision**: Rejected - self-contained approach is better.

---

## Open Questions

1. **Should we version the embedded schema?**
   - Add version comment in SQL (`-- Schema Version: 1.0.0`)?
   - Store version in `wh_schema_version` table?
   - **Proposed**: Add comment for now, defer table to Phase 3.

2. **Should constraints be in separate method or inline?**
   - Separate (`ExecuteConstraintsAsync()`) allows skipping if not needed
   - Inline simplifies code
   - **Proposed**: Separate method for clarity.

3. **Should we support custom schema prefixes?**
   - Currently hardcoded `wh_` prefix
   - User might want different prefix
   - **Proposed**: Defer to future - use hardcoded `wh_` for v0.1.0.

4. **How to handle partial index WHERE clauses?**
   - `idx_receptor_processing_status WHERE status & 4 = 4`
   - Not supported in IndexDefinition
   - **Proposed**: Add in `ExecuteConstraintsAsync()` manually.

---

## Success Criteria

Phase 2 is complete when:
1. ✅ `GenerateCreateScript()` removed from template
2. ✅ Core infrastructure schema embedded via PostgresSchemaBuilder
3. ✅ Perspective tables generated at build time
4. ✅ All 9 core tables + perspective tables created successfully
5. ✅ Composite PKs and FKs added via constraints
6. ✅ Integration tests pass (fresh DB and existing DB)
7. ✅ No IL3050 warnings in consuming applications
8. ✅ ECommerce sample builds and runs with AOT

---

## Timeline Estimate

- **Step 1** (Generator changes): 2-3 hours
- **Step 2** (Template changes): 1-2 hours
- **Step 3** (Constraint handling): 1 hour
- **Step 4** (Testing): 2-3 hours
- **Step 5** (Documentation): 1 hour

**Total**: 7-10 hours of focused work

---

## Next Steps

1. Review this design document
2. Approve approach
3. Begin implementation with Step 1 (Generator changes)
4. Commit incrementally (generator → template → constraints → tests)
5. Verify with ECommerce sample

---

**Document Status**: Ready for Review
**Approver**: Phil Carbone
**Implementation**: Proceed on approval
